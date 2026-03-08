import fs from 'node:fs'
import path from 'node:path'

const packageJsonPath = path.resolve('package.json')
const packageLockPath = path.resolve('package-lock.json')

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, 'utf8'))
}

function writeJson(filePath, value) {
  fs.writeFileSync(filePath, `${JSON.stringify(value, null, 2)}\n`, 'utf8')
}

function incrementPatch(version) {
  const [majorText = '0', minorText = '0', patchText = '0'] = String(version).split('.')
  const major = Number.parseInt(majorText, 10) || 0
  const minor = Number.parseInt(minorText, 10) || 0
  const patch = Number.parseInt(patchText, 10) || 0
  return `${major}.${minor}.${patch + 1}`
}

const packageJson = readJson(packageJsonPath)
const nextVersion = incrementPatch(packageJson.version)
packageJson.version = nextVersion
writeJson(packageJsonPath, packageJson)

if (fs.existsSync(packageLockPath)) {
  const packageLock = readJson(packageLockPath)
  packageLock.version = nextVersion
  if (packageLock.packages && packageLock.packages['']) {
    packageLock.packages[''].version = nextVersion
  }
  writeJson(packageLockPath, packageLock)
}

console.log(nextVersion)
