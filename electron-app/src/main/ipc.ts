import path from 'node:path'
import { app, BrowserWindow, dialog, ipcMain, type IpcMainInvokeEvent, type OpenDialogOptions } from 'electron'
import type { AppPathSettings, DefaultPaths, DirectoryChoiceOptions, FileChoiceOptions } from '@shared/contracts'
import { CodexManager } from './services/codex-manager'
import { ProfileStore } from './services/profile-store'
import { SettingsService } from './services/settings-service'
import {
  getDefaultProfilesRoot,
  getDefaultSharedStoreHome,
  getDefaultStateHome,
  getDefaultTargetHome,
  normalizeDirectoryKey
} from './utils/path-utils'

type AppServices = {
  manager: CodexManager
  profileStore: ProfileStore
  settingsService: SettingsService
  databasePath: string
  materializedProfilesRoot: string
}

let servicesCache: AppServices | null = null

function getLocalDataRoot(): string {
  const localAppData = process.env.LOCALAPPDATA?.trim()
  return localAppData
    ? path.join(localAppData, 'CodexHomeManager')
    : path.join(app.getPath('userData'), 'CodexHomeManager')
}

function getServices(): AppServices {
  if (servicesCache) {
    return servicesCache
  }

  const localDataRoot = getLocalDataRoot()
  const databasePath = path.join(localDataRoot, 'managed-accounts.db')
  const materializedProfilesRoot = path.join(localDataRoot, 'materialized-profiles')
  const settingsPath = path.join(localDataRoot, 'ui-settings.json')

  servicesCache = {
    manager: new CodexManager(),
    profileStore: new ProfileStore(databasePath, materializedProfilesRoot),
    settingsService: new SettingsService(settingsPath),
    databasePath,
    materializedProfilesRoot
  }

  return servicesCache
}

async function buildDefaultPaths(): Promise<DefaultPaths> {
  const { manager, databasePath, materializedProfilesRoot } = getServices()
  return {
    stateHome: getDefaultStateHome(),
    profilesRoot: getDefaultProfilesRoot(),
    sharedStoreHome: getDefaultSharedStoreHome(),
    targetHome: getDefaultTargetHome(),
    appExePath: (await manager.findCodexAppExecutable()) ?? '',
    databasePath,
    materializedProfilesRoot
  }
}

function getDialogParent(event: IpcMainInvokeEvent): BrowserWindow | undefined {
  return BrowserWindow.fromWebContents(event.sender) ?? BrowserWindow.getFocusedWindow() ?? undefined
}

async function choosePath(
  event: IpcMainInvokeEvent,
  options: OpenDialogOptions
): Promise<string | null> {
  const parent = getDialogParent(event)
  const result = parent
    ? await dialog.showOpenDialog(parent, options)
    : await dialog.showOpenDialog(options)

  return result.canceled ? null : (result.filePaths[0] ?? null)
}

function resolveCurrentDefaultProfile(settings: AppPathSettings, mappings: Record<string, string>): string {
  const storeKey = normalizeDirectoryKey(settings.sharedStoreHome)
  if (storeKey && mappings[storeKey]?.trim()) {
    return mappings[storeKey].trim()
  }

  return settings.defaultLaunchProfile?.trim() ?? ''
}

function handle<Args extends unknown[], Result>(
  channel: string,
  listener: (event: IpcMainInvokeEvent, ...args: Args) => Result | Promise<Result>
): void {
  ipcMain.removeHandler(channel)
  ipcMain.handle(channel, listener)
}

export function registerIpcHandlers(): void {
  handle('app:getDefaultPaths', async () => buildDefaultPaths())

  handle('app:loadSettings', async () => getServices().settingsService.load())

  handle('app:saveSettings', async (_event, settings: AppPathSettings) => {
    getServices().settingsService.save(settings)
  })

  handle('dialog:browseDirectory', async (event, options: DirectoryChoiceOptions) =>
    choosePath(event, {
      title: options.title,
      defaultPath: options.defaultPath,
      properties: ['openDirectory', 'createDirectory']
    })
  )

  handle('dialog:browseFile', async (event, options: FileChoiceOptions) =>
    choosePath(event, {
      title: options.title,
      defaultPath: options.defaultPath,
      filters: options.filters,
      properties: ['openFile']
    })
  )

  handle('codex:findExecutable', async () => getServices().manager.findCodexAppExecutable())
  handle('codex:isRunning', async () => getServices().manager.isCodexAppRunning())
  handle('codex:closeRunning', async () => getServices().manager.closeRunningCodexApp())

  handle('codex:getStatus', async (_event, settings: AppPathSettings) => {
    const { manager, profileStore } = getServices()
    const mappings = profileStore.migrateSharedStoreDefaultLaunchProfiles(settings.sharedStoreDefaultLaunchProfiles ?? null)
    const profilesCount = profileStore.listProfiles(settings.profilesRoot).length

    const effectiveProvider =
      manager.getEffectiveModelProvider(settings.targetHome) ||
      manager.getEffectiveModelProvider(settings.authHome) ||
      manager.getEffectiveModelProvider(settings.sharedStoreHome)

    return {
      codexRunning: await manager.isCodexAppRunning(),
      effectiveProvider,
      defaultLaunchProfile: resolveCurrentDefaultProfile(settings, mappings),
      selectedProfile: settings.selectedProfile,
      sharedStoreHome: settings.sharedStoreHome,
      runtimeHome: settings.targetHome,
      profilesCount
    }
  })

  handle('codex:loadSessions', async (_event, home: string) => getServices().manager.loadSessions(home))

  handle(
    'codex:prepareSharedWorkspace',
    async (_event, authFromHome: string | null, sharedStoreHome: string, runtimeHome: string, overwriteRuntimeConfig: boolean) => {
      getServices().manager.prepareSharedWorkspace(authFromHome, sharedStoreHome, runtimeHome, overwriteRuntimeConfig)
    }
  )

  handle(
    'codex:importSessionToSharedStoreOnly',
    async (
      _event,
      sourceHome: string,
      sharedStoreHome: string,
      sessionId: string,
      refreshUpdatedAt: boolean,
      addWorkspaceHint: boolean
    ) => getServices().manager.importSessionToSharedStoreOnly(sourceHome, sharedStoreHome, sessionId, refreshUpdatedAt, addWorkspaceHint)
  )

  handle(
    'codex:importSessionToSharedStore',
    async (
      _event,
      sourceHome: string,
      sharedStoreHome: string,
      authFromHome: string | null,
      runtimeHome: string,
      sessionId: string,
      refreshUpdatedAt: boolean,
      addWorkspaceHint: boolean
    ) => getServices().manager.importSessionToSharedStore(sourceHome, sharedStoreHome, authFromHome, runtimeHome, sessionId, refreshUpdatedAt, addWorkspaceHint)
  )

  handle(
    'codex:syncRuntimeHome',
    async (_event, sharedStoreHome: string, authFromHome: string | null, runtimeHome: string, overwriteRuntimeConfig?: boolean) =>
      getServices().manager.syncRuntimeHome(sharedStoreHome, authFromHome, runtimeHome, overwriteRuntimeConfig ?? false)
  )

  handle(
    'codex:syncAndLaunch',
    async (_event, sharedStoreHome: string, authFromHome: string | null, runtimeHome: string, appExePath: string | null) =>
      getServices().manager.syncAndLaunchCodexApp(sharedStoreHome, authFromHome, runtimeHome, appExePath)
  )

  handle('profiles:list', async (_event, profilesRoot: string) => getServices().profileStore.listProfiles(profilesRoot))
  handle('profiles:get', async (_event, profilesRoot: string, profileName: string) => getServices().profileStore.getProfile(profilesRoot, profileName))
  handle('profiles:getContent', async (_event, profileName: string) => getServices().profileStore.getProfileContent(profileName))
  handle('profiles:getOrCreateContent', async (_event, profileName: string) => getServices().profileStore.getOrCreateProfileContent(profileName))
  handle('profiles:saveFromHome', async (_event, profilesRoot: string, profileName: string, sourceHome: string, overwrite: boolean) =>
    getServices().profileStore.saveProfile(profilesRoot, profileName, sourceHome, overwrite)
  )
  handle('profiles:saveContent', async (_event, profileName: string, authJson: string, configToml: string) =>
    getServices().profileStore.saveProfileContent(profileName, authJson, configToml)
  )
  handle('profiles:createEmpty', async (_event, profileName: string) => getServices().profileStore.createEmptyProfile(profileName))
  handle('profiles:rename', async (_event, currentProfileName: string, newProfileName: string) =>
    getServices().profileStore.renameProfile(currentProfileName, newProfileName)
  )
  handle('profiles:delete', async (_event, profileName: string) => {
    getServices().profileStore.deleteProfile(profileName)
  })
  handle('profiles:import', async (_event, profilesRoot: string, sourceDirectory: string, profileName: string | null, overwrite: boolean) =>
    getServices().profileStore.importProfile(profilesRoot, sourceDirectory, profileName, overwrite)
  )
  handle('profiles:export', async (_event, profilesRoot: string, profileName: string, targetRoot: string, overwrite: boolean) =>
    getServices().profileStore.exportProfile(profilesRoot, profileName, targetRoot, overwrite)
  )
  handle('profiles:migrateDefaults', async (_event, mappings?: Record<string, string> | null) =>
    getServices().profileStore.migrateSharedStoreDefaultLaunchProfiles(mappings)
  )
  handle('profiles:loadDefaults', async () => getServices().profileStore.loadSharedStoreDefaultLaunchProfiles())
  handle('profiles:saveDefaults', async (_event, mappings: Record<string, string>) => {
    getServices().profileStore.saveSharedStoreDefaultLaunchProfiles(mappings)
  })
}
