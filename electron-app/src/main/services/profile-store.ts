import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import BetterSqlite3 from 'better-sqlite3'
import type { ManagedProfileContent, ProviderProfile } from '@shared/contracts'
import {
  computeSha256,
  ensureDir,
  normalizeDirectoryKey,
  normalizeProfileName,
  normalizeProvider,
  readTextIfExists,
  samePath,
  stripTomlComment,
  tryReadTopLevelTomlStringValue,
  unquoteTomlString,
  writeTextNoBom
} from '../utils/path-utils'

type AccountRow = {
  id: number
  name: string
  provider: string
  auth_json: string
  config_toml: string
  revision: number
  updated_at: string
}

export class ProfileStore {
  private isInitialized = false

  constructor(
    private readonly databasePath: string,
    private readonly materializedProfilesRoot: string
  ) {
    this.ensureInitialized()
  }

  get defaultProfilesRoot(): string {
    return path.join(os.homedir(), '.codex-profiles')
  }

  get databaseFilePath(): string {
    return this.databasePath
  }

  get materializedRoot(): string {
    return this.materializedProfilesRoot
  }

  listProfiles(profilesRoot: string): ProviderProfile[] {
    this.ensureInitialized()
    this.migrateLegacyProfilesIfDatabaseEmpty(profilesRoot)

    const db = this.openConnection()
    try {
      const rows = db
        .prepare(`
          SELECT a.id, a.name, a.provider, c.revision, c.updated_at
          FROM accounts a
          JOIN account_contents c ON c.account_id = a.id
          WHERE a.is_enabled = 1
          ORDER BY a.name COLLATE NOCASE
        `)
        .all() as Array<{ id: number; name: string; provider: string; revision: number; updated_at: string }>

      return rows.map((row) => ({
        id: row.id,
        name: row.name,
        directoryPath: '',
        modelProvider: row.provider ?? '',
        revision: row.revision,
        updatedAt: this.parseTimestamp(row.updated_at)
      }))
    } finally {
      db.close()
    }
  }

  saveProfile(profilesRoot: string, profileName: string, sourceHome: string, overwrite: boolean): ProviderProfile {
    if (!sourceHome.trim() || !fs.existsSync(sourceHome)) {
      throw new Error(`Profile source home does not exist: ${sourceHome}`)
    }

    this.ensureInitialized()
    this.migrateLegacyProfilesIfDatabaseEmpty(profilesRoot)

    const normalizedName = normalizeProfileName(profileName)
    if (!normalizedName) {
      throw new Error('Profile name cannot be empty.')
    }

    if (!overwrite && this.profileExists(normalizedName)) {
      return this.getProfileMetadata(normalizedName)
    }

    const authJson = readTextIfExists(path.join(sourceHome, 'auth.json'))
    const configToml = readTextIfExists(path.join(sourceHome, 'config.toml'))
    if (!authJson.trim() && !configToml.trim()) {
      throw new Error('The selected source home does not contain auth.json or config.toml.')
    }

    return this.saveProfileContentInternal(normalizedName, authJson, configToml)
  }

  importProfile(profilesRoot: string, sourceDirectory: string, profileName: string | null, overwrite: boolean): ProviderProfile {
    if (!sourceDirectory.trim() || !fs.existsSync(sourceDirectory)) {
      throw new Error(`Import source directory does not exist: ${sourceDirectory}`)
    }

    let suggestedName = normalizeProfileName(profileName)
    if (!suggestedName) {
      suggestedName = path.basename(path.resolve(sourceDirectory))
    }

    return this.saveProfile(profilesRoot, suggestedName, sourceDirectory, overwrite)
  }

  exportProfile(profilesRoot: string, profileName: string, targetRoot: string, overwrite: boolean): string {
    if (!targetRoot.trim()) {
      throw new Error('Export target directory cannot be empty.')
    }

    this.ensureInitialized()
    this.migrateLegacyProfilesIfDatabaseEmpty(profilesRoot)

    const content = this.getProfileContent(profileName)
    ensureDir(targetRoot)

    const exportDirectory = path.join(targetRoot, content.name)
    if (fs.existsSync(exportDirectory) && !overwrite) {
      throw new Error(`Export target already exists: ${exportDirectory}`)
    }

    ensureDir(exportDirectory)
    this.writeMaterializedFile(path.join(exportDirectory, 'auth.json'), content.authJson)
    this.writeMaterializedFile(path.join(exportDirectory, 'config.toml'), content.configToml)
    this.writeProfileMetadata(exportDirectory, content.name, content.modelProvider, content.revision, content.updatedAt)
    return exportDirectory
  }

  getProfile(profilesRoot: string, profileName: string): ProviderProfile {
    this.ensureInitialized()
    this.migrateLegacyProfilesIfDatabaseEmpty(profilesRoot)

    const content = this.getProfileContent(profileName)
    const materializedDirectory = this.materializeProfile(content)
    return {
      id: content.accountId,
      name: content.name,
      directoryPath: materializedDirectory,
      modelProvider: content.modelProvider,
      revision: content.revision,
      updatedAt: content.updatedAt
    }
  }

  getProfileContent(profileName: string): ManagedProfileContent {
    this.ensureInitialized()

    const normalizedName = normalizeProfileName(profileName)
    if (!normalizedName) {
      throw new Error('Profile name cannot be empty.')
    }

    const db = this.openConnection()
    try {
      const row = db
        .prepare(`
          SELECT a.id, a.name, a.provider, c.auth_json, c.config_toml, c.revision, c.updated_at
          FROM accounts a
          JOIN account_contents c ON c.account_id = a.id
          WHERE a.name = ? COLLATE NOCASE
          LIMIT 1
        `)
        .get(normalizedName) as AccountRow | undefined

      if (!row) {
        throw new Error(`Profile does not exist: ${normalizedName}`)
      }

      return {
        accountId: row.id,
        name: row.name,
        modelProvider: row.provider ?? '',
        authJson: row.auth_json ?? '',
        configToml: row.config_toml ?? '',
        revision: row.revision,
        updatedAt: this.parseTimestamp(row.updated_at)
      }
    } finally {
      db.close()
    }
  }

  getOrCreateProfileContent(profileName: string): ManagedProfileContent {
    this.ensureInitialized()

    const normalizedName = normalizeProfileName(profileName)
    if (!normalizedName) {
      throw new Error('Profile name cannot be empty.')
    }

    if (this.profileExists(normalizedName)) {
      return this.getProfileContent(normalizedName)
    }

    const db = this.openConnection()
    try {
      const timestamp = this.createTimestamp()
      const createProfile = db.transaction(() => {
        const insertAccount = db.prepare(`
          INSERT INTO accounts (name, provider, remark, is_enabled, created_at, updated_at)
          VALUES (?, '', '', 1, ?, ?)
        `)
        const accountResult = insertAccount.run(normalizedName, timestamp, timestamp)
        const accountId = Number(accountResult.lastInsertRowid)

        db.prepare(`
          INSERT INTO account_contents (
            account_id,
            auth_json,
            config_toml,
            auth_sha256,
            config_sha256,
            revision,
            created_at,
            updated_at
          ) VALUES (?, '', '', '', '', 1, ?, ?)
        `).run(accountId, timestamp, timestamp)
      })
      createProfile()
    } finally {
      db.close()
    }

    return this.getProfileContent(normalizedName)
  }

  saveProfileContent(profileName: string, authJson: string, configToml: string): ManagedProfileContent {
    const profile = this.saveProfileContentInternal(profileName, authJson, configToml)
    return this.getProfileContent(profile.name)
  }

  updateProfileFromHome(profileName: string, sourceHome: string): ProviderProfile {
    return this.saveProfile(this.defaultProfilesRoot, profileName, sourceHome, true)
  }

  createEmptyProfile(profileName: string): ProviderProfile {
    this.ensureInitialized()

    const normalizedName = normalizeProfileName(profileName)
    if (!normalizedName) {
      throw new Error('Profile name cannot be empty.')
    }

    if (this.profileExists(normalizedName)) {
      throw new Error(`Profile already exists: ${normalizedName}`)
    }

    return this.saveProfileContentInternal(normalizedName, '', '')
  }

  renameProfile(currentProfileName: string, newProfileName: string): ProviderProfile {
    this.ensureInitialized()

    const normalizedCurrentName = normalizeProfileName(currentProfileName)
    if (!normalizedCurrentName) {
      throw new Error('Current profile name cannot be empty.')
    }

    const normalizedNewName = normalizeProfileName(newProfileName)
    if (!normalizedNewName) {
      throw new Error('New profile name cannot be empty.')
    }

    if (normalizedCurrentName === normalizedNewName) {
      return this.getProfileMetadata(normalizedCurrentName)
    }

    const db = this.openConnection()
    let accountId = 0
    try {
      const renameTransaction = db.transaction(() => {
        const existingAccountId = this.tryGetAccountId(db, normalizedCurrentName)
        if (existingAccountId == null) {
          throw new Error(`Profile does not exist: ${normalizedCurrentName}`)
        }

        const conflictingAccountId = this.tryGetAccountId(db, normalizedNewName)
        if (conflictingAccountId != null && conflictingAccountId !== existingAccountId) {
          throw new Error(`Profile already exists: ${normalizedNewName}`)
        }

        accountId = existingAccountId
        db.prepare(`
          UPDATE accounts
          SET name = ?, updated_at = ?
          WHERE id = ?
        `).run(normalizedNewName, this.createTimestamp(), existingAccountId)
      })
      renameTransaction()
    } finally {
      db.close()
    }

    const content = this.getProfileContent(normalizedNewName)
    const materializedDirectory = this.materializeProfile(content)
    this.deleteMaterializedDirectoriesForAccount(accountId, materializedDirectory)
    return {
      id: content.accountId,
      name: content.name,
      directoryPath: materializedDirectory,
      modelProvider: content.modelProvider,
      revision: content.revision,
      updatedAt: content.updatedAt
    }
  }

  deleteProfile(profileName: string): void {
    this.ensureInitialized()

    const normalizedName = normalizeProfileName(profileName)
    if (!normalizedName) {
      throw new Error('Profile name cannot be empty.')
    }

    const db = this.openConnection()
    let accountId = 0
    try {
      const deleteTransaction = db.transaction(() => {
        const existingAccountId = this.tryGetAccountId(db, normalizedName)
        if (existingAccountId == null) {
          throw new Error(`Profile does not exist: ${normalizedName}`)
        }

        accountId = existingAccountId
        db.prepare('DELETE FROM accounts WHERE id = ?').run(existingAccountId)
      })
      deleteTransaction()
    } finally {
      db.close()
    }

    this.deleteMaterializedDirectoriesForAccount(accountId)
  }

  loadSharedStoreDefaultLaunchProfiles(): Record<string, string> {
    this.ensureInitialized()

    const db = this.openConnection()
    try {
      const rows = db
        .prepare(`
          SELECT d.shared_store_key, a.name
          FROM shared_store_defaults d
          JOIN accounts a ON a.id = d.account_id
          ORDER BY d.shared_store_path COLLATE NOCASE
        `)
        .all() as Array<{ shared_store_key: string; name: string }>

      const mappings: Record<string, string> = {}
      for (const row of rows) {
        if (!row.shared_store_key?.trim() || !row.name?.trim()) {
          continue
        }

        mappings[row.shared_store_key] = row.name
      }

      return mappings
    } finally {
      db.close()
    }
  }

  migrateSharedStoreDefaultLaunchProfiles(mappings?: Record<string, string> | null): Record<string, string> {
    this.ensureInitialized()
    if (!mappings || Object.keys(mappings).length === 0) {
      return this.loadSharedStoreDefaultLaunchProfiles()
    }

    const db = this.openConnection()
    try {
      const countRow = db.prepare('SELECT COUNT(1) AS count FROM shared_store_defaults').get() as { count?: number } | undefined
      const existingCount = Number(countRow?.count ?? 0)
      if (existingCount > 0) {
        return this.loadSharedStoreDefaultLaunchProfiles()
      }
    } finally {
      db.close()
    }

    this.saveSharedStoreDefaultLaunchProfiles(mappings)
    return this.loadSharedStoreDefaultLaunchProfiles()
  }

  saveSharedStoreDefaultLaunchProfiles(mappings: Record<string, string>): void {
    this.ensureInitialized()

    const db = this.openConnection()
    try {
      const saveMappings = db.transaction(() => {
        db.prepare('DELETE FROM shared_store_defaults').run()

        for (const [rawStoreKey, rawProfileName] of Object.entries(mappings)) {
          const sharedStoreKey = this.normalizeStoreKey(rawStoreKey)
          const profileName = normalizeProfileName(rawProfileName)
          if (!sharedStoreKey || !profileName) {
            continue
          }

          const accountId = this.tryGetAccountId(db, profileName)
          if (accountId == null) {
            continue
          }

          db.prepare(`
            INSERT INTO shared_store_defaults (shared_store_key, shared_store_path, account_id, created_at, updated_at)
            VALUES (?, ?, ?, ?, ?)
          `).run(sharedStoreKey, rawStoreKey.trim(), accountId, this.createTimestamp(), this.createTimestamp())
        }
      })
      saveMappings()
    } finally {
      db.close()
    }
  }

  private saveProfileContentInternal(profileName: string, authJson: string, configToml: string): ProviderProfile {
    this.ensureInitialized()

    const normalizedName = normalizeProfileName(profileName)
    if (!normalizedName) {
      throw new Error('Profile name cannot be empty.')
    }

    const nextAuthJson = authJson ?? ''
    const nextConfigToml = configToml ?? ''
    const provider = this.inferProvider(nextConfigToml, nextAuthJson)
    const timestamp = this.createTimestamp()

    const db = this.openConnection()
    try {
      const saveProfileContent = db.transaction(() => {
        const existingAccountId = this.tryGetAccountId(db, normalizedName)
        if (existingAccountId == null) {
          const accountResult = db.prepare(`
            INSERT INTO accounts (name, provider, remark, is_enabled, created_at, updated_at)
            VALUES (?, ?, '', 1, ?, ?)
          `).run(normalizedName, provider, timestamp, timestamp)

          const accountId = Number(accountResult.lastInsertRowid)
          db.prepare(`
            INSERT INTO account_contents (
              account_id,
              auth_json,
              config_toml,
              auth_sha256,
              config_sha256,
              revision,
              created_at,
              updated_at
            ) VALUES (?, ?, ?, ?, ?, 1, ?, ?)
          `).run(accountId, nextAuthJson, nextConfigToml, computeSha256(nextAuthJson), computeSha256(nextConfigToml), timestamp, timestamp)
          return
        }

        db.prepare(`
          UPDATE accounts
          SET provider = ?, updated_at = ?
          WHERE id = ?
        `).run(provider, timestamp, existingAccountId)

        const revisionRow = db.prepare('SELECT revision FROM account_contents WHERE account_id = ? LIMIT 1').get(existingAccountId) as { revision?: number } | undefined
        const revision = revisionRow?.revision ? Number(revisionRow.revision) + 1 : 1

        db.prepare(`
          INSERT INTO account_contents (
            account_id,
            auth_json,
            config_toml,
            auth_sha256,
            config_sha256,
            revision,
            created_at,
            updated_at
          ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
          ON CONFLICT(account_id) DO UPDATE SET
            auth_json = excluded.auth_json,
            config_toml = excluded.config_toml,
            auth_sha256 = excluded.auth_sha256,
            config_sha256 = excluded.config_sha256,
            revision = excluded.revision,
            updated_at = excluded.updated_at
        `).run(existingAccountId, nextAuthJson, nextConfigToml, computeSha256(nextAuthJson), computeSha256(nextConfigToml), revision, timestamp, timestamp)
      })
      saveProfileContent()
    } finally {
      db.close()
    }

    const content = this.getProfileContent(normalizedName)
    this.materializeProfile(content)
    return {
      id: content.accountId,
      name: content.name,
      directoryPath: this.getMaterializedDirectory(content.accountId, content.name),
      modelProvider: content.modelProvider,
      revision: content.revision,
      updatedAt: content.updatedAt
    }
  }

  private migrateLegacyProfilesIfDatabaseEmpty(profilesRoot: string): void {
    if (this.countProfiles() > 0) {
      return
    }

    if (!profilesRoot.trim() || !fs.existsSync(profilesRoot)) {
      return
    }

    for (const entry of fs.readdirSync(profilesRoot, { withFileTypes: true })) {
      if (!entry.isDirectory()) {
        continue
      }

      const directory = path.join(profilesRoot, entry.name)
      const authPath = path.join(directory, 'auth.json')
      const configPath = path.join(directory, 'config.toml')
      if (!fs.existsSync(authPath) && !fs.existsSync(configPath)) {
        continue
      }

      this.saveProfileContentInternal(entry.name, readTextIfExists(authPath), readTextIfExists(configPath))
    }
  }

  private countProfiles(): number {
    const db = this.openConnection()
    try {
      const row = db.prepare('SELECT COUNT(1) AS count FROM accounts').get() as { count: number }
      return Number(row.count ?? 0)
    } finally {
      db.close()
    }
  }

  private getProfileMetadata(profileName: string): ProviderProfile {
    const content = this.getProfileContent(profileName)
    return {
      id: content.accountId,
      name: content.name,
      directoryPath: this.getMaterializedDirectory(content.accountId, content.name),
      modelProvider: content.modelProvider,
      revision: content.revision,
      updatedAt: content.updatedAt
    }
  }

  private profileExists(profileName: string): boolean {
    const db = this.openConnection()
    try {
      const row = db.prepare('SELECT COUNT(1) AS count FROM accounts WHERE name = ? COLLATE NOCASE').get(profileName) as { count: number }
      return Number(row.count ?? 0) > 0
    } finally {
      db.close()
    }
  }

  private materializeProfile(content: ManagedProfileContent): string {
    const directory = this.getMaterializedDirectory(content.accountId, content.name)
    ensureDir(directory)
    this.writeMaterializedFile(path.join(directory, 'auth.json'), content.authJson)
    this.writeMaterializedFile(path.join(directory, 'config.toml'), content.configToml)
    this.writeProfileMetadata(directory, content.name, content.modelProvider, content.revision, content.updatedAt)
    return directory
  }

  private getMaterializedDirectory(accountId: number, profileName: string): string {
    return path.join(this.materializedProfilesRoot, `${String(accountId).padStart(6, '0')}-${normalizeProfileName(profileName)}`)
  }

  private deleteMaterializedDirectoriesForAccount(accountId: number, exceptDirectory?: string): void {
    if (!fs.existsSync(this.materializedProfilesRoot)) {
      return
    }

    for (const entry of fs.readdirSync(this.materializedProfilesRoot, { withFileTypes: true })) {
      if (!entry.isDirectory() || !entry.name.startsWith(`${String(accountId).padStart(6, '0')}-`)) {
        continue
      }

      const directory = path.join(this.materializedProfilesRoot, entry.name)
      if (exceptDirectory && samePath(directory, exceptDirectory)) {
        continue
      }

      try {
        fs.rmSync(directory, { recursive: true, force: true })
      } catch {
        // Best effort cleanup.
      }
    }
  }

  private ensureInitialized(): void {
    if (this.isInitialized) {
      return
    }

    const databaseDirectory = path.dirname(this.databasePath)
    ensureDir(databaseDirectory)
    ensureDir(this.materializedProfilesRoot)

    const db = this.openConnection()
    try {
      db.pragma('journal_mode = WAL')
      db.exec(`
        CREATE TABLE IF NOT EXISTS accounts (
          id                INTEGER PRIMARY KEY AUTOINCREMENT,
          name              TEXT NOT NULL COLLATE NOCASE UNIQUE,
          provider          TEXT NOT NULL DEFAULT '',
          remark            TEXT NOT NULL DEFAULT '',
          is_enabled        INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0, 1)),
          created_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
          updated_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
        );
        CREATE TABLE IF NOT EXISTS account_contents (
          account_id        INTEGER PRIMARY KEY,
          auth_json         TEXT NOT NULL DEFAULT '',
          config_toml       TEXT NOT NULL DEFAULT '',
          auth_sha256       TEXT NOT NULL DEFAULT '',
          config_sha256     TEXT NOT NULL DEFAULT '',
          revision          INTEGER NOT NULL DEFAULT 1 CHECK (revision >= 1),
          created_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
          updated_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
          FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS shared_store_defaults (
          shared_store_key  TEXT PRIMARY KEY COLLATE NOCASE,
          shared_store_path TEXT NOT NULL,
          account_id        INTEGER NOT NULL,
          created_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
          updated_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
          FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS materialized_targets (
          target_home_key   TEXT PRIMARY KEY COLLATE NOCASE,
          target_home_path  TEXT NOT NULL,
          account_id        INTEGER NOT NULL,
          revision          INTEGER NOT NULL DEFAULT 0,
          auth_sha256       TEXT NOT NULL DEFAULT '',
          config_sha256     TEXT NOT NULL DEFAULT '',
          last_written_at   TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
          FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS app_settings (
          key               TEXT PRIMARY KEY,
          value             TEXT NOT NULL DEFAULT '',
          updated_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
        );
        CREATE INDEX IF NOT EXISTS idx_accounts_provider ON accounts(provider);
        CREATE INDEX IF NOT EXISTS idx_accounts_enabled ON accounts(is_enabled);
        CREATE INDEX IF NOT EXISTS idx_shared_store_defaults_account_id ON shared_store_defaults(account_id);
        CREATE INDEX IF NOT EXISTS idx_materialized_targets_account_id ON materialized_targets(account_id);
      `)
      this.isInitialized = true
    } finally {
      db.close()
    }
  }

  private openConnection(): BetterSqlite3.Database {
    const db = new BetterSqlite3(this.databasePath)
    db.pragma('foreign_keys = ON')
    return db
  }

  private tryGetAccountId(db: BetterSqlite3.Database, profileName: string): number | null {
    const row = db.prepare('SELECT id FROM accounts WHERE name = ? COLLATE NOCASE LIMIT 1').get(profileName) as { id?: number } | undefined
    return row?.id != null ? Number(row.id) : null
  }

  private writeMaterializedFile(filePath: string, content: string): void {
    if (!content.trim()) {
      if (fs.existsSync(filePath)) {
        fs.rmSync(filePath, { force: true })
      }
      return
    }

    writeTextNoBom(filePath, content)
  }

  private writeProfileMetadata(profileDirectory: string, name: string, provider: string, revision: number, updatedAt: string): void {
    writeTextNoBom(
      path.join(profileDirectory, 'profile.json'),
      JSON.stringify(
        {
          name,
          model_provider: provider,
          revision,
          updated_at: updatedAt
        },
        null,
        2
      )
    )
  }

  private normalizeStoreKey(sharedStoreHome: string | null | undefined): string {
    return normalizeDirectoryKey(sharedStoreHome)
  }

  private inferProvider(configToml: string, authJson: string): string {
    const configuredProvider = tryReadTopLevelTomlStringValue(configToml, 'model_provider')
    if (configuredProvider?.trim()) {
      return normalizeProvider(configuredProvider)
    }

    if (authJson.trim()) {
      try {
        const parsed = JSON.parse(authJson) as Record<string, unknown>
        if (typeof parsed.OPENAI_API_KEY === 'string' && parsed.OPENAI_API_KEY.trim()) {
          return 'openai'
        }
      } catch {
        // Best effort detection.
      }
    }

    return ''
  }

  private createTimestamp(): string {
    return new Date().toISOString()
  }

  private parseTimestamp(value: string | null | undefined): string {
    const parsed = value ? new Date(value) : new Date()
    return Number.isNaN(parsed.getTime()) ? new Date().toISOString() : parsed.toISOString()
  }
}
