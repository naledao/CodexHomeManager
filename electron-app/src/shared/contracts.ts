export interface AppPathSettings {
  stateHome: string
  authHome: string
  profilesRoot: string
  selectedProfile: string
  defaultLaunchProfile: string
  sharedStoreDefaultLaunchProfiles: Record<string, string>
  sharedStoreHome: string
  targetHome: string
  appExePath: string
  autoSyncConfigChanges: boolean
}

export interface ManagedProfileContent {
  accountId: number
  name: string
  modelProvider: string
  authJson: string
  configToml: string
  revision: number
  updatedAt: string
}

export interface ProviderProfile {
  id: number
  name: string
  directoryPath: string
  modelProvider: string
  revision: number
  updatedAt: string
}

export interface RuntimeSyncResult {
  sharedStoreHome: string
  runtimeHome: string
  effectiveProvider: string
  sessionCount: number
  lastImportedSessionId: string
}

export interface SessionRecord {
  id: string
  title: string
  cwd: string
  sessionPath: string
  updatedAt: string
  createdAt: string
  source: string
  modelProvider: string
}

export interface AppStatusSnapshot {
  codexRunning: boolean
  effectiveProvider: string
  defaultLaunchProfile: string
  selectedProfile: string
  sharedStoreHome: string
  runtimeHome: string
  profilesCount: number
}

export interface DefaultPaths {
  stateHome: string
  profilesRoot: string
  sharedStoreHome: string
  targetHome: string
  appExePath: string
  databasePath: string
  materializedProfilesRoot: string
}

export interface DirectoryChoiceOptions {
  title: string
  defaultPath?: string
}

export interface FileChoiceOptions {
  title: string
  defaultPath?: string
  filters?: Array<{ name: string; extensions: string[] }>
}

export interface MainApi {
  getDefaultPaths(): Promise<DefaultPaths>
  loadSettings(): Promise<AppPathSettings | null>
  saveSettings(settings: AppPathSettings): Promise<void>
  browseDirectory(options: DirectoryChoiceOptions): Promise<string | null>
  browseFile(options: FileChoiceOptions): Promise<string | null>
  findCodexAppExecutable(): Promise<string | null>
  isCodexAppRunning(): Promise<boolean>
  closeRunningCodexApp(): Promise<number>
  getStatus(settings: AppPathSettings): Promise<AppStatusSnapshot>
  loadSessions(home: string): Promise<SessionRecord[]>
  prepareSharedWorkspace(authFromHome: string | null, sharedStoreHome: string, runtimeHome: string, overwriteRuntimeConfig: boolean): Promise<void>
  importSessionToSharedStoreOnly(sourceHome: string, sharedStoreHome: string, sessionId: string, refreshUpdatedAt: boolean, addWorkspaceHint: boolean): Promise<SessionRecord>
  importSessionToSharedStore(sourceHome: string, sharedStoreHome: string, authFromHome: string | null, runtimeHome: string, sessionId: string, refreshUpdatedAt: boolean, addWorkspaceHint: boolean): Promise<RuntimeSyncResult>
  syncRuntimeHome(sharedStoreHome: string, authFromHome: string | null, runtimeHome: string, overwriteRuntimeConfig?: boolean): Promise<RuntimeSyncResult>
  syncAndLaunchCodexApp(sharedStoreHome: string, authFromHome: string | null, runtimeHome: string, appExePath: string | null): Promise<RuntimeSyncResult>
  listProfiles(profilesRoot: string): Promise<ProviderProfile[]>
  getProfile(profilesRoot: string, profileName: string): Promise<ProviderProfile>
  getProfileContent(profileName: string): Promise<ManagedProfileContent>
  getOrCreateProfileContent(profileName: string): Promise<ManagedProfileContent>
  saveProfile(profilesRoot: string, profileName: string, sourceHome: string, overwrite: boolean): Promise<ProviderProfile>
  saveProfileContent(profileName: string, authJson: string, configToml: string): Promise<ManagedProfileContent>
  createEmptyProfile(profileName: string): Promise<ProviderProfile>
  renameProfile(currentProfileName: string, newProfileName: string): Promise<ProviderProfile>
  deleteProfile(profileName: string): Promise<void>
  importProfile(profilesRoot: string, sourceDirectory: string, profileName: string | null, overwrite: boolean): Promise<ProviderProfile>
  exportProfile(profilesRoot: string, profileName: string, targetRoot: string, overwrite: boolean): Promise<string>
  migrateSharedStoreDefaultLaunchProfiles(mappings?: Record<string, string> | null): Promise<Record<string, string>>
  loadSharedStoreDefaultLaunchProfiles(): Promise<Record<string, string>>
  saveSharedStoreDefaultLaunchProfiles(mappings: Record<string, string>): Promise<void>
}
