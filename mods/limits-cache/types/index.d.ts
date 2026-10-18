export type Limit = { kind: string; percentUsed: number; resetsAt?: string }

export type CacheReading = {
  /** When the main thread's last request was sent, in `$.clock.now()` ms. */
  at: number
  model: string
  read: number
  write: number
  uncached: number
}

declare module 'claude-code' {
  interface PluginState {
    'limits-cache': {
      limits: Limit[]
      cache: CacheReading | null
      ttlMs: number
      isTtlLearned: boolean
      now: number
      isHidden: boolean
    }
  }
}
