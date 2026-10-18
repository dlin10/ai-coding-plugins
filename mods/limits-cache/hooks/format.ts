import type { CacheReading, Limit } from '../types'

export const FIVE_MINUTES = 5 * 60_000
export const ONE_HOUR = 60 * 60_000

const KIND_LABELS: Record<string, string> = {
  five_hour: '5ч',
  seven_day: 'неделя',
  seven_day_opus: 'неделя Opus',
  seven_day_sonnet: 'неделя Sonnet',
  spend_limit: 'расход',
}

export function kindLabel(kind: string): string {
  return KIND_LABELS[kind] ?? kind
}

export function bar(percent: number, width = 10): string {
  const filled = Math.round((Math.min(100, Math.max(0, percent)) / 100) * width)
  return '█'.repeat(filled) + '░'.repeat(width - filled)
}

export function levelColor(percent: number): 'success' | 'warning' | 'error' {
  if (percent >= 80) return 'error'
  if (percent >= 50) return 'warning'
  return 'success'
}

/** "2д 4ч", "3ч 12м", "7м", "<1м": the two largest units, for a reset. */
export function humanSpan(ms: number): string {
  if (ms < 60_000) return '<1м'
  const minutes = Math.floor(ms / 60_000)
  const days = Math.floor(minutes / 1440)
  const hours = Math.floor((minutes % 1440) / 60)
  const rest = minutes % 60
  if (days > 0) return hours > 0 ? `${days}д ${hours}ч` : `${days}д`
  if (hours > 0) return rest > 0 ? `${hours}ч ${rest}м` : `${hours}ч`
  return `${rest}м`
}

/** "4:07" or "59:59": a countdown to the second. */
export function clock(ms: number): string {
  const seconds = Math.max(0, Math.ceil(ms / 1000))
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`
}

export function tokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`
  if (n >= 10_000) return `${Math.round(n / 1000)}k`
  if (n >= 1000) return `${(n / 1000).toFixed(1)}k`
  return String(n)
}

export function ttlLabel(ttlMs: number): string {
  return ttlMs >= ONE_HOUR ? '1ч' : `${Math.round(ttlMs / 60_000)}м`
}

export function resetsIn(limit: Limit, now: number): string | undefined {
  if (limit.resetsAt === undefined) return undefined
  const at = Date.parse(limit.resetsAt)
  return Number.isNaN(at) ? undefined : humanSpan(Math.max(0, at - now))
}

/** The prefix the cache held after the request: what the next one reads or rewrites. */
export function cachedPrefix(cache: CacheReading): number {
  return cache.read + cache.write
}

/** Share of the request's input the cache served, 0 to 100. */
export function hitPercent(cache: CacheReading): number {
  const total = cache.read + cache.write + cache.uncached
  return total === 0 ? 0 : Math.round((cache.read / total) * 100)
}

export function ttlFromOption(option: unknown): number | undefined {
  if (option === '5m') return FIVE_MINUTES
  if (option === '1h') return ONE_HOUR
  return undefined
}

/**
 * What a request says about the cache's lifetime, given the reading before it:
 * a hit after more than five minutes of quiet proves an hour; a miss after
 * five minutes to an hour of quiet, on the same model, points back to five.
 */
export function learnTtl(
  previous: CacheReading,
  current: CacheReading,
): number | undefined {
  const gap = current.at - previous.at
  const prefix = cachedPrefix(previous)
  if (prefix === 0 || gap <= FIVE_MINUTES + 30_000) return undefined
  if (current.read >= prefix * 0.5) return ONE_HOUR
  const isMiss = current.read < prefix * 0.25 && current.write >= prefix * 0.5
  if (isMiss && gap < ONE_HOUR - 60_000 && current.model === previous.model) {
    return FIVE_MINUTES
  }
  return undefined
}
