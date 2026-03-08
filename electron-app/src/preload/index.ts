import { contextBridge, ipcRenderer } from 'electron'
import type { MainApi } from '@shared/contracts'

const api: MainApi = {
  getDefaultPaths: () => ipcRenderer.invoke('app:getDefaultPaths'),
  loadSettings: () => ipcRenderer.invoke('app:loadSettings'),
  saveSettings: (settings) => ipcRenderer.invoke('app:saveSettings', settings),
  browseDirectory: (options) => ipcRenderer.invoke('dialog:browseDirectory', options),
  browseFile: (options) => ipcRenderer.invoke('dialog:browseFile', options),
  findCodexAppExecutable: () => ipcRenderer.invoke('codex:findExecutable'),
  isCodexAppRunning: () => ipcRenderer.invoke('codex:isRunning'),
  closeRunningCodexApp: () => ipcRenderer.invoke('codex:closeRunning'),
  getStatus: (settings) => ipcRenderer.invoke('codex:getStatus', settings),
  loadSessions: (home) => ipcRenderer.invoke('codex:loadSessions', home),
  prepareSharedWorkspace: (authFromHome, sharedStoreHome, runtimeHome, overwriteRuntimeConfig) =>
    ipcRenderer.invoke('codex:prepareSharedWorkspace', authFromHome, sharedStoreHome, runtimeHome, overwriteRuntimeConfig),
  importSessionToSharedStoreOnly: (sourceHome, sharedStoreHome, sessionId, refreshUpdatedAt, addWorkspaceHint) =>
    ipcRenderer.invoke('codex:importSessionToSharedStoreOnly', sourceHome, sharedStoreHome, sessionId, refreshUpdatedAt, addWorkspaceHint),
  importSessionToSharedStore: (sourceHome, sharedStoreHome, authFromHome, runtimeHome, sessionId, refreshUpdatedAt, addWorkspaceHint) =>
    ipcRenderer.invoke('codex:importSessionToSharedStore', sourceHome, sharedStoreHome, authFromHome, runtimeHome, sessionId, refreshUpdatedAt, addWorkspaceHint),
  syncRuntimeHome: (sharedStoreHome, authFromHome, runtimeHome, overwriteRuntimeConfig) =>
    ipcRenderer.invoke('codex:syncRuntimeHome', sharedStoreHome, authFromHome, runtimeHome, overwriteRuntimeConfig),
  syncAndLaunchCodexApp: (sharedStoreHome, authFromHome, runtimeHome, appExePath) =>
    ipcRenderer.invoke('codex:syncAndLaunch', sharedStoreHome, authFromHome, runtimeHome, appExePath),
  listProfiles: (profilesRoot) => ipcRenderer.invoke('profiles:list', profilesRoot),
  getProfile: (profilesRoot, profileName) => ipcRenderer.invoke('profiles:get', profilesRoot, profileName),
  getProfileContent: (profileName) => ipcRenderer.invoke('profiles:getContent', profileName),
  getOrCreateProfileContent: (profileName) => ipcRenderer.invoke('profiles:getOrCreateContent', profileName),
  saveProfile: (profilesRoot, profileName, sourceHome, overwrite) =>
    ipcRenderer.invoke('profiles:saveFromHome', profilesRoot, profileName, sourceHome, overwrite),
  saveProfileContent: (profileName, authJson, configToml) =>
    ipcRenderer.invoke('profiles:saveContent', profileName, authJson, configToml),
  createEmptyProfile: (profileName) => ipcRenderer.invoke('profiles:createEmpty', profileName),
  renameProfile: (currentProfileName, newProfileName) =>
    ipcRenderer.invoke('profiles:rename', currentProfileName, newProfileName),
  deleteProfile: (profileName) => ipcRenderer.invoke('profiles:delete', profileName),
  importProfile: (profilesRoot, sourceDirectory, profileName, overwrite) =>
    ipcRenderer.invoke('profiles:import', profilesRoot, sourceDirectory, profileName, overwrite),
  exportProfile: (profilesRoot, profileName, targetRoot, overwrite) =>
    ipcRenderer.invoke('profiles:export', profilesRoot, profileName, targetRoot, overwrite),
  migrateSharedStoreDefaultLaunchProfiles: (mappings) =>
    ipcRenderer.invoke('profiles:migrateDefaults', mappings),
  loadSharedStoreDefaultLaunchProfiles: () => ipcRenderer.invoke('profiles:loadDefaults'),
  saveSharedStoreDefaultLaunchProfiles: (mappings) =>
    ipcRenderer.invoke('profiles:saveDefaults', mappings)
}

contextBridge.exposeInMainWorld('codexApi', api)
