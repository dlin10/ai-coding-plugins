import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register, RenderChildren } from 'claude-code'

import type { CacheReading, Limit } from '../types'
import {
  AUTO_HOURS,
  FIVE_MINUTES,
  MINUTE,
  ONE_HOUR,
  autoLabel,
  autoLead,
  bar,
  barCells,
  cachedPrefix,
  isRunningLow,
  kindLabel,
  learnTtl,
  minutesLeft,
  parseHours,
  resetsIn,
  ttlFromOption,
  ttlLabel,
  warmsFor,
} from './format'

const COMMAND = 'session-pulse'
const STORED_TTL = 'ttlMs'
// The rate limits are the account's, so every session of the mod shares the
// newest reading through the store: an idle session takes what another read.
const STORED_LIMITS = 'limits'
const AUTO_QUESTION = 'How long should auto-warming keep the cache warm?'
// The mod's own dialog: AskUserQuestion raised outside a turn leaves the
// desktop showing the session as working once it is answered.
const AUTO_DIALOG = 'session-pulse-auto'
const WARM_PROMPT = 'This is a check that keeps the prompt cache warm. Reply with one word: ok.'
const TICK_MS = 5000

const limits = atom({ plugin: 'session-pulse', key: 'limits' } as const, [])
const cache = atom({ plugin: 'session-pulse', key: 'cache' } as const, null)
const ttlMs = atom({ plugin: 'session-pulse', key: 'ttlMs' } as const, ONE_HOUR)
const isTtlLearned = atom({ plugin: 'session-pulse', key: 'isTtlLearned' } as const, false)
const now = atom({ plugin: 'session-pulse', key: 'now' } as const, 0)
const isHidden = atom({ plugin: 'session-pulse', key: 'isHidden' } as const, false)
const isWarming = atom({ plugin: 'session-pulse', key: 'isWarming' } as const, false)
const auto = atom({ plugin: 'session-pulse', key: 'auto' } as const, null)

const toLimits = (windows: readonly Limit[]): Limit[] =>
  windows.map(({ kind, percentUsed, resetsAt }) => ({ kind, percentUsed, resetsAt }))

let lastTickWrite = 0
let lastTickMinute = 0
let isTurnRunning = false
let limitsAt = 0
let quietMisses = 0

async function shareLimits($: EngineInterface, windows: readonly Limit[]) {
  const at = await $.clock.now()
  const shown = toLimits(windows)
  await update($, limits, () => shown)
  limitsAt = at
  await $.store.set(STORED_LIMITS, { at, windows: shown })
}

async function takeSharedLimits($: EngineInterface) {
  const stored = await $.store.get(STORED_LIMITS)
  if (typeof stored !== 'object' || stored === null) return
  const { at, windows } = stored as { at?: unknown; windows?: unknown }
  if (typeof at !== 'number' || at <= limitsAt || !Array.isArray(windows) || windows.length === 0) return
  limitsAt = at
  await update($, limits, () => toLimits(windows as Limit[]))
}

/**
 * Reads the main thread's cached prefix once more with a one-word fork, which
 * refreshes the cache entry without adding a message to the conversation.
 */
async function warm($: EngineInterface): Promise<boolean> {
  if (await read($, isWarming)) return false
  await update($, isWarming, () => true)
  try {
    const previous = await read($, cache)
    const at = await $.clock.now()
    const reply = await $.model.fork({ prompt: WARM_PROMPT })
    if (!reply.isAnswered) {
      $.ui.toast(`Could not warm the cache: ${reply.reason}`)
      return false
    }
    const { usage } = reply
    if (previous !== null && usage.cache_read_input_tokens < cachedPrefix(previous) * 0.5) {
      $.ui.toast('The cache had already gone cold: the warm-up read the conversation afresh')
    }
    await update($, cache, () => ({
      at,
      model: previous?.model ?? '',
      read: usage.cache_read_input_tokens,
      write: usage.cache_creation_input_tokens,
      uncached: usage.input_tokens,
    }))
    await update($, now, () => at)
    return true
  } finally {
    await update($, isWarming, () => false)
  }
}

async function chooseAuto($: EngineInterface) {
  if ((await read($, auto)) !== null) {
    await update($, auto, () => null)
    $.ui.toast('Auto-warming is off')
    return
  }

  await $.ui.open({ id: AUTO_DIALOG, title: 'Auto-warm', focus: true, closeOnEscape: true, rows: 4 })
}

async function startTypedAuto($: EngineInterface, text: string) {
  const hours = parseHours(text)
  if (hours === undefined) {
    $.ui.toast(`Not a number of hours: "${text}"`)
    return
  }
  await startAuto($, hours)
}

async function startAuto($: EngineInterface, hours: number) {
  await $.ui.close({ id: AUTO_DIALOG })
  const ttl = await read($, ttlMs)
  const at = await $.clock.now()
  const total = warmsFor(hours, ttl)
  await update($, auto, () => ({ until: at + hours * ONE_HOUR, done: 0, total }))
  $.ui.toast(`Auto-warming is on: ${autoLabel(hours, ttl)}`)
}

// Auto-warming warms once the countdown reaches its lead, and stops when its
// hours or warms run out or the cache went cold anyway. The band shows whole
// minutes, so it is redrawn when the minute changes, or every half minute for
// the reset times.
async function tick($: EngineInterface) {
  await takeSharedLimits($)
  const at = await $.clock.now()
  const reading = await read($, cache)
  const ttl = await read($, ttlMs)
  const left = reading === null ? -1 : reading.at + ttl - at

  const plan = await read($, auto)
  if (plan !== null) {
    if (at >= plan.until || plan.done >= plan.total) {
      await update($, auto, () => null)
      $.ui.toast('Auto-warming has finished')
    } else if (reading !== null && left <= 0) {
      await update($, auto, () => null)
      $.ui.toast('The cache went cold, so auto-warming is off')
    } else if (reading !== null && left <= autoLead(ttl) && !isTurnRunning && (await warm($))) {
      await update($, auto, value => (value === null ? null : { ...value, done: value.done + 1 }))
    }
  }

  const minute = Math.ceil(left / MINUTE)
  if (minute !== lastTickMinute || at - lastTickWrite >= 30_000) {
    lastTickMinute = minute
    lastTickWrite = at
    await update($, now, () => at)
  }
}

export const register: Register = (on, options) => {
  const pinnedTtl = ttlFromOption(options.cacheTtl)

  on('session.start', async ($, e, next) => {
    await $.command.register({
      name: COMMAND,
      description: 'Show or hide the session pulse band: rate limits and prompt cache',
    })

    // Before its first request a process has no reading yet; a reload or resume
    // keeps the windows the session's state already holds, or takes the store's.
    const usage = await $.session.usage()
    if (usage.rateLimits.length > 0) await shareLimits($, usage.rateLimits)
    else await takeSharedLimits($)

    const stored = await $.store.get(STORED_TTL)
    const learned = typeof stored === 'number' ? stored : undefined
    await update($, ttlMs, () => pinnedTtl ?? learned ?? ONE_HOUR)
    await update($, isTtlLearned, () => pinnedTtl === undefined && learned !== undefined)
    const startedAt = await $.clock.now()
    await update($, now, () => startedAt)

    $.clock.every(TICK_MS, () => void tick($))

    return next(e)
  })

  on('command.run', { command: COMMAND }, async $ => {
    const hidden = await update($, isHidden, value => !value)

    return { text: hidden ? 'Session pulse hidden.' : 'Session pulse shown.' }
  })

  on('session.measure', async ($, e, next) => {
    if (e.changed.includes('rateLimits')) {
      if (e.rateLimits.length > 0) await shareLimits($, e.rateLimits)
      else await update($, limits, () => [])
    }

    return next(e)
  })

  on('turn.start', async ($, e, next) => {
    if (e.agentId === undefined) isTurnRunning = true

    return next(e)
  })

  on('turn.complete', async ($, e, next) => {
    if (e.agentId === undefined) isTurnRunning = false

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

    if (pinnedTtl === undefined && previous !== null && previous.model !== '') {
      // A lone miss can be an entry evicted before its hour, so five minutes
      // takes two in a row; a hit after the same quiet starts the count over.
      let learned = learnTtl(previous, current)
      if (learned === FIVE_MINUTES) {
        quietMisses += 1
        if (quietMisses < 2) learned = undefined
      } else if (learned !== undefined) {
        quietMisses = 0
      }
      if (learned !== undefined && learned !== (await read($, ttlMs))) {
        await update($, ttlMs, () => learned)
        await update($, isTtlLearned, () => true)
        await $.store.set(STORED_TTL, learned)
        $.ui.toast(`The prompt cache seems to live ${ttlLabel(learned)}`)
      }
    }

    return result
  })

  on('session.end', { reason: 'clear' }, async ($, e, next) => {
    await update($, cache, () => null)
    await update($, auto, () => null)

    return next(e)
  })

  on('ui.render', { component: 'Pane', requestId: AUTO_DIALOG }, async ($, e) => {
    const elements = $.ui.resolve(e)
    const { Box, Button, Text } = elements
    const Input = 'Input' in elements ? elements.Input : undefined
    const ttl = await read($, ttlMs)

    return (
      <Box flexDirection="column" gap={1}>
        <Text>{AUTO_QUESTION}</Text>
        <Box flexDirection="row" gap={1} flexWrap="wrap">
          {AUTO_HOURS.map((hours, index) => (
            <Button
              key={`auto-${hours}`}
              label={autoLabel(hours, ttl)}
              autoFocus={index === 0 ? true : undefined}
              onPress={() => startAuto($, hours)}
            />
          ))}
          <Button key="auto-cancel" label="Cancel" role="dismiss" onPress={() => $.ui.close({ id: AUTO_DIALOG })} />
        </Box>
        {Input && (
          <Input
            key="auto-hours"
            label="Other"
            placeholder="hours, up to 24"
            submitLabel="Start"
            onSubmit={text => startTypedAuto($, text)}
          />
        )}
      </Box>
    )
  })

  on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
    if (e.props.hasSurvey || (await read($, isHidden))) {
      return next(e)
    }

    const { Box, Button, Text } = $.ui.resolve(e)
    const t = await read($, now)
    const windows = await read($, limits)
    const reading = await read($, cache)
    const ttl = await read($, ttlMs)
    const plan = await read($, auto)

    const left = reading === null ? 0 : reading.at + ttl - t
    const isWarm = reading !== null && left > 0
    const cacheText = reading === null ? 'no requests yet' : isWarm ? minutesLeft(left) : 'cold'
    const shown = windows.map(limit => ({
      limit,
      label: kindLabel(limit.kind),
      percent: `${Math.round(limit.percentUsed)}%`,
      reset: limit.kind === 'five_hour' ? resetsIn(limit, t) : undefined,
    }))
    const autoText = plan === null ? 'Auto Warm' : `Auto Warm ${plan.done}/${plan.total}`

    // The row's text in cells: the label's column and its gap, each part after a
    // gap, and the button's `[ label ]` after one.
    const textCells =
      8 +
      cacheText.length +
      shown.reduce(
        (sum, { label, percent, reset }) =>
          sum + 1 + label.length + 1 + percent.length + (reset === undefined ? 0 : ` resets in ${reset}`.length),
        0,
      ) +
      1 +
      autoText.length +
      4
    const cells = barCells(e.props.bodyColumns, textCells, (reading === null ? 0 : 1) + windows.length)

    const row = (label: string, ...children: RenderChildren[]) => (
      <Box flexDirection="row" gap={1}>
        <Box width={7} flexShrink={0}>
          <Text dimColor>{label}</Text>
        </Box>
        <Box flexDirection="row" gap={1} flexWrap="wrap" flexShrink={1}>
          {children}
        </Box>
      </Box>
    )

    let cacheParts
    if (reading === null) {
      cacheParts = <Text dimColor>{cacheText}</Text>
    } else if (isWarm) {
      const color = isRunningLow(left, ttl) ? 'warning' : 'suggestion'
      cacheParts = (
        <Text color={color}>
          {cells === 0 ? '' : `${bar(left / ttl, cells)} `}
          {cacheText}
        </Text>
      )
    } else {
      cacheParts = <Text color="error">{cells === 0 ? '' : `${bar(0, cells)} `}{cacheText}</Text>
    }

    const limitParts = shown.map(({ limit, label, percent, reset }) => {
      const color = limit.percentUsed >= 95 ? 'error' : limit.percentUsed >= 80 ? 'warning' : 'suggestion'
      return (
        <Text>
          <Text dimColor>{label} </Text>
          {cells > 0 && <Text color={color}>{bar(limit.percentUsed / 100, cells)} </Text>}
          <Text weight="bold">{percent}</Text>
          {reset !== undefined && <Text dimColor> resets in {reset}</Text>}
        </Text>
      )
    })

    return (
      <Box flexDirection="row" gap={1} justifyContent="space-between">
        {row('cache', cacheParts, ...limitParts)}
        <Button
          key="auto"
          label={autoText}
          variant={plan === null ? 'secondary' : 'primary'}
          onPress={() => chooseAuto($)}
        />
      </Box>
    )
  })
}
