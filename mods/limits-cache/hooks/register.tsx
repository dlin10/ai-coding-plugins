import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'

import type { CacheReading, Limit } from '../types'
import {
  FIVE_MINUTES,
  bar,
  cachedPrefix,
  clock,
  hitPercent,
  humanSpan,
  kindLabel,
  learnTtl,
  levelColor,
  resetsIn,
  tokens,
  ttlFromOption,
  ttlLabel,
} from './format'

const COMMAND = 'limits-cache'
const STORED_TTL = 'ttlMs'

const limits = atom({ plugin: 'limits-cache', key: 'limits' } as const, [])
const cache = atom({ plugin: 'limits-cache', key: 'cache' } as const, null)
const ttlMs = atom({ plugin: 'limits-cache', key: 'ttlMs' } as const, FIVE_MINUTES)
const isTtlLearned = atom({ plugin: 'limits-cache', key: 'isTtlLearned' } as const, false)
const now = atom({ plugin: 'limits-cache', key: 'now' } as const, 0)
const isHidden = atom({ plugin: 'limits-cache', key: 'isHidden' } as const, false)

const toLimits = (windows: readonly Limit[]): Limit[] =>
  windows.map(({ kind, percentUsed, resetsAt }) => ({ kind, percentUsed, resetsAt }))

let lastTickWrite = 0

// The countdown needs a redraw every second while the cache is warm; once it
// is cold, the reset times only need one every half minute.
async function tick($: EngineInterface) {
  const at = await $.clock.now()
  const reading = await read($, cache)
  const ttl = await read($, ttlMs)
  const isWarm = reading !== null && at - reading.at < ttl + 1000
  if (isWarm || at - lastTickWrite >= 30_000) {
    lastTickWrite = at
    await update($, now, () => at)
  }
}

export const register: Register = (on, options) => {
  const pinnedTtl = ttlFromOption(options.cacheTtl)

  on('session.start', async ($, e, next) => {
    await $.command.register({
      name: COMMAND,
      description: 'Show or hide the rate-limit and prompt-cache band',
    })

    const usage = await $.session.usage()
    await update($, limits, () => toLimits(usage.rateLimits))

    const stored = await $.store.get(STORED_TTL)
    const learned = typeof stored === 'number' ? stored : undefined
    await update($, ttlMs, () => pinnedTtl ?? learned ?? FIVE_MINUTES)
    await update($, isTtlLearned, () => pinnedTtl === undefined && learned !== undefined)
    const startedAt = await $.clock.now()
    await update($, now, () => startedAt)

    $.clock.every(1000, () => void tick($))

    return next(e)
  })

  on('command.run', { command: COMMAND }, async $ => {
    const hidden = await update($, isHidden, value => !value)

    return { text: hidden ? 'Limits and cache band hidden.' : 'Limits and cache band shown.' }
  })

  on('session.measure', async ($, e, next) => {
    if (e.changed.includes('rateLimits')) {
      await update($, limits, () => toLimits(e.rateLimits))
    }

    return next(e)
  })

  // Every request of the main thread refreshes the cache entry it reads, so the
  // countdown runs from the last one sent. Subagents cache prefixes of their own.
  on('turn.step', async function* ($, e, next) {
    if (e.agentId !== undefined) {
      return yield* next(e)
    }

    const at = await $.clock.now()
    const result = yield* next(e)
    const usage = result.usage
    if (usage === null) {
      return result
    }

    const current: CacheReading = {
      at,
      model: usage.model,
      read: usage.cache_read_input_tokens,
      write: usage.cache_creation_input_tokens,
      uncached: usage.input_tokens,
    }
    const previous = await read($, cache)
    await update($, cache, () => current)
    await update($, now, () => at)

    if (pinnedTtl === undefined && previous !== null) {
      const learned = learnTtl(previous, current)
      const ttl = await read($, ttlMs)
      if (learned !== undefined && learned !== ttl) {
        await update($, ttlMs, () => learned)
        await update($, isTtlLearned, () => true)
        await $.store.set(STORED_TTL, learned)
        $.ui.toast(`Prompt cache TTL looks like ${ttlLabel(learned)}`)
      }
    }

    return result
  })

  on('session.end', { reason: 'clear' }, async ($, e, next) => {
    await update($, cache, () => null)

    return next(e)
  })

  on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
    if (e.props.hasSurvey || (await read($, isHidden))) {
      return next(e)
    }

    const { Box, Text } = $.ui.resolve(e)
    const t = await read($, now)
    const windows = await read($, limits)
    const reading = await read($, cache)
    const ttl = await read($, ttlMs)
    const isLearned = await read($, isTtlLearned)
    const isNarrow = e.props.bodyColumns < 70

    const limitParts = windows.map(limit => {
      const reset = resetsIn(limit, t)
      return (
        <Text>
          {kindLabel(limit.kind)}{' '}
          <Text color={levelColor(limit.percentUsed)}>
            {isNarrow ? '' : `${bar(limit.percentUsed)} `}
            {Math.round(limit.percentUsed)}%
          </Text>
          {reset !== undefined && <Text dimColor> ↻ {reset}</Text>}
          {'   '}
        </Text>
      )
    })

    const ttlNote = `TTL ${ttlLabel(ttl)}${pinnedTtl === undefined ? (isLearned ? ', изучен' : ', по умолч.') : ''}`
    let cacheLine
    if (reading === null) {
      cacheLine = <Text dimColor>ещё не было запросов</Text>
    } else {
      const left = reading.at + ttl - t
      const prefix = tokens(cachedPrefix(reading))
      cacheLine =
        left > 0 ? (
          <Text>
            <Text color={left < 60_000 ? 'warning' : 'success'}>● тёплый {clock(left)}</Text>
            <Text dimColor> ({ttlNote})</Text>
            {' · '}попадание {hitPercent(reading)}%{' · '}в кэше {prefix}
          </Text>
        ) : (
          <Text>
            <Text color="error">○ остыл {humanSpan(-left)} назад</Text>
            <Text dimColor> ({ttlNote})</Text>
            {' · '}следующий запрос заново запишет ~{prefix}
          </Text>
        )
    }

    return (
      <Box flexDirection="column">
        <Text>
          <Text weight="bold">Лимиты </Text>
          {windows.length === 0 ? <Text dimColor>нет данных (не подписка или ещё не было ответа)</Text> : limitParts}
        </Text>
        <Text>
          <Text weight="bold">Кэш    </Text>
          {cacheLine}
        </Text>
      </Box>
    )
  })
}
