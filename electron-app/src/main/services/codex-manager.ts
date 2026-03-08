import { spawn } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import BetterSqlite3 from 'better-sqlite3'
import type { RuntimeSyncResult, SessionRecord } from '@shared/contracts'
import {
  copyPathIfExists,
  ensureDir,
  normalizeCwd,
  normalizeProvider,
  normalizeWorkspaceRoot,
  readTextIfExists,
  samePath,
  stripVerbatimPathPrefix,
  toVerbatimPath,
  tryReadTopLevelTomlStringValue,
  writeTextNoBom
} from '../utils/path-utils'
import { runPowerShell } from '../utils/process-utils'

const STATE_PATHS = [
  'sessions',
  'history.jsonl',
  'session_index.jsonl',
  '.codex-global-state.json',
  'state_5.sqlite',
  'state_5.sqlite-shm',
  'state_5.sqlite-wal'
] as const

const SESSION_ID_REGEX = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i

type SessionIndexEntry = {
  title: string
  updatedAt: string
}

type ThreadRow = {
  id: string
  title: string
  cwd: string
  source: string
  model_provider: string
  created_at: number
  updated_at: number
  rollout_path: string
}

type SessionMeta = {
  title: string
  cwd: string
  modelProvider: string
  createdAt: string
}

export class CodexManager {
  get defaultCodexHome(): string {
    return path.join(process.env.USERPROFILE ?? process.env.HOME ?? 'C:\\', '.codex')
  }

  get defaultSharedStoreHome(): string {
    return path.join(process.env.USERPROFILE ?? process.env.HOME ?? 'C:\\', '.codex-shared-store')
  }

  async findCodexAppExecutable(): Promise<string | null> {
    const runningExecutable = await this.findRunningCodexExecutable()
    if (runningExecutable) {
      return runningExecutable
    }

    try {
      const output = await runPowerShell(`
$root = 'C:\\Program Files\\WindowsApps'
if (Test-Path $root) {
  Get-ChildItem -Path $root -Directory -Filter 'OpenAI.Codex_*' -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    ForEach-Object {
      $candidate = Join-Path $_.FullName 'app\\Codex.exe'
      if (Test-Path $candidate) {
        Write-Output $candidate
        break
      }
    }
}
      `)

      const executable = output.trim()
      return executable && fs.existsSync(executable) ? executable : null
    } catch {
      return null
    }
  }

  async isCodexAppRunning(): Promise<boolean> {
    try {
      const output = await runPowerShell(`
$targets = Get-Process Codex -ErrorAction SilentlyContinue | Where-Object {
  try {
    $_.Path -and $_.Path -like '*OpenAI.Codex*'
  } catch {
    $false
  }
}
if (@($targets).Count -gt 0) { 'true' } else { 'false' }
      `)

      return output.trim().toLowerCase() === 'true'
    } catch {
      return false
    }
  }

  async closeRunningCodexApp(): Promise<number> {
    try {
      const output = await runPowerShell(`
$targets = @(Get-Process Codex -ErrorAction SilentlyContinue | Where-Object {
  try {
    $_.Path -and $_.Path -like '*OpenAI.Codex*'
  } catch {
    $false
  }
})
$count = $targets.Count
if ($count -gt 0) {
  $targets | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 300
}
Write-Output $count
      `)

      const count = Number.parseInt(output.trim(), 10)
      return Number.isNaN(count) ? 0 : count
    } catch {
      return 0
    }
  }

  loadSessions(codexHome: string): SessionRecord[] {
    this.validateHomeExists(codexHome, 'codexHome')

    const sessionsRoot = path.join(codexHome, 'sessions')
    const indexById = this.loadSessionIndex(codexHome)
    const dbById = this.loadThreadRows(codexHome)
    const records = new Map<string, SessionRecord>()

    if (fs.existsSync(sessionsRoot)) {
      for (const sessionPath of this.enumerateSessionFiles(sessionsRoot)) {
        const sessionId = this.extractSessionId(sessionPath)
        if (!sessionId) {
          continue
        }

        records.set(sessionId.toLowerCase(), this.buildSessionRecord(sessionPath, sessionId, indexById, dbById))
      }
    }

    for (const [id, entry] of indexById.entries()) {
      const key = id.toLowerCase()
      if (!records.has(key)) {
        records.set(key, {
          id,
          title: entry.title || id,
          cwd: '',
          sessionPath: '',
          updatedAt: this.safeIsoString(entry.updatedAt),
          createdAt: this.safeIsoString(entry.updatedAt),
          source: 'index',
          modelProvider: ''
        })
      }
    }

    for (const [id, row] of dbById.entries()) {
      const key = id.toLowerCase()
      const record = records.get(key) ?? {
        id,
        title: id,
        cwd: '',
        sessionPath: '',
        updatedAt: new Date(0).toISOString(),
        createdAt: new Date(0).toISOString(),
        source: 'sqlite',
        modelProvider: ''
      }

      this.applyThreadRow(record, row)
      records.set(key, record)
    }

    return [...records.values()].sort((left, right) => {
      const rightTime = Date.parse(right.updatedAt)
      const leftTime = Date.parse(left.updatedAt)
      if (rightTime !== leftTime) {
        return rightTime - leftTime
      }

      return left.id.localeCompare(right.id, undefined, { sensitivity: 'accent' })
    })
  }

  prepareSharedWorkspace(
    authFromHome: string | null,
    sharedStoreHome: string,
    runtimeHome: string,
    overwriteRuntimeConfig: boolean
  ): void {
    this.ensureProjectionHomes(sharedStoreHome, runtimeHome)
    this.ensureSharedStoreHome(sharedStoreHome)
    ensureDir(runtimeHome)
    this.ensureRuntimeConfigurationFiles(authFromHome, runtimeHome, overwriteRuntimeConfig)
  }

  importSessionToSharedStoreOnly(
    sourceHome: string,
    sharedStoreHome: string,
    sessionId: string,
    refreshUpdatedAt: boolean,
    addWorkspaceHint: boolean
  ): SessionRecord {
    this.ensureSharedStoreHome(sharedStoreHome)

    const imported = this.importSession(sourceHome, sharedStoreHome, sessionId, refreshUpdatedAt, addWorkspaceHint)
    this.upsertSharedCatalog(sharedStoreHome, imported, sourceHome)
    return imported
  }

  async importSessionToSharedStore(
    sourceHome: string,
    sharedStoreHome: string,
    authFromHome: string | null,
    runtimeHome: string,
    sessionId: string,
    refreshUpdatedAt: boolean,
    addWorkspaceHint: boolean
  ): Promise<RuntimeSyncResult> {
    this.ensureProjectionHomes(sharedStoreHome, runtimeHome)

    const imported = this.importSessionToSharedStoreOnly(
      sourceHome,
      sharedStoreHome,
      sessionId,
      refreshUpdatedAt,
      addWorkspaceHint
    )

    const syncResult = await this.syncRuntimeHome(sharedStoreHome, authFromHome, runtimeHome, true)
    return {
      ...syncResult,
      lastImportedSessionId: imported.id
    }
  }

  async syncRuntimeHome(
    sharedStoreHome: string,
    authFromHome: string | null,
    runtimeHome: string,
    overwriteRuntimeConfig = false
  ): Promise<RuntimeSyncResult> {
    this.ensureProjectionHomes(sharedStoreHome, runtimeHome)
    this.ensureSharedStoreHome(sharedStoreHome)
    ensureDir(runtimeHome)
    this.ensureRuntimeConfigurationFiles(authFromHome, runtimeHome, overwriteRuntimeConfig)

    if (await this.isCodexAppRunning()) {
      throw new Error('Codex 正在运行，请先关闭后再同步运行目录。')
    }

    await this.resetManagedState(runtimeHome)

    for (const relativePath of STATE_PATHS) {
      copyPathIfExists(path.join(sharedStoreHome, relativePath), path.join(runtimeHome, relativePath))
    }

    this.repairTargetHome(runtimeHome, sharedStoreHome)
    const sessions = this.loadSessions(runtimeHome)

    return {
      sharedStoreHome,
      runtimeHome,
      effectiveProvider: this.resolvePreferredModelProvider(runtimeHome),
      sessionCount: sessions.length,
      lastImportedSessionId: ''
    }
  }

  async syncAndLaunchCodexApp(
    sharedStoreHome: string,
    authFromHome: string | null,
    runtimeHome: string,
    appExePath: string | null
  ): Promise<RuntimeSyncResult> {
    const syncResult = await this.syncRuntimeHome(sharedStoreHome, authFromHome, runtimeHome, true)
    await this.launchCodexApp(runtimeHome, appExePath)
    return syncResult
  }

  getEffectiveModelProvider(codexHome: string): string {
    if (!codexHome.trim() || !fs.existsSync(codexHome)) {
      return ''
    }

    return this.resolvePreferredModelProvider(codexHome)
  }

  repairTargetHome(targetHome: string, copiedFromHome: string | null): void {
    this.validateHomeExists(targetHome, 'targetHome')

    const preferredModelProvider = this.resolvePreferredModelProvider(targetHome)
    this.normalizeWorkspaceRoots(targetHome)
    this.normalizeSessionFiles(path.join(targetHome, 'sessions'), preferredModelProvider)
    this.repairThreadRows(targetHome, copiedFromHome, preferredModelProvider)
  }

  readOpenAiApiKey(codexHome: string): string | null {
    const authPath = path.join(codexHome, 'auth.json')
    if (!fs.existsSync(authPath)) {
      return null
    }

    try {
      const parsed = JSON.parse(fs.readFileSync(authPath, 'utf8')) as Record<string, unknown>
      return typeof parsed.OPENAI_API_KEY === 'string' ? parsed.OPENAI_API_KEY : null
    } catch {
      return null
    }
  }

  private async launchCodexApp(codexHome: string, appExePath: string | null): Promise<void> {
    this.validateHomeExists(codexHome, 'codexHome')

    if (await this.isCodexAppRunning()) {
      throw new Error('Codex 已经在运行，请先关闭后再启动。')
    }

    const executable = appExePath?.trim() ? appExePath.trim() : await this.findCodexAppExecutable()
    if (!executable || !fs.existsSync(executable)) {
      throw new Error('未找到 Codex 程序，请先选择 Codex.exe。')
    }

    const apiKey = this.readOpenAiApiKey(codexHome)
    const child = spawn(executable, [], {
      cwd: path.dirname(executable),
      detached: true,
      stdio: 'ignore',
      windowsHide: false,
      env: {
        ...process.env,
        CODEX_HOME: codexHome,
        ...(apiKey?.trim() ? { OPENAI_API_KEY: apiKey } : {})
      }
    })

    child.unref()
  }

  private findRunningCodexExecutable(): Promise<string | null> {
    return runPowerShell(`
$path = Get-Process Codex -ErrorAction SilentlyContinue |
  ForEach-Object {
    try {
      $_.Path
    } catch {
      $null
    }
  } |
  Where-Object { $_ -and $_ -like '*OpenAI.Codex*' } |
  Select-Object -First 1
if ($path) { Write-Output $path }
    `)
      .then((value) => {
        const executable = value.trim()
        return executable && fs.existsSync(executable) ? executable : null
      })
      .catch(() => null)
  }

  private validateHomeExists(directoryPath: string, paramName: string): void {
    if (!directoryPath.trim() || !fs.existsSync(directoryPath) || !fs.statSync(directoryPath).isDirectory()) {
      throw new Error(`${paramName} 不存在：${directoryPath}`)
    }
  }

  private extractSessionId(filePath: string): string | null {
    const match = SESSION_ID_REGEX.exec(path.basename(filePath))
    return match?.[0] ?? null
  }

  private enumerateSessionFiles(rootDirectory: string): string[] {
    if (!fs.existsSync(rootDirectory)) {
      return []
    }

    const result: string[] = []
    const stack = [rootDirectory]

    while (stack.length > 0) {
      const current = stack.pop()
      if (!current) {
        continue
      }

      for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
        const absolutePath = path.join(current, entry.name)
        if (entry.isDirectory()) {
          stack.push(absolutePath)
          continue
        }

        if (entry.isFile() && absolutePath.toLowerCase().endsWith('.jsonl')) {
          result.push(absolutePath)
        }
      }
    }

    return result
  }

  private loadSessionIndex(codexHome: string): Map<string, SessionIndexEntry> {
    const filePath = path.join(codexHome, 'session_index.jsonl')
    const result = new Map<string, SessionIndexEntry>()
    if (!fs.existsSync(filePath)) {
      return result
    }

    for (const line of fs.readFileSync(filePath, 'utf8').split(/\r?\n/u)) {
      if (!line.trim()) {
        continue
      }

      try {
        const parsed = JSON.parse(line) as Record<string, unknown>
        const id = typeof parsed.id === 'string' ? parsed.id : ''
        if (!id.trim()) {
          continue
        }

        result.set(id, {
          title: typeof parsed.thread_name === 'string' ? parsed.thread_name : '',
          updatedAt: typeof parsed.updated_at === 'string' ? parsed.updated_at : new Date(0).toISOString()
        })
      } catch {
        // Ignore malformed JSONL entries.
      }
    }

    return result
  }

  private loadThreadRows(codexHome: string): Map<string, ThreadRow> {
    const databasePath = path.join(codexHome, 'state_5.sqlite')
    const result = new Map<string, ThreadRow>()
    if (!fs.existsSync(databasePath)) {
      return result
    }

    const db = new BetterSqlite3(databasePath)
    try {
      this.ensureThreadsTable(db)
      const rows = db
        .prepare(`
          SELECT id, title, cwd, source, model_provider, created_at, updated_at, rollout_path
          FROM threads
        `)
        .all() as ThreadRow[]

      for (const row of rows) {
        result.set(row.id, row)
      }
    } finally {
      db.close()
    }

    return result
  }

  private buildSessionRecord(
    sessionPath: string,
    sessionId: string,
    indexById: Map<string, SessionIndexEntry>,
    dbById: Map<string, ThreadRow>
  ): SessionRecord {
    const stats = fs.statSync(sessionPath)
    let title = ''
    let cwd = ''
    let modelProvider = ''
    let updatedAt = stats.mtime.toISOString()
    let createdAt = updatedAt

    const indexEntry = indexById.get(sessionId)
    if (indexEntry) {
      title = indexEntry.title
      updatedAt = this.safeIsoString(indexEntry.updatedAt)
    }

    const row = dbById.get(sessionId)
    if (row) {
      title = row.title || title
      cwd = row.cwd ? normalizeCwd(row.cwd) : cwd
      modelProvider = normalizeProvider(row.model_provider)
      if (row.created_at > 0) {
        createdAt = new Date(row.created_at * 1000).toISOString()
      }
      if (row.updated_at > 0) {
        updatedAt = new Date(row.updated_at * 1000).toISOString()
      }
    }

    if (!title || !cwd || !modelProvider) {
      const meta = this.tryReadSessionMeta(sessionPath)
      title = title || meta.title
      cwd = cwd || meta.cwd
      modelProvider = modelProvider || meta.modelProvider
      if (createdAt === updatedAt && meta.createdAt) {
        createdAt = meta.createdAt
      }
    }

    return {
      id: sessionId,
      title: title || sessionId,
      cwd,
      sessionPath,
      updatedAt,
      createdAt,
      source: 'session',
      modelProvider
    }
  }

  private tryReadSessionMeta(sessionPath: string): SessionMeta {
    try {
      const content = fs.readFileSync(sessionPath, 'utf8')
      const lines = content.split(/\r?\n/u)
      if (lines.length === 0 || !lines[0].trim()) {
        return { title: '', cwd: '', modelProvider: '', createdAt: '' }
      }

      let title = ''
      let cwd = ''
      let modelProvider = ''
      let createdAt = ''

      const firstLine = JSON.parse(lines[0]) as Record<string, unknown>
      const payload = this.asRecord(firstLine.payload)
      if (payload) {
        if (typeof payload.cwd === 'string') {
          cwd = normalizeCwd(payload.cwd)
        }
        if (typeof payload.model_provider === 'string') {
          modelProvider = normalizeProvider(payload.model_provider)
        }
        if (typeof payload.timestamp === 'string') {
          createdAt = this.safeIsoString(payload.timestamp)
        }
      }

      for (let index = 1; index < lines.length; index += 1) {
        const line = lines[index]
        if (!line?.trim()) {
          continue
        }

        let row: Record<string, unknown>
        try {
          row = JSON.parse(line) as Record<string, unknown>
        } catch {
          continue
        }

        if (row.type !== 'response_item') {
          continue
        }

        const rowPayload = this.asRecord(row.payload)
        if (!rowPayload || rowPayload.role !== 'user' || !Array.isArray(rowPayload.content)) {
          continue
        }

        for (const item of rowPayload.content) {
          const contentItem = this.asRecord(item)
          if (!contentItem || contentItem.type !== 'input_text') {
            continue
          }

          const text = typeof contentItem.text === 'string' ? contentItem.text : ''
          const normalized = this.normalizeTitle(text)
          if (normalized) {
            title = normalized
            return { title, cwd, modelProvider, createdAt }
          }
        }
      }

      return { title, cwd, modelProvider, createdAt }
    } catch {
      return { title: '', cwd: '', modelProvider: '', createdAt: '' }
    }
  }

  private applyThreadRow(record: SessionRecord, row: ThreadRow): void {
    if (row.title?.trim()) {
      record.title = row.title
    }
    if (row.cwd?.trim()) {
      record.cwd = normalizeCwd(row.cwd)
    }
    if (row.rollout_path?.trim() && !record.sessionPath) {
      record.sessionPath = stripVerbatimPathPrefix(row.rollout_path)
    }
    if (row.created_at > 0) {
      record.createdAt = new Date(row.created_at * 1000).toISOString()
    }
    if (row.updated_at > 0) {
      record.updatedAt = new Date(row.updated_at * 1000).toISOString()
    }
    if (row.source?.trim()) {
      record.source = row.source
    }
    if (row.model_provider?.trim()) {
      record.modelProvider = normalizeProvider(row.model_provider)
    }
  }

  private importSession(
    sourceHome: string,
    targetHome: string,
    sessionId: string,
    refreshUpdatedAt: boolean,
    addWorkspaceHint: boolean
  ): SessionRecord {
    this.validateHomeExists(sourceHome, 'sourceHome')
    this.validateHomeExists(targetHome, 'targetHome')

    const sessions = this.loadSessions(sourceHome)
    const sourceSession = sessions.find((item) => item.id.toLowerCase() === sessionId.toLowerCase())
    if (!sourceSession?.sessionPath || !fs.existsSync(sourceSession.sessionPath)) {
      throw new Error(`来源目录中不存在该会话：${sessionId}`)
    }

    const sourceSessionsRoot = path.join(sourceHome, 'sessions')
    const relativePath = path.relative(sourceSessionsRoot, sourceSession.sessionPath)
    const targetSessionPath = path.join(targetHome, 'sessions', relativePath)
    ensureDir(path.dirname(targetSessionPath))
    fs.copyFileSync(sourceSession.sessionPath, targetSessionPath)
    this.normalizeSessionFileMetadata(targetSessionPath, this.resolvePreferredModelProvider(targetHome))

    const importedSession: SessionRecord = {
      ...sourceSession,
      sessionPath: targetSessionPath
    }

    this.upsertSessionIndex(targetHome, importedSession, refreshUpdatedAt)
    this.upsertThreadRow(targetHome, importedSession, targetSessionPath, refreshUpdatedAt)
    this.appendHistoryLines(sourceHome, targetHome, sessionId)

    if (addWorkspaceHint && importedSession.cwd.trim()) {
      this.addWorkspaceRoot(targetHome, importedSession.cwd)
    }

    return this.loadSessions(targetHome).find((record) => record.id.toLowerCase() === sessionId.toLowerCase()) ?? importedSession
  }

  private upsertSessionIndex(targetHome: string, session: SessionRecord, refreshUpdatedAt: boolean): void {
    const filePath = path.join(targetHome, 'session_index.jsonl')
    ensureDir(path.dirname(filePath))

    const entry = JSON.stringify({
      id: session.id,
      thread_name: session.title,
      updated_at: refreshUpdatedAt ? new Date().toISOString() : this.safeIsoString(session.updatedAt)
    })

    this.upsertJsonlRow(filePath, 'id', session.id, entry)
  }

  private upsertThreadRow(targetHome: string, session: SessionRecord, targetSessionPath: string, refreshUpdatedAt: boolean): void {
    const databasePath = path.join(targetHome, 'state_5.sqlite')
    ensureDir(targetHome)

    const db = new BetterSqlite3(databasePath)
    try {
      this.ensureThreadsTable(db)

      const updatedAt = refreshUpdatedAt ? new Date() : new Date(session.updatedAt)
      const createdAt = session.createdAt ? new Date(session.createdAt) : updatedAt
      let modelProvider = this.coerceModelProvider(session.modelProvider, this.resolvePreferredModelProvider(targetHome))
      if (!modelProvider) {
        modelProvider = 'openai'
      }

      db.prepare(`
        INSERT INTO threads (
          id,
          rollout_path,
          created_at,
          updated_at,
          source,
          model_provider,
          cwd,
          title,
          sandbox_policy,
          approval_mode,
          tokens_used,
          has_user_event,
          archived,
          cli_version,
          first_user_message,
          memory_mode
        ) VALUES (
          @id,
          @rollout_path,
          @created_at,
          @updated_at,
          @source,
          @model_provider,
          @cwd,
          @title,
          @sandbox_policy,
          @approval_mode,
          0,
          1,
          0,
          @cli_version,
          @first_user_message,
          'enabled'
        )
        ON CONFLICT(id) DO UPDATE SET
          rollout_path = excluded.rollout_path,
          updated_at = excluded.updated_at,
          source = excluded.source,
          model_provider = excluded.model_provider,
          cwd = excluded.cwd,
          title = excluded.title,
          sandbox_policy = excluded.sandbox_policy,
          approval_mode = excluded.approval_mode,
          has_user_event = excluded.has_user_event,
          first_user_message = excluded.first_user_message,
          cli_version = excluded.cli_version
      `).run({
        id: session.id,
        rollout_path: toVerbatimPath(targetSessionPath),
        created_at: Math.floor(createdAt.getTime() / 1000),
        updated_at: Math.floor(updatedAt.getTime() / 1000),
        source: session.source?.trim() || 'cli',
        model_provider: modelProvider,
        cwd: session.cwd,
        title: session.title,
        sandbox_policy: '{"type":"danger-full-access"}',
        approval_mode: 'never',
        cli_version: 'imported-by-electron-client',
        first_user_message: session.title
      })
    } finally {
      db.close()
    }
  }

  private ensureThreadsTable(db: BetterSqlite3.Database): void {
    db.exec(`
      CREATE TABLE IF NOT EXISTS threads (
        id TEXT PRIMARY KEY,
        rollout_path TEXT NOT NULL,
        created_at INTEGER NOT NULL,
        updated_at INTEGER NOT NULL,
        source TEXT NOT NULL,
        model_provider TEXT NOT NULL,
        cwd TEXT NOT NULL,
        title TEXT NOT NULL,
        sandbox_policy TEXT NOT NULL,
        approval_mode TEXT NOT NULL,
        tokens_used INTEGER NOT NULL DEFAULT 0,
        has_user_event INTEGER NOT NULL DEFAULT 0,
        archived INTEGER NOT NULL DEFAULT 0,
        archived_at INTEGER,
        git_sha TEXT,
        git_branch TEXT,
        git_origin_url TEXT,
        cli_version TEXT NOT NULL DEFAULT '',
        first_user_message TEXT NOT NULL DEFAULT '',
        agent_nickname TEXT,
        agent_role TEXT,
        memory_mode TEXT NOT NULL DEFAULT 'enabled'
      );
      CREATE INDEX IF NOT EXISTS idx_threads_created_at ON threads(created_at DESC, id DESC);
      CREATE INDEX IF NOT EXISTS idx_threads_updated_at ON threads(updated_at DESC, id DESC);
      CREATE INDEX IF NOT EXISTS idx_threads_archived ON threads(archived);
      CREATE INDEX IF NOT EXISTS idx_threads_source ON threads(source);
      CREATE INDEX IF NOT EXISTS idx_threads_provider ON threads(model_provider);
    `)
  }

  private appendHistoryLines(sourceHome: string, targetHome: string, sessionId: string): void {
    const sourcePath = path.join(sourceHome, 'history.jsonl')
    if (!fs.existsSync(sourcePath)) {
      return
    }

    const sessionIdLower = sessionId.toLowerCase()
    const matchingLines = new Set(
      fs.readFileSync(sourcePath, 'utf8')
        .split(/\r?\n/u)
        .filter((line) => line.trim() && line.toLowerCase().includes(sessionIdLower))
    )

    if (matchingLines.size === 0) {
      return
    }

    const targetPath = path.join(targetHome, 'history.jsonl')
    const existing = fs.existsSync(targetPath)
      ? new Set(fs.readFileSync(targetPath, 'utf8').split(/\r?\n/u).filter((line) => line.trim()))
      : new Set<string>()

    const merged = [...existing]
    for (const line of matchingLines) {
      if (!existing.has(line)) {
        merged.push(line)
      }
    }

    writeTextNoBom(targetPath, merged.join('\n'))
  }

  private addWorkspaceRoot(targetHome: string, workspaceRoot: string): void {
    const normalizedWorkspaceRoot = normalizeWorkspaceRoot(workspaceRoot)
    if (!normalizedWorkspaceRoot) {
      return
    }

    const filePath = path.join(targetHome, '.codex-global-state.json')
    let root = this.tryParseJsonObject(readTextIfExists(filePath))
    if (!root) {
      root = {}
    }

    this.ensureArrayContains(root, 'electron-saved-workspace-roots', normalizedWorkspaceRoot)
    this.ensureArrayContains(root, 'active-workspace-roots', normalizedWorkspaceRoot)

    writeTextNoBom(filePath, JSON.stringify(root, null, 2))
  }

  private ensureArrayContains(root: Record<string, unknown>, propertyName: string, value: string): void {
    const current = Array.isArray(root[propertyName]) ? [...(root[propertyName] as unknown[])] : []
    const normalized = current.filter((item): item is string => typeof item === 'string' && item.trim().length > 0)
    if (!normalized.some((item) => item.toLowerCase() === value.toLowerCase())) {
      normalized.push(value)
    }

    root[propertyName] = normalized
  }

  private ensureSharedStoreHome(sharedStoreHome: string): void {
    if (!sharedStoreHome.trim()) {
      throw new Error('共享仓目录不能为空。')
    }

    ensureDir(sharedStoreHome)
  }

  private ensureProjectionHomes(sharedStoreHome: string, runtimeHome: string): void {
    if (!runtimeHome.trim()) {
      throw new Error('运行目录不能为空。')
    }

    if (samePath(sharedStoreHome, runtimeHome)) {
      throw new Error('共享仓目录和运行目录不能是同一个目录。')
    }
  }

  private ensureRuntimeConfigurationFiles(sourceHome: string | null, runtimeHome: string, overwriteExisting: boolean): void {
    if (!sourceHome?.trim() || !fs.existsSync(sourceHome)) {
      return
    }

    this.syncRuntimeConfigFile(sourceHome, runtimeHome, 'auth.json', overwriteExisting)
    this.syncRuntimeConfigFile(sourceHome, runtimeHome, 'config.toml', overwriteExisting)
  }

  private syncRuntimeConfigFile(sourceHome: string, runtimeHome: string, fileName: string, overwriteExisting: boolean): void {
    const source = path.join(sourceHome, fileName)
    const target = path.join(runtimeHome, fileName)
    if (samePath(source, target)) {
      return
    }

    if (!fs.existsSync(source)) {
      if (overwriteExisting && fs.existsSync(target)) {
        fs.rmSync(target, { force: true })
      }
      return
    }

    if (!overwriteExisting && fs.existsSync(target)) {
      return
    }

    ensureDir(runtimeHome)
    fs.copyFileSync(source, target)
  }

  private async resetManagedState(runtimeHome: string): Promise<void> {
    for (const relativePath of STATE_PATHS) {
      await this.deleteManagedPath(path.join(runtimeHome, relativePath))
    }
  }

  private async deleteManagedPath(targetPath: string): Promise<void> {
    const maxAttempts = 6
    for (let attempt = 1; attempt <= maxAttempts; attempt += 1) {
      try {
        if (fs.existsSync(targetPath)) {
          const stat = fs.statSync(targetPath)
          if (stat.isDirectory()) {
            fs.rmSync(targetPath, { recursive: true, force: true })
          } else {
            fs.rmSync(targetPath, { force: true })
          }
        }
        return
      } catch (error) {
        if (attempt >= maxAttempts) {
          throw error
        }

        await new Promise((resolve) => setTimeout(resolve, 150))
      }
    }
  }

  private upsertSharedCatalog(sharedStoreHome: string, session: SessionRecord, sourceHome: string): void {
    const filePath = path.join(sharedStoreHome, 'catalog.jsonl')
    ensureDir(sharedStoreHome)

    const entry = JSON.stringify({
      session_id: session.id,
      title: session.title,
      cwd: session.cwd,
      source_home: sourceHome,
      shared_session_path: session.sessionPath,
      original_provider: normalizeProvider(session.modelProvider),
      imported_at: new Date().toISOString()
    })

    this.upsertJsonlRow(filePath, 'session_id', session.id, entry)
  }

  private upsertJsonlRow(filePath: string, keyField: string, keyValue: string, entryLine: string): void {
    const rows: string[] = []
    let replaced = false
    const targetKey = keyValue.toLowerCase()

    if (fs.existsSync(filePath)) {
      for (const line of fs.readFileSync(filePath, 'utf8').split(/\r?\n/u)) {
        if (!line.trim()) {
          continue
        }

        const parsed = this.tryParseJsonObject(line)
        if (parsed && typeof parsed[keyField] === 'string' && String(parsed[keyField]).toLowerCase() === targetKey) {
          if (!replaced) {
            rows.push(entryLine)
            replaced = true
          }
          continue
        }

        rows.push(line)
      }
    }

    if (!replaced) {
      rows.push(entryLine)
    }

    writeTextNoBom(filePath, rows.join('\n'))
  }

  private normalizeWorkspaceRoots(targetHome: string): void {
    const filePath = path.join(targetHome, '.codex-global-state.json')
    if (!fs.existsSync(filePath)) {
      return
    }

    const root = this.tryParseJsonObject(readTextIfExists(filePath))
    if (!root) {
      return
    }

    this.normalizeWorkspaceRootArray(root, 'electron-saved-workspace-roots')
    this.normalizeWorkspaceRootArray(root, 'active-workspace-roots')

    if (this.isRecord(root['thread-workspace-root-hints'])) {
      const hints = root['thread-workspace-root-hints'] as Record<string, unknown>
      for (const [key, value] of Object.entries(hints)) {
        if (typeof value === 'string' && value.trim()) {
          hints[key] = normalizeWorkspaceRoot(value)
        }
      }
    }

    writeTextNoBom(filePath, JSON.stringify(root, null, 2))
  }

  private normalizeWorkspaceRootArray(root: Record<string, unknown>, propertyName: string): void {
    if (!Array.isArray(root[propertyName])) {
      return
    }

    const seen = new Set<string>()
    const normalized: string[] = []

    for (const item of root[propertyName] as unknown[]) {
      if (typeof item !== 'string' || !item.trim()) {
        continue
      }

      const value = normalizeWorkspaceRoot(item)
      const key = value.toLowerCase()
      if (!seen.has(key)) {
        seen.add(key)
        normalized.push(value)
      }
    }

    root[propertyName] = normalized
  }

  private normalizeSessionFiles(sessionsRoot: string, preferredModelProvider: string): void {
    if (!fs.existsSync(sessionsRoot)) {
      return
    }

    for (const sessionPath of this.enumerateSessionFiles(sessionsRoot)) {
      this.normalizeSessionFileMetadata(sessionPath, preferredModelProvider)
    }
  }

  private normalizeSessionFileMetadata(sessionPath: string, preferredModelProvider: string): void {
    if (!fs.existsSync(sessionPath)) {
      return
    }

    const raw = fs.readFileSync(sessionPath, 'utf8')
    if (!raw.trim()) {
      return
    }

    const lineEnding = raw.includes('\r\n') ? '\r\n' : '\n'
    const firstSeparatorIndex = raw.indexOf(lineEnding)
    const firstLine = firstSeparatorIndex >= 0 ? raw.slice(0, firstSeparatorIndex) : raw
    const rest = firstSeparatorIndex >= 0 ? raw.slice(firstSeparatorIndex + lineEnding.length) : ''
    const root = this.tryParseJsonObject(firstLine)
    if (!root || root.type !== 'session_meta' || !this.isRecord(root.payload)) {
      return
    }

    const payload = root.payload as Record<string, unknown>
    let changed = false

    const originalProvider = typeof payload.model_provider === 'string' ? payload.model_provider : ''
    const normalizedProvider = this.coerceModelProvider(originalProvider, preferredModelProvider)
    if (normalizedProvider && normalizedProvider !== originalProvider) {
      payload.model_provider = normalizedProvider
      changed = true
    }

    const originalCwd = typeof payload.cwd === 'string' ? payload.cwd : ''
    const normalizedCwd = normalizeWorkspaceRoot(originalCwd)
    if (originalCwd && normalizedCwd !== originalCwd) {
      payload.cwd = normalizedCwd
      changed = true
    }

    if (!changed) {
      return
    }

    const nextContent = firstSeparatorIndex >= 0
      ? `${JSON.stringify(root)}${lineEnding}${rest}`
      : JSON.stringify(root)

    writeTextNoBom(sessionPath, nextContent)
  }

  private repairThreadRows(targetHome: string, copiedFromHome: string | null, preferredModelProvider: string): void {
    const databasePath = path.join(targetHome, 'state_5.sqlite')
    if (!fs.existsSync(databasePath)) {
      return
    }

    const sourceSessionsRoot = copiedFromHome?.trim() ? path.join(copiedFromHome, 'sessions') : null
    const targetSessionsRoot = path.join(targetHome, 'sessions')

    const db = new BetterSqlite3(databasePath)
    try {
      this.ensureThreadsTable(db)
      const rows = db
        .prepare(`
          SELECT id, rollout_path, model_provider, cwd
          FROM threads
        `)
        .all() as Array<{ id: string; rollout_path: string; model_provider: string; cwd: string }>

      const update = db.prepare(`
        UPDATE threads
        SET rollout_path = @rollout_path,
            model_provider = @model_provider,
            cwd = @cwd
        WHERE id = @id
      `)

      const transaction = db.transaction((entries: Array<{ id: string; rollout_path: string; model_provider: string; cwd: string }>) => {
        for (const entry of entries) {
          update.run(entry)
        }
      })

      const updates: Array<{ id: string; rollout_path: string; model_provider: string; cwd: string }> = []
      for (const row of rows) {
        const repairedPath = this.rewriteRolloutPath(row.rollout_path, sourceSessionsRoot, targetSessionsRoot)
        const repairedProvider = this.coerceModelProvider(row.model_provider, preferredModelProvider) || row.model_provider
        const repairedCwd = normalizeWorkspaceRoot(row.cwd)

        if (
          repairedPath !== row.rollout_path ||
          repairedProvider !== row.model_provider ||
          repairedCwd !== row.cwd
        ) {
          updates.push({
            id: row.id,
            rollout_path: repairedPath,
            model_provider: repairedProvider,
            cwd: repairedCwd
          })
        }
      }

      if (updates.length > 0) {
        transaction(updates)
      }
    } finally {
      db.close()
    }
  }

  private rewriteRolloutPath(rolloutPath: string, sourceSessionsRoot: string | null, targetSessionsRoot: string): string {
    if (!rolloutPath?.trim()) {
      return rolloutPath
    }

    const normalizedPath = path.resolve(stripVerbatimPathPrefix(rolloutPath))
    const normalizedTargetRoot = path.resolve(targetSessionsRoot)
    if (normalizedPath.toLowerCase().startsWith(normalizedTargetRoot.toLowerCase())) {
      return toVerbatimPath(normalizedPath)
    }

    if (!sourceSessionsRoot?.trim()) {
      return rolloutPath
    }

    const normalizedSourceRoot = path.resolve(sourceSessionsRoot)
    if (!normalizedPath.toLowerCase().startsWith(normalizedSourceRoot.toLowerCase())) {
      return rolloutPath
    }

    const relativePath = path.relative(normalizedSourceRoot, normalizedPath)
    return toVerbatimPath(path.join(normalizedTargetRoot, relativePath))
  }

  private resolvePreferredModelProvider(codexHome: string): string {
    const configuredProvider = this.tryReadConfiguredModelProvider(codexHome)
    if (configuredProvider) {
      return configuredProvider
    }

    return this.inferProviderFromAuth(codexHome)
  }

  private tryReadConfiguredModelProvider(codexHome: string): string {
    const configPath = path.join(codexHome, 'config.toml')
    if (!fs.existsSync(configPath)) {
      return ''
    }

    return normalizeProvider(tryReadTopLevelTomlStringValue(readTextIfExists(configPath), 'model_provider'))
  }

  private inferProviderFromAuth(codexHome: string): string {
    const authPath = path.join(codexHome, 'auth.json')
    if (!fs.existsSync(authPath)) {
      return ''
    }

    try {
      const parsed = JSON.parse(fs.readFileSync(authPath, 'utf8')) as Record<string, unknown>
      if (typeof parsed.OPENAI_API_KEY === 'string' && parsed.OPENAI_API_KEY.trim()) {
        return 'openai'
      }
    } catch {
      // Best effort only.
    }

    return ''
  }

  private coerceModelProvider(sourceProvider: string | null | undefined, preferredModelProvider: string | null | undefined): string {
    return normalizeProvider(preferredModelProvider) || normalizeProvider(sourceProvider)
  }

  private safeIsoString(value: string | number | Date): string {
    const date = value instanceof Date ? value : new Date(value)
    return Number.isNaN(date.getTime()) ? new Date(0).toISOString() : date.toISOString()
  }

  private normalizeTitle(text: string): string {
    const trimmed = text.replace(/\s+/gu, ' ').trim()
    if (!trimmed || trimmed.startsWith('<')) {
      return ''
    }

    return trimmed.length > 120 ? trimmed.slice(0, 120) : trimmed
  }

  private tryParseJsonObject(text: string): Record<string, unknown> | null {
    if (!text.trim()) {
      return null
    }

    try {
      const parsed = JSON.parse(text) as unknown
      return this.isRecord(parsed) ? parsed : null
    } catch {
      return null
    }
  }

  private asRecord(value: unknown): Record<string, unknown> | null {
    return this.isRecord(value) ? value : null
  }

  private isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === 'object' && value !== null && !Array.isArray(value)
  }
}
