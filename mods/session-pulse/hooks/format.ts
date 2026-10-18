import type { CacheReading, Limit } from '../types'

export const MINUTE = 60_000
export const FIVE_MINUTES = 5 * MINUTE
export const ONE_HOUR = 60 * MINUTE

const KIND_LABELS: Record<string, string> = {
  five_hour: '5ч',
  seven_day: 'нед',
  seven_day_opus: 'нед Opus',
  seven_day_sonnet: 'нед Sonnet',
  spend_limit: 'расход',
}

export function kindLabel(kind: string): string {
  return KIND_LABELS[kind] ?? kind
}

export function bar(fraction: number, width = 8): string {
  const filled = Math.round(Math.min(1, Math.max(0, fraction)) * width)
  return '█'.repeat(filled) + '░'.repeat(width - filled)
}

/** "2 ч 59 мин", "18 мин", "<1 мин": a span rounded down to the minute. */
export function humanSpan(ms: number): string {
  if (ms < MINUTE) return '<1 мин'
  const minutes = Math.floor(ms / MINUTE)
  const days = Math.floor(minutes / 1440)
  const hours = Math.floor((minutes % 1440) / 60)
  const rest = minutes % 60
  if (days > 0) return hours > 0 ? `${days} д ${hours} ч` : `${days} д`
  if (hours > 0) return rest > 0 ? `${hours} ч ${rest} мин` : `${hours} ч`
  return `${rest} мин`
}

/** What is left of the cache, rounded up: "18 мин" until the last minute. */
export function minutesLeft(ms: number): string {
  return `${Math.max(1, Math.ceil(ms / MINUTE))} мин`
}

/** "~81K", "~1.2M", "~640": the cached prefix as the warning reads it. */
export function tokens(n: number): string {
  if (n >= 1_000_000) return `~${(n / 1_000_000).toFixed(1)}M`
  if (n >= 1000) return `~${Math.round(n / 1000)}K`
  return `~${n}`
}

export function ttlLabel(ttlMs: number): string {
  return ttlMs >= ONE_HOUR ? '1 ч' : `${Math.round(ttlMs / MINUTE)} мин`
}

export function resetsIn(limit: Limit, now: number): string | undefined {
  if (limit.resetsAt === undefined) return undefined
  const at = Date.parse(limit.resetsAt)
  return Number.isNaN(at) ? undefined : humanSpan(Math.max(0, at - now))
}

/** The prefix the cache held after the request: what the next one reads or writes again. */
export function cachedPrefix(cache: CacheReading): number {
  return cache.read + cache.write
}

/** How long before the cache goes cold the warning row shows. */
export function warnLead(ttlMs: number): number {
  return ttlMs >= ONE_HOUR ? FIVE_MINUTES : 2 * MINUTE
}

/** How long before the cache goes cold auto-warming sends its warm. */
export function autoLead(ttlMs: number): number {
  return ttlMs >= ONE_HOUR ? 2 * MINUTE : MINUTE
}

/** The countdown turns from blue to orange at a quarter of the TTL (15 of 60 minutes). */
export function isRunningLow(left: number, ttlMs: number): boolean {
  return left <= ttlMs / 4
}

export function warmsWord(n: number): string {
  const tens = n % 100
  const ones = n % 10
  if (tens >= 11 && tens <= 14) return 'подогревов'
  if (ones === 1) return 'подогрев'
  if (ones >= 2 && ones <= 4) return 'подогрева'
  return 'подогревов'
}

/** How many warms keep the cache for `hours` at this TTL. */
export function warmsFor(hours: number, ttlMs: number): number {
  return Math.max(1, Math.round((hours * ONE_HOUR) / ttlMs))
}

export const AUTO_HOURS = [1, 2, 4, 8] as const

export function autoLabel(hours: number, ttlMs: number): string {
  const warms = warmsFor(hours, ttlMs)
  return `${hours} ч (${warms} ${warmsWord(warms)})`
}

/** The hours an answer names: one of the labels, or a number typed under Other. */
export function parseHours(answer: string): number | undefined {
  const match = answer.replace(',', '.').match(/\d+(?:\.\d+)?/)
  if (match === null) return undefined
  const hours = Number(match[0])
  return hours > 0 && hours <= 24 ? hours : undefined
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
export function learnTtl(previous: CacheReading, current: CacheReading): number | undefined {
  const gap = current.at - previous.at
  const prefix = cachedPrefix(previous)
  if (prefix === 0 || gap <= FIVE_MINUTES + 30_000) return undefined
  if (current.read >= prefix * 0.5) return ONE_HOUR
  const isMiss = current.read < prefix * 0.25 && current.write >= prefix * 0.5
  if (isMiss && gap < ONE_HOUR - MINUTE && current.model === previous.model) {
    return FIVE_MINUTES
  }
  return undefined
}
