import type { MainApi } from '@shared/contracts'

declare global {
  interface Window {
    codexApi: MainApi
  }
}

export {}