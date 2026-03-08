import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

export const utf8NoBom = new TextEncoder()

export function getDefaultStateHome(): string {
  return path.join(os.homedir(), '.codex')
}

export function getDefaultProfilesRoot(): string {
  return path.join(os.homedir(), '.codex-profiles')
}

export function getDefaultSharedStoreHome(): string {
  return path.join(os.homedir(), '.codex-shared-store')
}

export function getDefaultTargetHome(): string {
  return path.join('C:\\', 'codex-home-hybrid')
}

export function stripVerbatimPathPrefix(value: string): string {
  return value.startsWith('\\\\?\\') ? value.slice(4) : value
}

export function toVerbatimPath(value: string): string {
  if (!value.trim()) {
    return value
  }

  const fullPath = path.resolve(stripVerbatimPathPrefix(value))
  if (fullPath.startsWith('\\\\?\\')) {
    return fullPath
  }

  return `\\\\?\\${fullPath}`
}

export function isLocalWindowsPath(value: string): boolean {
  const normalized = stripVerbatimPathPrefix(value)
  return /^[A-Za-z]:[\\/]/.test(normalized)
}

export function normalizeWorkspaceRoot(value: string | null | undefined): string {
  if (!value?.trim()) {
    return ''
  }

  return isLocalWindowsPath(value) ? toVerbatimPath(value) : value.trim()
}

export function normalizeCwd(value: string): string {
  return stripVerbatimPathPrefix(value)
}

export function normalizeDirectoryKey(value: string | null | undefined): string {
  if (!value?.trim()) {
    return ''
  }

  try {
    return path.resolve(stripVerbatimPathPrefix(value.trim())).replace(/[\\/]+$/, '')
  } catch {
    return value.trim().replace(/[\\/]+$/, '')
  }
}

export function samePath(left: string | null | undefined, right: string | null | undefined): boolean {
  if (!left?.trim() || !right?.trim()) {
    return false
  }

  return normalizeDirectoryKey(left).toLowerCase() === normalizeDirectoryKey(right).toLowerCase()
}

export function ensureDir(directory: string): void {
  fs.mkdirSync(directory, { recursive: true })
}

export function readTextIfExists(filePath: string): string {
  return fs.existsSync(filePath) ? fs.readFileSync(filePath, 'utf8') : ''
}

export function writeTextNoBom(filePath: string, content: string): void {
  ensureDir(path.dirname(filePath))
  fs.writeFileSync(filePath, content, { encoding: 'utf8' })
}

export function copyDirectory(source: string, target: string): void {
  ensureDir(target)
  for (const directory of fs.readdirSync(source, { withFileTypes: true })) {
    const sourcePath = path.join(source, directory.name)
    const targetPath = path.join(target, directory.name)
    if (directory.isDirectory()) {
      copyDirectory(sourcePath, targetPath)
    } else {
      ensureDir(path.dirname(targetPath))
      fs.copyFileSync(sourcePath, targetPath)
    }
  }
}

export function copyPathIfExists(source: string, target: string): void {
  if (fs.existsSync(source)) {
    const stat = fs.statSync(source)
    if (stat.isDirectory()) {
      copyDirectory(source, target)
      return
    }

    ensureDir(path.dirname(target))
    fs.copyFileSync(source, target)
  }
}

export function computeSha256(content: string): string {
  if (!content) {
    return ''
  }

  return crypto.createHash('sha256').update(content, 'utf8').digest('hex').toUpperCase()
}

export function normalizeProfileName(profileName: string | null | undefined): string {
  if (!profileName?.trim()) {
    return ''
  }

  return profileName
    .trim()
    .replace(/[<>:"/\\|?*\u0000-\u001F]/g, '_')
    .trim()
}

export function normalizeProvider(value: string | null | undefined): string {
  return value?.trim().toLowerCase() ?? ''
}

export function stripTomlComment(line: string): string {
  let inSingleQuote = false
  let inDoubleQuote = false

  for (let index = 0; index < line.length; index += 1) {
    const current = line[index]
    if (current === '\'' && !inDoubleQuote) {
      inSingleQuote = !inSingleQuote
      continue
    }

    if (current === '"' && !inSingleQuote) {
      const escaped = index > 0 && line[index - 1] === '\\'
      if (!escaped) {
        inDoubleQuote = !inDoubleQuote
      }
      continue
    }

    if (current === '#' && !inSingleQuote && !inDoubleQuote) {
      return line.slice(0, index)
    }
  }

  return line
}

export function unquoteTomlString(value: string): string {
  const trimmed = value.trim()
  if (trimmed.length >= 2) {
    if ((trimmed.startsWith('"') && trimmed.endsWith('"')) || (trimmed.startsWith('\'') && trimmed.endsWith('\''))) {
      return trimmed.slice(1, -1)
    }
  }

  return trimmed
}

export function tryReadTopLevelTomlStringValue(content: string, key: string): string | null {
  if (!content.trim()) {
    return null
  }

  let inSection = false
  for (const rawLine of content.split(/\r?\n/u)) {
    const line = stripTomlComment(rawLine).trim()
    if (!line) {
      continue
    }

    if (line.startsWith('[')) {
      inSection = true
      continue
    }

    if (inSection) {
      continue
    }

    const separatorIndex = line.indexOf('=')
    if (separatorIndex <= 0) {
      continue
    }

    const currentKey = line.slice(0, separatorIndex).trim()
    if (currentKey.toLowerCase() !== key.toLowerCase()) {
      continue
    }

    return unquoteTomlString(line.slice(separatorIndex + 1).trim())
  }

  return null
}

export function toIsoString(value: Date): string {
  return value.toISOString()
}