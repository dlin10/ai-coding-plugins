import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register, RenderChildren, SessionMessage } from 'claude-code'

import type { CacheReading, Limit, Recap } from '../types'
import {
  AUTO_HOURS,
  MINUTE,
  ONE_HOUR,
  autoLabel,
  autoLead,
  bar,
  cachedPrefix,
  humanSpan,
  isRunningLow,
  kindLabel,
  learnTtl,
  minutesLeft,
  parseHours,
  resetsIn,
  tokens,
  ttlFromOption,
  ttlLabel,
  warmsFor,
  warnLead,
} from './format'

const COMMAND = 'session-pulse'
const STORED_TTL = 'ttlMs'
const AUTO_QUESTION = 'На сколько включить автоподогрев кэша?'
const WARM_PROMPT = 'Это служебная проверка, чтобы кэш не остыл. Ответь одним словом: ок.'
const RECAP_SYSTEM =
  'Ты читаешь переписку пользователя с агентом Claude Code. В двух-трёх предложениях по-русски ' +
  'перескажи: чего хотел пользователь, что агент уже сделал, на чём остановились и какой шаг следующий. ' +
  'Пиши о пользователе и агенте в третьем лице, без вступлений, заголовков и списков.'
const RECAP_MAX_CHARS = 24_000
const TICK_MS = 5000

const limits = atom({ plugin: 'session-pulse', key: 'limits' } as const, [])
const cache = atom({ plugin: 'session-pulse', key: 'cache' } as const, null)
const ttlMs = atom({ plugin: 'session-pulse', key: 'ttlMs' } as const, ONE_HOUR)
const isTtlLearned = atom({ plugin: 'session-pulse', key: 'isTtlLearned' } as const, false)
const now = atom({ plugin: 'session-pulse', key: 'now' } as const, 0)
const isHidden = atom({ plugin: 'session-pulse', key: 'isHidden' } as const, false)
const warnHiddenAt = atom({ plugin: 'session-pulse', key: 'warnHiddenAt' } as const, null)
const isWarming = atom({ plugin: 'session-pulse', key: 'isWarming' } as const, false)
const auto = atom({ plugin: 'session-pulse', key: 'auto' } as const, null)
const recap = atom({ plugin: 'session-pulse', key: 'recap' } as const, null)

const toLimits = (windows: readonly Limit[]): Limit[] =>
  windows.map(({ kind, percentUsed, resetsAt }) => ({ kind, percentUsed, resetsAt }))

let lastTickWrite = 0
let lastTickMinute = 0
let isTurnRunning = false

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
      $.ui.toast(`Не удалось подогреть кэш: ${reply.reason}`)
      return false
    }
    const { usage } = reply
    if (previous !== null && usage.cache_read_input_tokens < cachedPrefix(previous) * 0.5) {
      $.ui.toast('Кэш уже остыл: подогрев перечитал переписку заново')
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
    $.ui.toast('Автоподогрев выключен')
    return
  }

  const ttl = await read($, ttlMs)
  let answer: string
  try {
    answer = await $.ui.ask(AUTO_QUESTION, {
      header: 'Автоподогрев',
      options: AUTO_HOURS.map(hours => autoLabel(hours, ttl)),
    })
  } catch {
    return
  }
  const hours = parseHours(answer)
  if (hours === undefined) {
    $.ui.toast(`Не понял, на сколько часов: «${answer}»`)
    return
  }
  const at = await $.clock.now()
  const total = warmsFor(hours, ttl)
  await update($, auto, () => ({ until: at + hours * ONE_HOUR, done: 0, total }))
  $.ui.toast(`Автоподогрев включён: ${autoLabel(hours, ttl)}`)
}

function messageLine(message: SessionMessage): string {
  const who = message.role === 'user' ? 'Пользователь' : 'Агент'
  const tools = message.toolUses.map(use => use.tool)
  const toolNote = tools.length > 0 ? ` [инструменты: ${tools.join(', ')}]` : ''
  return `${who}: ${message.text.slice(0, 2000)}${toolNote}`
}

async function summarize($: EngineInterface) {
  if ((await read($, recap))?.status === 'loading') return
  await update($, recap, (): Recap => ({ status: 'loading' }))

  const messages = (await $.session.messages()).filter(
    message => message.text.trim() !== '' || message.toolUses.length > 0,
  )
  if (messages.length === 0) {
    await update($, recap, (): Recap => ({ status: 'done', text: 'В этой сессии ещё ничего не происходило.' }))
    return
  }

  const transcript = messages.map(messageLine).join('\n\n')
  const prompt = transcript.length > RECAP_MAX_CHARS ? `…${transcript.slice(-RECAP_MAX_CHARS)}` : transcript
  const reply = await $.model.complete({
    model: 'haiku',
    system: RECAP_SYSTEM,
    prompt,
    maxTokens: 400,
    timeoutMs: 60_000,
  })
  await update($, recap, (): Recap =>
    reply.isAnswered && reply.text.trim() !== ''
      ? { status: 'done', text: reply.text.trim() }
      : { status: 'error', text: `Haiku не ответил${reply.isAnswered ? '' : ` (${reply.reason})`}` },
  )
}

async function compact($: EngineInterface) {
  const result = await $.session.compact()
  if (result.skip !== undefined) {
    $.ui.toast(`Сжатие не выполнено: ${result.skip}`)
    return
  }
  const reading = await read($, cache)
  await update($, warnHiddenAt, () => reading?.at ?? null)
}

async function hideWarning($: EngineInterface) {
  const reading = await read($, cache)
  await update($, warnHiddenAt, () => reading?.at ?? null)
}

// Auto-warming warms once the countdown reaches its lead, and stops when its
// hours or warms run out or the cache went cold anyway. The band shows whole
// minutes, so it is redrawn when the minute changes, or every half minute for
// the reset times.
async function tick($: EngineInterface) {
  const at = await $.clock.now()
  const reading = await read($, cache)
  const ttl = await read($, ttlMs)
  const left = reading === null ? -1 : reading.at + ttl - at

  const plan = await read($, auto)
  if (plan !== null) {
    if (at >= plan.until || plan.done >= plan.total) {
      await update($, auto, () => null)
      $.ui.toast('Автоподогрев закончился')
    } else if (reading !== null && left <= 0) {
      await update($, auto, () => null)
      $.ui.toast('Кэш остыл, автоподогрев выключен')
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
      description: 'Show or hide the session pulse band: limits, cache and recap',
    })

    const usage = await $.session.usage()
    await update($, limits, () => toLimits(usage.rateLimits))

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
      await update($, limits, () => toLimits(e.rateLimits))
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
      const learned = learnTtl(previous, current)
      if (learned !== undefined && learned !== (await read($, ttlMs))) {
        await update($, ttlMs, () => learned)
        await update($, isTtlLearned, () => true)
        await $.store.set(STORED_TTL, learned)
        $.ui.toast(`Похоже, кэш живёт ${ttlLabel(learned)}`)
      }
    }

    return result
  })

  on('session.end', { reason: 'clear' }, async ($, e, next) => {
    await update($, cache, () => null)
    await update($, auto, () => null)
    await update($, recap, () => null)

    return next(e)
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
    const summary = await read($, recap)
    const warming = await read($, isWarming)
    const isNarrow = e.props.bodyColumns < 80

    const left = reading === null ? 0 : reading.at + ttl - t
    const isWarm = reading !== null && left > 0
    const showWarning =
      reading !== null && left <= warnLead(ttl) && (await read($, warnHiddenAt)) !== reading.at

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

    const rows = []

    if (summary !== null) {
      rows.push(
        row(
          'где мы',
          summary.status === 'loading' ? (
            <Text dimColor>Haiku читает переписку…</Text>
          ) : (
            <Text color={summary.status === 'error' ? 'error' : undefined}>{summary.text}</Text>
          ),
          <Button key="recap-hide" label="Скрыть" onPress={() => update($, recap, () => null)} />,
        ),
      )
    }

    if (showWarning && reading !== null) {
      const prefix = tokens(cachedPrefix(reading))
      rows.push(
        row(
          'кэш',
          isWarm ? (
            <Text weight="bold">остынет через {minutesLeft(left)}</Text>
          ) : (
            <Text color="error" weight="bold">остыл {humanSpan(-left)} назад</Text>
          ),
          <Text>
            <Text dimColor>следующий ход перечитает </Text>
            <Text weight="bold">{prefix}</Text>
            <Text dimColor> токенов</Text>
          </Text>,
          isWarm && (
            <Button key="warm" label={warming ? 'Греется…' : 'Подогреть'} onPress={() => warm($)} />
          ),
          <Button key="compact" label="Сжать" onPress={() => compact($)} />,
          <Button key="warn-hide" label="Скрыть" onPress={() => hideWarning($)} />,
        ),
      )
    }

    let cacheParts
    if (reading === null) {
      cacheParts = <Text dimColor>ещё не было запросов</Text>
    } else if (isWarm) {
      const color = isRunningLow(left, ttl) ? 'warning' : 'suggestion'
      cacheParts = (
        <Text color={color}>
          {isNarrow ? '' : `${bar(left / ttl)} `}
          {minutesLeft(left)}
        </Text>
      )
    } else {
      cacheParts = <Text color="error">{isNarrow ? '' : `${bar(0)} `}остыл</Text>
    }

    const limitParts = windows.map(limit => {
      const reset = limit.kind === 'five_hour' ? resetsIn(limit, t) : undefined
      const color = limit.percentUsed >= 95 ? 'error' : limit.percentUsed >= 80 ? 'warning' : 'suggestion'
      return (
        <Text>
          <Text dimColor>{kindLabel(limit.kind)} </Text>
          {!isNarrow && <Text color={color}>{bar(limit.percentUsed / 100)} </Text>}
          <Text weight="bold">{Math.round(limit.percentUsed)}%</Text>
          {reset !== undefined && <Text dimColor> {reset}</Text>}
        </Text>
      )
    })

    rows.push(
      row(
        'кэш',
        cacheParts,
        ...limitParts,
        <Button
          key="auto"
          label={plan === null ? 'Авто' : `Авто ${plan.done}/${plan.total}`}
          variant={plan === null ? 'secondary' : 'primary'}
          onPress={() => chooseAuto($)}
        />,
        <Button
          key="recap"
          label="Где мы"
          variant="primary"
          onPress={() => summarize($)}
        />,
      ),
    )

    return <Box flexDirection="column">{rows}</Box>
  })
}
