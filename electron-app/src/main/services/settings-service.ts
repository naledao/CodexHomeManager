import fs from 'node:fs'
import path from 'node:path'
import type { AppPathSettings } from '@shared/contracts'
import { ensureDir } from '../utils/path-utils'

export class SettingsService {
  constructor(private readonly settingsPath: string) {}

  load(): AppPathSettings | null {
    if (!fs.existsSync(this.settingsPath)) {
      return null
    }

    try {
      return JSON.parse(fs.readFileSync(this.settingsPath, 'utf8')) as AppPathSettings
    } catch {
      return null
    }
  }

  save(settings: AppPathSettings): void {
    try {
      ensureDir(path.dirname(this.settingsPath))
      fs.writeFileSync(this.settingsPath, JSON.stringify(settings, null, 2), 'utf8')
    } catch {
      // Best effort persistence.
    }
  }
}