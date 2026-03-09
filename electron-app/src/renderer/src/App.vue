<template>
  <div class="app-shell">
    <transition name="boot-fade">
      <div v-if="booting" class="boot-screen">
        <div class="boot-card">
          <div class="boot-mark">Codex Home Manager</div>
          <h1>正在准备工作台</h1>
          <p>{{ bootStage }}</p>
          <el-progress :percentage="bootProgress" :stroke-width="10" :show-text="false" status="success" />
        </div>
      </div>
    </transition>

    <div class="page-wrap">
      <header class="hero-panel">
        <div class="hero-copy">
          <h1>Codex 共享会话与账号管理台</h1>
          <p>用共享仓承接会话，用账号库存承接 auth/config，再把运行目录作为真正的 CODEX_HOME 投影出去。</p>
        </div>

        <div class="hero-metrics">
          <article v-for="metric in metrics" :key="metric.label" class="metric-tile">
            <span class="metric-label">{{ metric.label }}</span>
            <strong>{{ metric.value }}</strong>
            <small>{{ metric.note }}</small>
          </article>
        </div>

        <div class="hero-actions">
          <el-button type="primary" :loading="working && busyText === '准备共享仓'" @click="prepareWorkspace">
            准备共享仓
          </el-button>
          <el-button :loading="working && busyText === '同步运行目录'" @click="syncRuntime(false)">
            同步运行目录
          </el-button>
          <el-button type="success" :loading="working && busyText === '同步并启动 Codex'" @click="syncAndLaunch">
            同步并启动
          </el-button>
          <el-button type="warning" plain @click="refreshAll">
            刷新全部
          </el-button>
          <el-button type="danger" plain @click="closeRunningCodex">
            关闭 Codex
          </el-button>
        </div>
      </header>

      <main class="page-main">
        <section class="setup-stack">
          <el-card class="surface-card" shadow="never">
            <template #header>
              <div class="card-title-row">
                <div>
                  <h2>路径与运行设置</h2>
                  <span>目录全部可持久化，跨分辨率下自动伸缩。</span>
                </div>
                <el-button text @click="refreshExecutable">重新识别 Codex.exe</el-button>
              </div>
            </template>

            <div class="path-grid">
              <div v-for="field in pathFields" :key="field.key" class="path-item">
                <label>{{ field.label }}</label>
                <div class="path-input-row">
                  <el-input
                    :model-value="getSettingValue(field.key)"
                    :placeholder="field.placeholder"
                    clearable
                    @update:model-value="setSettingValue(field.key, $event)"
                  />
                  <el-button @click="browsePath(field)">{{ field.kind === 'file' ? '选文件' : '浏览' }}</el-button>
                </div>
              </div>
            </div>

            <div class="option-strip">
              <el-checkbox v-model="refreshUpdatedAt">导入会话时刷新更新时间</el-checkbox>
              <el-checkbox v-model="addWorkspaceHint">导入会话时附加工作区提示</el-checkbox>
              <el-checkbox v-model="overwriteRuntimeConfig">同步时覆盖运行目录 auth/config</el-checkbox>
            </div>

            <div class="footnote">
              账号数据库：{{ defaultPaths?.databasePath || '加载中' }}
              <br />
              物化账号目录：{{ defaultPaths?.materializedProfilesRoot || '加载中' }}
            </div>
          </el-card>

          <el-card class="surface-card" shadow="never">
            <template #header>
              <div class="card-title-row">
                <div>
                  <h2>账号库存</h2>
                  <span>账号的 `auth.json` 与 `config.toml` 存数据库，使用时再落地。</span>
                </div>
                <el-tag effect="plain" type="success">当前默认启动账号：{{ currentDefaultProfile || '未设置' }}</el-tag>
              </div>
            </template>

            <div class="profile-toolbar">
              <el-select
                v-model="settings.selectedProfile"
                class="profile-select"
                filterable
                clearable
                placeholder="选择账号"
              >
                <el-option v-for="profile in profiles" :key="profile.id" :label="profile.name" :value="profile.name" />
              </el-select>

              <el-space wrap>
                <el-button @click="saveCurrentAccount">保存当前账号</el-button>
                <el-button @click="createEmptyProfile">新建空账号</el-button>
                <el-button @click="renameSelectedProfile" :disabled="!settings.selectedProfile">重命名</el-button>
                <el-button type="danger" plain :disabled="!settings.selectedProfile" @click="deleteSelectedProfile">删除账号</el-button>
                <el-button type="primary" plain :disabled="!settings.selectedProfile" @click="editSelectedProfileContent">编辑内容</el-button>
                <el-button @click="importProfileFromDirectory">导入账号目录</el-button>
                <el-button :disabled="!settings.selectedProfile" @click="exportSelectedProfile">导出账号目录</el-button>
                <el-button type="warning" plain :disabled="!settings.selectedProfile" @click="setCurrentDefaultProfile">设为当前共享仓默认</el-button>
                <el-button type="success" plain :disabled="!settings.selectedProfile" @click="applySelectedProfile(false)">应用当前账号</el-button>
                <el-button type="success" :disabled="!settings.selectedProfile" @click="applySelectedProfile(true)">切换并启动</el-button>
                <el-button text @click="openMappingsDialog">管理默认映射</el-button>
              </el-space>
            </div>

            <div class="profile-summary">
              <div class="summary-item">
                <span>当前账号目录</span>
                <strong>{{ settings.authHome || '未指定' }}</strong>
              </div>
              <div class="summary-item">
                <span>账号供应方</span>
                <strong>{{ formatProvider(selectedProfileMeta?.modelProvider || status.effectiveProvider) }}</strong>
              </div>
              <div class="summary-item">
                <span>修订版</span>
                <strong>{{ selectedProfileMeta?.revision ?? '-' }}</strong>
              </div>
              <div class="summary-item">
                <span>更新时间</span>
                <strong>{{ formatDateTime(selectedProfileMeta?.updatedAt) }}</strong>
              </div>
            </div>
          </el-card>
        </section>

        <section class="session-grid">
          <section class="workspace-column">
          <el-card class="surface-card session-card" shadow="never">
            <template #header>
              <div class="card-title-row">
                <div>
                  <h2>会话列表</h2>
                </div>
                <el-radio-group v-model="sessionScope" size="small">
                  <el-radio-button label="source">源会话</el-radio-button>
                  <el-radio-button label="shared">共享仓</el-radio-button>
                </el-radio-group>
              </div>
            </template>

            <div class="session-actions">
              <el-tag effect="plain">源会话 {{ sourceSessions.length }}</el-tag>
              <el-tag effect="plain" type="success">共享仓 {{ sharedSessions.length }}</el-tag>
              <el-space wrap>
                <el-button @click="refreshSessions('source')">刷新源会话</el-button>
                <el-button @click="refreshSessions('shared')">刷新共享仓</el-button>
                <el-button type="primary" :disabled="!selectedSourceSession" @click="importSelectedSession(false)">导入选中会话</el-button>
                <el-button type="success" plain :disabled="!selectedSourceSession" @click="importSelectedSession(true)">导入并同步</el-button>
              </el-space>
            </div>

            <div class="session-table-wrap" v-loading="loadingSessions">
              <el-table
                :data="visibleSessions"
                row-key="id"
                height="100%"
                scrollbar-always-on
                highlight-current-row
                @row-click="selectSession"
              >
                <el-table-column prop="title" label="标题" min-width="220" show-overflow-tooltip />
                <el-table-column prop="modelProvider" label="提供方" width="110">
                  <template #default="{ row }">
                    {{ formatProvider(row.modelProvider) }}
                  </template>
                </el-table-column>
                <el-table-column prop="updatedAt" label="更新时间" width="180">
                  <template #default="{ row }">
                    {{ formatDateTime(row.updatedAt) }}
                  </template>
                </el-table-column>
                <el-table-column prop="cwd" label="工作目录" min-width="280" show-overflow-tooltip />
              </el-table>
            </div>
          </el-card>
          </section>

          <aside class="side-column">
            <el-card class="surface-card detail-card" shadow="never">
              <template #header>
                <div class="card-title-row">
                  <div>
                    <h2>会话详情</h2>
                    <span>{{ selectedSession ? '当前选中会话的元信息' : '点击左侧会话查看详情' }}</span>
                  </div>
                </div>
              </template>

              <template v-if="selectedSession">
                <div class="detail-hero">
                  <h3>{{ selectedSession.title }}</h3>
                  <el-tag size="small" effect="plain">{{ formatProvider(selectedSession.modelProvider) }}</el-tag>
                </div>
                <div class="detail-list">
                  <div class="detail-row"><span>ID</span><strong>{{ selectedSession.id }}</strong></div>
                  <div class="detail-row"><span>来源</span><strong>{{ sessionScope === 'source' ? '源目录' : '共享仓' }}</strong></div>
                  <div class="detail-row"><span>工作目录</span><strong>{{ selectedSession.cwd || '未记录' }}</strong></div>
                  <div class="detail-row"><span>会话文件</span><strong>{{ selectedSession.sessionPath || '仅索引/数据库记录' }}</strong></div>
                  <div class="detail-row"><span>创建时间</span><strong>{{ formatDateTime(selectedSession.createdAt) }}</strong></div>
                  <div class="detail-row"><span>更新时间</span><strong>{{ formatDateTime(selectedSession.updatedAt) }}</strong></div>
                </div>
              </template>
              <el-empty v-else description="暂无选中会话" />
            </el-card>

            <el-card class="surface-card log-card" shadow="never">
              <template #header>
                <div class="card-title-row">
                  <div>
                    <h2>操作日志</h2>
                    <span>{{ busyText ? `${busyText}进行中...` : '记录最近一次操作链路' }}</span>
                  </div>
                  <el-button text @click="logs = []">清空</el-button>
                </div>
              </template>

              <div v-if="logs.length" class="log-list">
                <article v-for="item in logs" :key="item.id" class="log-item" :data-tone="item.tone">
                  <span>{{ item.time }}</span>
                  <p>{{ item.text }}</p>
                </article>
              </div>
              <el-empty v-else description="还没有日志" />
            </el-card>
          </aside>
        </section>
      </main>
    </div>

    <el-dialog v-model="profileEditor.visible" :title="`${profileEditor.name} - 编辑账号内容`" width="min(1200px, 92vw)">
      <div class="editor-grid">
        <div class="editor-pane">
          <label>auth.json</label>
          <el-input v-model="profileEditor.authJson" type="textarea" :rows="18" resize="vertical" />
        </div>
        <div class="editor-pane">
          <label>config.toml</label>
          <el-input v-model="profileEditor.configToml" type="textarea" :rows="18" resize="vertical" />
        </div>
      </div>
      <template #footer>
        <el-button @click="profileEditor.visible = false">取消</el-button>
        <el-button type="primary" :loading="profileEditor.saving" @click="saveProfileEditor">保存内容</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="mappingsDialogVisible" title="共享仓默认启动账号映射" width="min(960px, 92vw)">
      <div class="mapping-actions">
        <el-button @click="addMappingRow">新增一条</el-button>
      </div>
      <div class="mapping-grid">
        <div v-for="(row, index) in mappingRows" :key="`${row.storeKey}-${index}`" class="mapping-row">
          <el-input v-model="row.storeKey" placeholder="共享仓目录标识" />
          <el-select v-model="row.profileName" filterable clearable placeholder="账号">
            <el-option v-for="profile in profiles" :key="profile.id" :label="profile.name" :value="profile.name" />
          </el-select>
          <el-button type="danger" plain @click="removeMappingRow(index)">删除</el-button>
        </div>
      </div>
      <template #footer>
        <el-button @click="mappingsDialogVisible = false">取消</el-button>
        <el-button type="primary" @click="saveMappingRows">保存映射</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import type {
  AppPathSettings,
  AppStatusSnapshot,
  DefaultPaths,
  ManagedProfileContent,
  ProviderProfile,
  SessionRecord
} from '@shared/contracts'

type SessionScope = 'source' | 'shared'
type LogTone = 'info' | 'success' | 'warning' | 'error'
type PathKey = 'stateHome' | 'authHome' | 'profilesRoot' | 'sharedStoreHome' | 'targetHome' | 'appExePath'

interface LogEntry {
  id: number
  time: string
  tone: LogTone
  text: string
}

interface MappingRow {
  storeKey: string
  profileName: string
}

interface ProfileEditorState {
  visible: boolean
  name: string
  authJson: string
  configToml: string
  saving: boolean
}

const pathFields: Array<{ key: PathKey; label: string; placeholder: string; kind: 'directory' | 'file' }> = [
  { key: 'stateHome', label: '会话来源目录', placeholder: '旧会话所在的 .codex 目录', kind: 'directory' },
  { key: 'authHome', label: '当前账号目录', placeholder: '当前使用中的 auth/config 目录', kind: 'directory' },
  { key: 'profilesRoot', label: '账号库存目录', placeholder: '兼容旧版账号目录导入路径', kind: 'directory' },
  { key: 'sharedStoreHome', label: '共享仓目录', placeholder: '导入后的会话统一存放到这里', kind: 'directory' },
  { key: 'targetHome', label: '运行目录', placeholder: '启动 Codex 时真正使用的 CODEX_HOME', kind: 'directory' },
  { key: 'appExePath', label: 'Codex 程序', placeholder: '选择 Codex.exe，或让软件自动识别', kind: 'file' }
]

function createSettings(): AppPathSettings {
  return {
    stateHome: '',
    authHome: '',
    profilesRoot: '',
    selectedProfile: '',
    defaultLaunchProfile: '',
    sharedStoreDefaultLaunchProfiles: {},
    sharedStoreHome: '',
    targetHome: '',
    appExePath: '',
    autoSyncConfigChanges: true
  }
}

function createStatus(): AppStatusSnapshot {
  return {
    codexRunning: false,
    effectiveProvider: '',
    defaultLaunchProfile: '',
    selectedProfile: '',
    sharedStoreHome: '',
    runtimeHome: '',
    profilesCount: 0
  }
}

const booting = ref(true)
const bootStage = ref('正在读取默认路径...')
const bootProgress = ref(8)
const initialized = ref(false)
const working = ref(false)
const busyText = ref('')
const loadingSessions = ref(false)
const defaultPaths = ref<DefaultPaths | null>(null)
const status = ref<AppStatusSnapshot>(createStatus())
const settings = reactive<AppPathSettings>(createSettings())
const profiles = ref<ProviderProfile[]>([])
const sourceSessions = ref<SessionRecord[]>([])
const sharedSessions = ref<SessionRecord[]>([])
const sessionScope = ref<SessionScope>('source')
const selectedSourceSessionId = ref('')
const selectedSharedSessionId = ref('')
const refreshUpdatedAt = ref(true)
const addWorkspaceHint = ref(true)
const overwriteRuntimeConfig = ref(true)
const mappingsDialogVisible = ref(false)
const mappingRows = ref<MappingRow[]>([])
const profileEditor = reactive<ProfileEditorState>({
  visible: false,
  name: '',
  authJson: '',
  configToml: '',
  saving: false
})
const logs = ref<LogEntry[]>([])

const visibleSessions = computed(() => (sessionScope.value === 'source' ? sourceSessions.value : sharedSessions.value))
const selectedSession = computed(() =>
  visibleSessions.value.find((item) => item.id === (sessionScope.value === 'source' ? selectedSourceSessionId.value : selectedSharedSessionId.value)) ?? null
)
const selectedSourceSession = computed(() => sourceSessions.value.find((item) => item.id === selectedSourceSessionId.value) ?? null)
const selectedProfileMeta = computed(() => profiles.value.find((item) => item.name === settings.selectedProfile) ?? null)
const currentDefaultProfile = computed(() => {
  const storeKey = normalizeStoreKey(settings.sharedStoreHome)
  return settings.sharedStoreDefaultLaunchProfiles[storeKey] || settings.defaultLaunchProfile || ''
})
const metrics = computed(() => [
  {
    label: 'Codex 状态',
    value: status.value.codexRunning ? '运行中' : '已停止',
    note: status.value.codexRunning ? '运行目录已被占用' : '可安全同步'
  },
  {
    label: '当前提供方',
    value: formatProvider(status.value.effectiveProvider),
    note: '由运行目录 auth/config 推断'
  },
  {
    label: '账号库存',
    value: String(status.value.profilesCount),
    note: '数据库中的可用账号数'
  },
  {
    label: '共享仓默认账号',
    value: currentDefaultProfile.value || '未设置',
    note: '针对当前共享仓路径生效'
  }
])

let saveTimer: ReturnType<typeof setTimeout> | null = null

function getSettingsSnapshot(): AppPathSettings {
  return {
    stateHome: settings.stateHome,
    authHome: settings.authHome,
    profilesRoot: settings.profilesRoot,
    selectedProfile: settings.selectedProfile,
    defaultLaunchProfile: settings.defaultLaunchProfile,
    sharedStoreDefaultLaunchProfiles: { ...settings.sharedStoreDefaultLaunchProfiles },
    sharedStoreHome: settings.sharedStoreHome,
    targetHome: settings.targetHome,
    appExePath: settings.appExePath,
    autoSyncConfigChanges: settings.autoSyncConfigChanges
  }
}

watch(
  settings,
  () => {
    if (!initialized.value) {
      return
    }

    if (saveTimer) {
      window.clearTimeout(saveTimer)
    }

    saveTimer = setTimeout(() => {
      window.codexApi.saveSettings(getSettingsSnapshot())
    }, 360)
  },
  { deep: true }
)

watch(sourceSessions, () => ensureSessionSelection('source'))
watch(sharedSessions, () => ensureSessionSelection('shared'))

onMounted(() => {
  bootstrap().catch((error) => {
    const message = getErrorMessage(error)
    pushLog('error', `初始化失败：${message}`)
    ElMessage.error(message)
    booting.value = false
  })
})

async function bootstrap(): Promise<void> {
  bootStage.value = '正在读取默认路径...'
  bootProgress.value = 18
  defaultPaths.value = await window.codexApi.getDefaultPaths()

  bootStage.value = '正在加载上次设置...'
  bootProgress.value = 34
  const savedSettings = await window.codexApi.loadSettings()

  bootStage.value = '正在迁移默认账号映射...'
  bootProgress.value = 52
  const migratedMappings = await window.codexApi.migrateSharedStoreDefaultLaunchProfiles(savedSettings?.sharedStoreDefaultLaunchProfiles ?? null)

  Object.assign(settings, {
    ...createSettings(),
    stateHome: defaultPaths.value.stateHome,
    authHome: defaultPaths.value.stateHome,
    profilesRoot: defaultPaths.value.profilesRoot,
    sharedStoreHome: defaultPaths.value.sharedStoreHome,
    targetHome: defaultPaths.value.targetHome,
    appExePath: defaultPaths.value.appExePath,
    ...(savedSettings ?? {}),
    sharedStoreDefaultLaunchProfiles: { ...migratedMappings }
  })

  if (!settings.selectedProfile) {
    settings.selectedProfile = currentDefaultProfile.value
  }

  bootStage.value = '正在加载账号与会话...'
  bootProgress.value = 76
  await Promise.all([reloadProfiles(settings.selectedProfile), refreshSessions('source'), refreshSessions('shared')])

  bootStage.value = '正在校准运行状态...'
  bootProgress.value = 92
  await refreshStatus()

  initialized.value = true
  pushLog('success', '桌面客户端已就绪。')
  bootProgress.value = 100
  setTimeout(() => {
    booting.value = false
  }, 260)
}

function normalizeStoreKey(value: string): string {
  return value.trim().replace(/[\\/]+$/u, '').toLowerCase()
}

function getSettingValue(key: PathKey): string {
  return settings[key]
}

function setSettingValue(key: PathKey, value: string): void {
  settings[key] = value
}

function ensureSessionSelection(scope: SessionScope): void {
  const sessions = scope === 'source' ? sourceSessions.value : sharedSessions.value
  const selected = scope === 'source' ? selectedSourceSessionId.value : selectedSharedSessionId.value
  const exists = sessions.some((item) => item.id === selected)
  const nextId = exists ? selected : (sessions[0]?.id ?? '')

  if (scope === 'source') {
    selectedSourceSessionId.value = nextId
  } else {
    selectedSharedSessionId.value = nextId
  }
}

function selectSession(session: SessionRecord): void {
  if (sessionScope.value === 'source') {
    selectedSourceSessionId.value = session.id
  } else {
    selectedSharedSessionId.value = session.id
  }
}

function pushLog(tone: LogTone, text: string): void {
  logs.value.unshift({
    id: Date.now() + Math.floor(Math.random() * 1000),
    time: new Date().toLocaleTimeString('zh-CN', { hour12: false }),
    tone,
    text
  })

  if (logs.value.length > 120) {
    logs.value = logs.value.slice(0, 120)
  }
}

function getErrorMessage(error: unknown): string {
  if (error instanceof Error && error.message.trim()) {
    return error.message.replace(/^Error invoking remote method '[^']+':\s*/i, '').trim()
  }

  return '发生未知错误。'
}

function formatDateTime(value?: string | null): string {
  if (!value) {
    return '-'
  }

  const date = new Date(value)
  if (Number.isNaN(date.getTime())) {
    return value
  }

  return date.toLocaleString('zh-CN', { hour12: false })
}

function formatProvider(value?: string | null): string {
  if (!value?.trim()) {
    return '未识别'
  }

  const normalized = value.trim().toLowerCase()
  if (normalized === 'openai') {
    return 'OpenAI'
  }

  return normalized
}

async function perform<T>(label: string, action: () => Promise<T> | T, successText?: string): Promise<T | null> {
  if (working.value) {
    return null
  }

  working.value = true
  busyText.value = label
  pushLog('info', `${label}...`)

  try {
    const result = await action()
    pushLog('success', successText ?? `${label}完成。`)
    return result
  } catch (error) {
    const message = getErrorMessage(error)
    pushLog('error', `${label}失败：${message}`)
    ElMessage.error(message)
    return null
  } finally {
    working.value = false
    busyText.value = ''
  }
}

async function browsePath(field: { key: PathKey; label: string; kind: 'directory' | 'file' }): Promise<void> {
  const current = getSettingValue(field.key)
  const selected = field.kind === 'file'
    ? await window.codexApi.browseFile({
        title: `选择${field.label}`,
        defaultPath: current,
        filters: [{ name: 'Executable', extensions: ['exe'] }]
      })
    : await window.codexApi.browseDirectory({
        title: `选择${field.label}`,
        defaultPath: current
      })

  if (selected) {
    setSettingValue(field.key, selected)
  }
}

async function refreshExecutable(): Promise<void> {
  const executable = await perform('自动识别 Codex 程序', () => window.codexApi.findCodexAppExecutable(), '已刷新 Codex 程序路径。')
  if (executable != null) {
    settings.appExePath = executable || settings.appExePath
  }
}

async function safeLoadSessions(home: string): Promise<SessionRecord[]> {
  if (!home.trim()) {
    return []
  }

  try {
    return await window.codexApi.loadSessions(home)
  } catch (error) {
    const message = getErrorMessage(error)
    if (/不存在|does not exist|not found/i.test(message)) {
      return []
    }

    throw error
  }
}

async function refreshSessions(scope: SessionScope): Promise<void> {
  loadingSessions.value = true
  try {
    if (scope === 'source') {
      sourceSessions.value = await safeLoadSessions(settings.stateHome)
      return
    }

    sharedSessions.value = await safeLoadSessions(settings.sharedStoreHome)
  } finally {
    loadingSessions.value = false
  }
}

async function reloadProfiles(preferredName?: string): Promise<void> {
  profiles.value = await window.codexApi.listProfiles(settings.profilesRoot)
  const preferred = preferredName || settings.selectedProfile || currentDefaultProfile.value
  if (preferred && profiles.value.some((item) => item.name === preferred)) {
    settings.selectedProfile = preferred
    return
  }

  settings.selectedProfile = profiles.value[0]?.name ?? ''
}

async function refreshStatus(): Promise<void> {
  status.value = await window.codexApi.getStatus(getSettingsSnapshot())
}

async function refreshAll(): Promise<void> {
  await perform('刷新全部数据', async () => {
    await Promise.all([reloadProfiles(settings.selectedProfile), refreshSessions('source'), refreshSessions('shared'), refreshStatus()])
  })
}

async function prepareWorkspace(): Promise<void> {
  const result = await perform('准备共享仓', () =>
    window.codexApi.prepareSharedWorkspace(settings.authHome || null, settings.sharedStoreHome, settings.targetHome, overwriteRuntimeConfig.value)
  )

  if (result !== null) {
    await refreshStatus()
  }
}

async function importSelectedSession(syncRuntime: boolean): Promise<void> {
  if (!selectedSourceSession.value) {
    ElMessage.warning('请先在“源会话”里选中一条会话。')
    return
  }

  const result = syncRuntime
    ? await perform('导入会话并同步运行目录', () =>
        window.codexApi.importSessionToSharedStore(
          settings.stateHome,
          settings.sharedStoreHome,
          settings.authHome || null,
          settings.targetHome,
          selectedSourceSession.value!.id,
          refreshUpdatedAt.value,
          addWorkspaceHint.value
        )
      )
    : await perform('导入会话到共享仓', () =>
        window.codexApi.importSessionToSharedStoreOnly(
          settings.stateHome,
          settings.sharedStoreHome,
          selectedSourceSession.value!.id,
          refreshUpdatedAt.value,
          addWorkspaceHint.value
        )
      )
  if (result !== null) {
    await Promise.all([refreshSessions('shared'), refreshStatus()])
  }
}

async function syncRuntime(showSuccess = true): Promise<void> {
  const result = await perform('同步运行目录', () =>
    window.codexApi.syncRuntimeHome(settings.sharedStoreHome, settings.authHome || null, settings.targetHome, overwriteRuntimeConfig.value),
    showSuccess ? '运行目录同步完成。' : undefined
  )

  if (result !== null) {
    await refreshStatus()
  }
}

async function syncAndLaunch(): Promise<void> {
  const result = await perform('同步并启动 Codex', () =>
    window.codexApi.syncAndLaunchCodexApp(
      settings.sharedStoreHome,
      settings.authHome || null,
      settings.targetHome,
      settings.appExePath || null
    )
  )

  if (result !== null) {
    await refreshStatus()
  }
}

async function closeRunningCodex(): Promise<void> {
  const confirmed = await ElMessageBox.confirm('这会强制关闭正在运行的 Codex。是否继续？', '关闭 Codex', {
    type: 'warning',
    confirmButtonText: '关闭',
    cancelButtonText: '取消'
  }).catch(() => false)

  if (!confirmed) {
    return
  }

  const count = await perform('关闭运行中的 Codex', () => window.codexApi.closeRunningCodexApp())
  if (count !== null) {
    await refreshStatus()
  }
}

async function saveCurrentAccount(): Promise<void> {
  let profileName = settings.selectedProfile.trim()
  if (!profileName) {
    const promptResult = await ElMessageBox.prompt('输入要保存到库存中的账号名。', '保存当前账号', {
      confirmButtonText: '保存',
      cancelButtonText: '取消',
      inputPlaceholder: '例如：rightcode'
    }).catch(() => null)

    if (!promptResult?.value?.trim()) {
      return
    }

    profileName = promptResult.value.trim()
  }

  const profile = await perform('保存当前账号', () =>
    window.codexApi.saveProfile(settings.profilesRoot, profileName, settings.authHome, true)
  )

  if (profile) {
    settings.selectedProfile = profile.name
    await Promise.all([reloadProfiles(profile.name), refreshStatus()])
  }
}

async function createEmptyProfile(): Promise<void> {
  const promptResult = await ElMessageBox.prompt('输入新账号名称。', '新建空账号', {
    confirmButtonText: '创建',
    cancelButtonText: '取消',
    inputPlaceholder: '例如：new-profile'
  }).catch(() => null)

  if (!promptResult?.value?.trim()) {
    return
  }

  const created = await perform('新建空账号', () => window.codexApi.createEmptyProfile(promptResult.value.trim()))
  if (created) {
    settings.selectedProfile = created.name
    settings.authHome = created.directoryPath
    await Promise.all([reloadProfiles(created.name), refreshStatus()])
  }
}

async function renameSelectedProfile(): Promise<void> {
  if (!settings.selectedProfile) {
    ElMessage.warning('请先选择一个账号。')
    return
  }

  const promptResult = await ElMessageBox.prompt('输入新的账号名称。', '重命名账号', {
    confirmButtonText: '重命名',
    cancelButtonText: '取消',
    inputValue: settings.selectedProfile
  }).catch(() => null)

  if (!promptResult?.value?.trim()) {
    return
  }

  const renamed = await perform('重命名账号', () =>
    window.codexApi.renameProfile(settings.selectedProfile, promptResult.value.trim())
  )

  if (renamed) {
    settings.selectedProfile = renamed.name
    settings.authHome = renamed.directoryPath
    settings.sharedStoreDefaultLaunchProfiles = await window.codexApi.loadSharedStoreDefaultLaunchProfiles()
    await Promise.all([reloadProfiles(renamed.name), refreshStatus()])
  }
}

async function deleteSelectedProfile(): Promise<void> {
  if (!settings.selectedProfile) {
    return
  }

  const deletingProfile = await window.codexApi.getProfile(settings.profilesRoot, settings.selectedProfile)
  const confirmed = await ElMessageBox.confirm(`确定要删除账号“${settings.selectedProfile}”吗？`, '删除账号', {
    type: 'warning',
    confirmButtonText: '删除',
    cancelButtonText: '取消'
  }).catch(() => false)

  if (!confirmed) {
    return
  }

  const deleted = await perform('删除账号', () => window.codexApi.deleteProfile(settings.selectedProfile))
  if (deleted !== null) {
    if (settings.authHome && settings.authHome === deletingProfile.directoryPath) {
      settings.authHome = ''
    }
    settings.sharedStoreDefaultLaunchProfiles = await window.codexApi.loadSharedStoreDefaultLaunchProfiles()
    await Promise.all([reloadProfiles(''), refreshStatus()])
  }
}

async function editSelectedProfileContent(): Promise<void> {
  if (!settings.selectedProfile) {
    ElMessage.warning('请先选择一个账号。')
    return
  }

  const content = await perform<ManagedProfileContent>('加载账号内容', () =>
    window.codexApi.getOrCreateProfileContent(settings.selectedProfile),
    '账号内容已加载。'
  )

  if (!content) {
    return
  }

  profileEditor.name = content.name
  profileEditor.authJson = content.authJson
  profileEditor.configToml = content.configToml
  profileEditor.visible = true
}

async function saveProfileEditor(): Promise<void> {
  profileEditor.saving = true
  try {
    const saved = await window.codexApi.saveProfileContent(profileEditor.name, profileEditor.authJson, profileEditor.configToml)
    settings.selectedProfile = saved.name
    const profile = await window.codexApi.getProfile(settings.profilesRoot, saved.name)
    settings.authHome = profile.directoryPath
    await Promise.all([reloadProfiles(saved.name), refreshStatus()])
    profileEditor.visible = false
    pushLog('success', `已保存账号内容：${saved.name}`)
    ElMessage.success('账号内容已保存。')
  } catch (error) {
    const message = getErrorMessage(error)
    pushLog('error', `保存账号内容失败：${message}`)
    ElMessage.error(message)
  } finally {
    profileEditor.saving = false
  }
}

async function importProfileFromDirectory(): Promise<void> {
  const selectedDirectory = await window.codexApi.browseDirectory({
    title: '选择要导入的账号目录',
    defaultPath: settings.profilesRoot
  })

  if (!selectedDirectory) {
    return
  }

  const profile = await perform('导入账号目录', () =>
    window.codexApi.importProfile(settings.profilesRoot, selectedDirectory, null, true)
  )

  if (profile) {
    settings.selectedProfile = profile.name
    await Promise.all([reloadProfiles(profile.name), refreshStatus()])
  }
}

async function exportSelectedProfile(): Promise<void> {
  if (!settings.selectedProfile) {
    ElMessage.warning('请先选择一个账号。')
    return
  }

  const targetRoot = await window.codexApi.browseDirectory({
    title: '选择导出目录',
    defaultPath: settings.profilesRoot
  })

  if (!targetRoot) {
    return
  }

  await perform('导出账号目录', () =>
    window.codexApi.exportProfile(settings.profilesRoot, settings.selectedProfile, targetRoot, true)
  )
}

async function setCurrentDefaultProfile(): Promise<void> {
  if (!settings.selectedProfile) {
    ElMessage.warning('请先选择一个账号。')
    return
  }

  const storeKey = normalizeStoreKey(settings.sharedStoreHome)
  if (!storeKey) {
    ElMessage.warning('请先填写共享仓目录。')
    return
  }

  settings.defaultLaunchProfile = settings.selectedProfile
  settings.sharedStoreDefaultLaunchProfiles = {
    ...settings.sharedStoreDefaultLaunchProfiles,
    [storeKey]: settings.selectedProfile
  }

  await window.codexApi.saveSharedStoreDefaultLaunchProfiles(settings.sharedStoreDefaultLaunchProfiles)
  await refreshStatus()
  pushLog('success', `已为当前共享仓设置默认账号：${settings.selectedProfile}`)
}

function openMappingsDialog(): void {
  mappingRows.value = Object.entries(settings.sharedStoreDefaultLaunchProfiles)
    .sort(([left], [right]) => left.localeCompare(right, undefined, { sensitivity: 'accent' }))
    .map(([storeKey, profileName]) => ({ storeKey, profileName }))

  if (mappingRows.value.length === 0 && settings.sharedStoreHome.trim()) {
    mappingRows.value.push({ storeKey: normalizeStoreKey(settings.sharedStoreHome), profileName: settings.selectedProfile })
  }

  mappingsDialogVisible.value = true
}

function addMappingRow(): void {
  mappingRows.value.push({
    storeKey: normalizeStoreKey(settings.sharedStoreHome),
    profileName: settings.selectedProfile
  })
}

function removeMappingRow(index: number): void {
  mappingRows.value.splice(index, 1)
}

async function saveMappingRows(): Promise<void> {
  const nextMappings = Object.fromEntries(
    mappingRows.value
      .map((row) => [normalizeStoreKey(row.storeKey), row.profileName.trim()] as const)
      .filter(([storeKey, profileName]) => Boolean(storeKey && profileName))
  )

  settings.sharedStoreDefaultLaunchProfiles = nextMappings
  await window.codexApi.saveSharedStoreDefaultLaunchProfiles(nextMappings)
  await refreshStatus()
  mappingsDialogVisible.value = false
  pushLog('success', `已保存共享仓默认账号映射，共 ${Object.keys(nextMappings).length} 条。`)
}

async function applySelectedProfile(launchAfter: boolean): Promise<void> {
  if (!settings.selectedProfile) {
    ElMessage.warning('请先选择一个账号。')
    return
  }

  const profile = await perform('加载账号目录', () => window.codexApi.getProfile(settings.profilesRoot, settings.selectedProfile), '账号目录已定位。')
  if (!profile) {
    return
  }

  settings.authHome = profile.directoryPath
  const result = await perform(launchAfter ? '切换账号并启动 Codex' : '应用当前账号', () =>
    launchAfter
      ? window.codexApi.syncAndLaunchCodexApp(settings.sharedStoreHome, profile.directoryPath, settings.targetHome, settings.appExePath || null)
      : window.codexApi.syncRuntimeHome(settings.sharedStoreHome, profile.directoryPath, settings.targetHome, true)
  )

  if (result !== null) {
    await refreshStatus()
  }
}
</script>

<style scoped>
.app-shell {
  min-height: 100vh;
  color: var(--cm-text);
}

.page-wrap {
  padding: 24px;
}

.hero-panel {
  display: grid;
  gap: 18px;
  padding: 24px;
  border: 1px solid var(--cm-line);
  border-radius: 28px;
  background: linear-gradient(135deg, rgba(255, 255, 255, 0.84), rgba(246, 252, 250, 0.92));
  box-shadow: var(--cm-shadow);
  backdrop-filter: blur(18px);
}

.hero-copy h1,
.card-title-row h2 {
  margin: 0;
  font-size: 28px;
  line-height: 1.1;
}

.hero-copy p,
.card-title-row span {
  margin: 0;
  color: var(--cm-text-soft);
}

.boot-mark {
  display: inline-flex;
  width: fit-content;
  padding: 8px 14px;
  border-radius: 999px;
  background: rgba(15, 139, 116, 0.1);
  color: var(--cm-accent-deep);
  font-size: 12px;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.hero-metrics,
.profile-summary {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 14px;
}

.metric-tile,
.summary-item {
  padding: 16px 18px;
  border: 1px solid rgba(15, 84, 74, 0.08);
  border-radius: 20px;
  background: rgba(255, 255, 255, 0.78);
}

.metric-label,
.summary-item span,
.detail-row span,
.log-item span,
.editor-pane label {
  display: block;
  color: var(--cm-text-soft);
  font-size: 12px;
}

.metric-tile strong,
.summary-item strong,
.detail-row strong {
  display: block;
  margin-top: 6px;
  font-size: 16px;
  word-break: break-word;
}

.metric-tile small {
  display: block;
  margin-top: 6px;
  color: var(--cm-text-soft);
}

.hero-actions,
.session-actions,
.option-strip,
.mapping-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  align-items: center;
}

.page-main,
.setup-stack,
.workspace-column,
.side-column {
  display: flex;
  flex-direction: column;
  gap: 18px;
}

.page-main {
  margin-top: 18px;
}

.session-grid {
  display: grid;
  grid-template-columns: minmax(0, 2fr) minmax(360px, 1fr);
  gap: 18px;
  align-items: start;
}

.surface-card {
  border: 1px solid var(--cm-line);
  background: var(--cm-surface);
  box-shadow: var(--cm-shadow);
  backdrop-filter: blur(12px);
}

.card-title-row {
  display: flex;
  justify-content: space-between;
  gap: 16px;
  align-items: center;
}

.path-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 14px 18px;
}

.path-item,
.editor-pane {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.path-item label,
.detail-hero h3 {
  margin: 0;
  font-weight: 700;
}

.path-input-row,
.profile-toolbar,
.mapping-row,
.detail-hero {
  display: flex;
  gap: 12px;
  align-items: center;
}

.path-input-row :deep(.el-input),
.profile-select,
.mapping-row :deep(.el-input),
.mapping-row :deep(.el-select) {
  flex: 1;
}

.footnote {
  margin-top: 16px;
  color: var(--cm-text-soft);
  font-size: 12px;
  line-height: 1.7;
}

.profile-toolbar {
  flex-direction: column;
  align-items: stretch;
}

.session-card :deep(.el-card__body),
.detail-card :deep(.el-card__body),
.log-card :deep(.el-card__body) {
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
}

.workspace-column,
.side-column {
  min-width: 0;
}

.detail-card {
  min-height: 420px;
}

.session-table-wrap {
  flex: 0 0 auto;
  height: 620px;
  min-height: 620px;
  max-height: 620px;
  overflow: hidden;
}

.session-table-wrap :deep(.el-table),
.session-table-wrap :deep(.el-table__inner-wrapper),
.session-table-wrap :deep(.el-table__body-wrapper) {
  height: 100%;
}

.detail-list,
.log-list,
.mapping-grid {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.detail-row,
.log-item,
.mapping-row {
  padding: 12px 14px;
  border: 1px solid rgba(15, 84, 74, 0.08);
  border-radius: 16px;
  background: rgba(255, 255, 255, 0.72);
}

.log-list {
  max-height: 560px;
  overflow: auto;
}

.log-item p {
  margin: 6px 0 0;
  white-space: pre-wrap;
}

.log-item[data-tone='error'] {
  border-color: rgba(216, 88, 63, 0.22);
}

.log-item[data-tone='success'] {
  border-color: rgba(15, 139, 116, 0.22);
}

.editor-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px;
}

.boot-screen {
  position: fixed;
  inset: 0;
  z-index: 20;
  display: grid;
  place-items: center;
  background: rgba(239, 244, 239, 0.8);
  backdrop-filter: blur(12px);
}

.boot-card {
  width: min(480px, calc(100vw - 40px));
  padding: 28px;
  border-radius: 28px;
  background: linear-gradient(155deg, rgba(255, 255, 255, 0.94), rgba(240, 248, 244, 0.94));
  box-shadow: 0 36px 80px rgba(15, 69, 61, 0.18);
}

.boot-card h1,
.boot-card p {
  margin: 0 0 12px;
}

.boot-fade-enter-active,
.boot-fade-leave-active {
  transition: opacity 0.28s ease;
}

.boot-fade-enter-from,
.boot-fade-leave-to {
  opacity: 0;
}

@media (max-width: 1360px) {
  .session-grid,
  .hero-metrics,
  .profile-summary,
  .path-grid,
  .editor-grid {
    grid-template-columns: 1fr;
  }

  .session-table-wrap {
    height: 560px;
    min-height: 560px;
    max-height: 560px;
  }
}

@media (max-width: 900px) {
  .page-wrap {
    padding: 14px;
  }

  .card-title-row,
  .path-input-row,
  .mapping-row,
  .detail-hero {
    flex-direction: column;
    align-items: stretch;
  }

  .session-table-wrap {
    height: 480px;
    min-height: 480px;
    max-height: 480px;
  }
}
</style>
