export type Limit = { kind: string; percentUsed: number; resetsAt?: string }

export type CacheReading = {
  /** When the request that last read or wrote the cache was sent, in `$.clock.now()` ms. */
  at: number
  model: string
  read: number
  write: number
  uncached: number
}

/** Auto-warming: on until `until`, `done` of `total` warms sent. */
export type AutoWarm = { until: number; done: number; total: number }

declare module 'claude-code' {
  interface PluginState {
    'session-pulse': {
      limits: Limit[]
      cache: CacheReading | null
      ttlMs: number
      isTtlLearned: boolean
      now: number
      isHidden: boolean
      /** The `at` of the cache reading whose warning the person hid. */
      warnHiddenAt: number | null
      isWarming: boolean
      auto: AutoWarm | null
    }
  }
}
