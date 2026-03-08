import path from 'node:path'
import { spawn } from 'node:child_process'

const [tool, ...args] = process.argv.slice(2)

if (!tool) {
  console.error('Usage: node scripts/run-with-modern-node.mjs <electron-vite|electron-builder> [...args]')
  process.exit(1)
}

const toolEntrypoints = {
  'electron-vite': path.resolve('node_modules/electron-vite/bin/electron-vite.js'),
  'electron-builder': path.resolve('node_modules/electron-builder/cli.js')
}

const entrypoint = toolEntrypoints[tool]
if (!entrypoint) {
  console.error(`Unsupported tool: ${tool}`)
  process.exit(1)
}

const [major, minor, patch] = process.versions.node.split('.').map((value) => Number.parseInt(value, 10))
const useFallbackNode = major === 20 && minor === 6 && patch === 0

const command = useFallbackNode ? (process.platform === 'win32' ? 'npx.cmd' : 'npx') : process.execPath
const commandArgs = useFallbackNode
  ? ['-p', 'node@20.11.0', 'node', entrypoint, ...args]
  : [entrypoint, ...args]

const child = spawn(command, commandArgs, {
  stdio: 'inherit',
  shell: false
})

child.on('exit', (code) => {
  process.exit(code ?? 1)
})
