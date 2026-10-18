import { describe, expect, mock, test } from 'claude-code/testing'
import type { Engine } from 'claude-code/testing'
import type { ModelUsage, On, SessionMessage, SessionRateLimit, TurnUsage } from 'claude-code'

import { autoLabel, learnTtl, parseHours, warmsWord } from '../hooks/format'

const START = Date.parse('2026-10-18T10:00:00Z')
const MINUTE = 60_000
const HOUR = 60 * MINUTE

const BAND = {
  plugin: 'session-pulse',
  component: 'AbovePrompt',
  props: {
    hasSurvey: false,
    isWorking: false,
    maxRows: 10,
    bodyColumns: 120,
    scroll: { offset: 0, bodyRows: 10 },
    view: {},
  },
} as const

const TOGGLE = {
  command: 'session-pulse',
  args: '',
  origin: { kind: 'composer' },
  presentation: { isFullscreen: false, columns: 120 },
} as const

type World = {
  usage: TurnUsage | null
  rateLimits: SessionRateLimit[]
  forks: number
  forkRead: number
  answer: string | null
  messages: SessionMessage[]
  recapPrompt: string
  compactions: number
}

function world(on: On, store: Record<string, unknown> = {}) {
  const state: World = {
    usage: null,
    rateLimits: [],
    forks: 0,
    forkRead: 80_000,
    answer: null,
    messages: [],
    recapPrompt: '',
    compactions: 0,
  }
  const clock = mock.clock(on, { now: START })
  mock.store(on, store)
  on('session.start', ($, e) => ({ cwd: e.cwd }))
  on('session.usage', () => ({
    value: { startedAt: START, context: { window: 200_000 }, rateLimits: state.rateLimits },
  }))
  on('session.measure', ($, e) => ({ changed: [...e.changed] }))
  on('command.register', ($, e) => ({ value: { command: e.name } }))
  on('ui.toast', () => ({ value: undefined }))
  on('turn.step', async function* ($, e) {
    return {
      turnId: e.turnId,
      index: e.index,
      answer: '',
      toolUses: [],
      stopReason: 'end_turn',
      usage: state.usage,
    }
  })
  on('model.fork', () => {
    state.forks += 1
    const usage: ModelUsage = {
      input_tokens: 20,
      output_tokens: 2,
      cache_read_input_tokens: state.forkRead,
      cache_creation_input_tokens: 0,
    }
    return { value: { isAnswered: true, text: 'ок', usage } }
  })
  on('model.complete', ($, e) => {
    state.recapPrompt = e.prompt
    const usage: ModelUsage = {
      input_tokens: 100,
      output_tokens: 40,
      cache_read_input_tokens: 0,
      cache_creation_input_tokens: 0,
    }
    return { value: { isAnswered: true, text: 'Пользователь просил README, агент его написал.', usage } }
  })
  on('session.messages', () => ({ value: state.messages }))
  on('session.compact', () => {
    state.compactions += 1
    return { messages: [{ role: 'user', text: 'Сводка разговора', toolUses: [] }] }
  })
  on('tool.call', { tool: 'AskUserQuestion' }, ($, e) => {
    if (state.answer === null) return { deny: 'skipped' }
    const question = e.questions[0]?.question ?? ''
    return { result: { questions: e.questions, answers: { [question]: state.answer } } }
  })

  return { state, clock }
}

async function start($: Engine) {
  await $.session.start({ cwd: '/', surface: 'terminal', isInteractive: true })
}

async function step($: Engine, index = 0, agentId?: string) {
  const stream = $.turn.step({ turnId: 't1', index, model: 'claude-test', messageCount: 1, agentId })
  for await (const _ of stream) {
    // drain
  }
  return stream.result
}

function usage(read: number, write: number): TurnUsage {
  return {
    model: 'claude-test',
    input_tokens: 10,
    output_tokens: 100,
    cache_read_input_tokens: read,
    cache_creation_input_tokens: write,
  }
}

describe('limits', () => {
  test('the band shows each window, and the five-hour reset', async ($, on) => {
    const { state } = world(on)
    await start($)
    state.rateLimits = [
      { kind: 'five_hour', percentUsed: 3, resetsAt: new Date(START + 182 * MINUTE).toISOString() },
      { kind: 'seven_day', percentUsed: 44, resetsAt: new Date(START + 4 * 24 * HOUR).toISOString() },
    ]
    await $.session.measure({ context: { window: 200_000 }, rateLimits: state.rateLimits, changed: ['rateLimits'] })

    for (const surface of ['terminal', 'desktop'] as const) {
      const ui = await $.ui.mount({ ...BAND, surface })
      expect(await ui.find({ text: /^5ч ░░░░░░░░ 3% 3 ч 2 мин$/ })).toBeDefined()
      expect(await ui.find({ text: /^нед ████░░░░ 44%$/ })).toBeDefined()
      await ui.unmount()
    }
  })
})

describe('cache', () => {
  test('counts down from the last request, warns, then goes cold', async ($, on) => {
    const { state, clock } = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    expect(await ui.find({ text: /ещё не было запросов/ })).toBeDefined()

    state.usage = usage(79_000, 2_000)
    await step($)
    expect(await ui.find({ text: /^████████ 60 мин$/ })).toBeDefined()

    await clock.advance(42 * MINUTE)
    expect(await ui.find({ text: /^██░░░░░░ 18 мин$/ })).toBeDefined()
    expect(await ui.find({ text: /остынет через/ })).toBeUndefined()

    await clock.advance(13 * MINUTE)
    expect(await ui.find({ text: /^остынет через 5 мин$/ })).toBeDefined()
    expect(await ui.find({ text: /^следующий ход перечитает ~81K токенов$/ })).toBeDefined()
    expect(await ui.find({ key: 'warm' })).toBeDefined()

    await clock.advance(7 * MINUTE)
    expect(await ui.find({ text: /^остыл 2 мин назад$/ })).toBeDefined()
    expect(await ui.find({ key: 'warm' })).toBeUndefined()
  })

  test('warming reads the cache through a fork and restarts the countdown', async ($, on) => {
    const { state, clock } = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    state.usage = usage(79_000, 2_000)
    await step($)
    await clock.advance(56 * MINUTE)

    await ui.press({ key: 'warm' })
    expect(state.forks).toBe(1)
    expect(await ui.find({ text: /^████████ 60 мин$/ })).toBeDefined()
    expect(await ui.find({ text: /остынет через/ })).toBeUndefined()
  })

  test('hiding the warning keeps it hidden until the next request', async ($, on) => {
    const { state, clock } = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    state.usage = usage(79_000, 2_000)
    await step($)
    await clock.advance(56 * MINUTE)

    await ui.press({ key: 'warn-hide' })
    expect(await ui.find({ text: /остынет через/ })).toBeUndefined()

    await step($, 1)
    await clock.advance(56 * MINUTE)
    expect(await ui.find({ text: /остынет через/ })).toBeDefined()
  })

  test('compacting runs the session compaction and hides the warning', async ($, on) => {
    const { state, clock } = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    state.usage = usage(79_000, 2_000)
    await step($)
    await clock.advance(56 * MINUTE)

    await ui.press({ key: 'compact' })
    expect(state.compactions).toBe(1)
    expect(await ui.find({ text: /остынет через/ })).toBeUndefined()
  })

  test('a subagent request leaves the countdown alone', async ($, on) => {
    const { state } = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    state.usage = usage(1_000, 1_000)
    await step($, 0, 'a1')
    expect(await ui.find({ text: /ещё не было запросов/ })).toBeDefined()
  })

  test('a miss after ten quiet minutes teaches a five-minute TTL', async ($, on) => {
    const { state, clock } = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    state.usage = usage(0, 50_000)
    await step($)
    await clock.advance(10 * MINUTE)
    state.usage = usage(0, 50_000)
    await step($, 1)
    expect(await ui.find({ text: /^████████ 5 мин$/ })).toBeDefined()
  })
})

describe('auto-warming', () => {
  test('asks for how long, then warms before each cooling', async ($, on) => {
    const { state, clock } = world(on)
    state.answer = autoLabel(2, HOUR)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    state.usage = usage(79_000, 2_000)
    await step($)

    await ui.press({ key: 'auto' })
    expect(await ui.find({ key: 'auto', text: 'Авто 0/2' })).toBeDefined()

    await clock.advance(59 * MINUTE)
    expect(state.forks).toBe(1)
    expect(await ui.find({ key: 'auto', text: 'Авто 1/2' })).toBeDefined()

    await clock.advance(58 * MINUTE)
    expect(state.forks).toBe(2)

    await clock.advance(10 * MINUTE)
    expect(state.forks).toBe(2)
    expect(await ui.find({ key: 'auto', text: 'Авто' })).toBeDefined()
  })

  test('skipping the question leaves it off, and a second press turns it off', async ($, on) => {
    const { state } = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    state.usage = usage(79_000, 2_000)
    await step($)

    await ui.press({ key: 'auto' })
    expect(await ui.find({ key: 'auto', text: 'Авто' })).toBeDefined()

    state.answer = '3'
    await ui.press({ key: 'auto' })
    expect(await ui.find({ key: 'auto', text: 'Авто 0/3' })).toBeDefined()
    await ui.press({ key: 'auto' })
    expect(await ui.find({ key: 'auto', text: 'Авто' })).toBeDefined()
  })
})

describe('recap', () => {
  test('Haiku retells the conversation, and Скрыть takes the row down', async ($, on) => {
    const { state } = world(on)
    state.messages = [
      { role: 'user', text: 'Напиши README', toolUses: [] },
      { role: 'assistant', text: 'Готово', toolUses: [] },
    ]
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })

    await ui.press({ key: 'recap' })
    expect(state.recapPrompt).toContain('Пользователь: Напиши README')
    expect(await ui.find({ text: /^где мы$/ })).toBeDefined()
    expect(await ui.find({ text: /агент его написал/ })).toBeDefined()

    await ui.press({ key: 'recap-hide' })
    expect(await ui.find({ text: /^где мы$/ })).toBeUndefined()
  })
})

test('the command hides and shows the band', async ($, on) => {
  world(on)
  on('ui.render', { component: 'AbovePrompt' }, ($, e) => {
    const { Text } = $.ui.resolve(e)
    return <Text>engine band</Text>
  })
  await start($)
  const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
  expect(await ui.find({ key: 'recap' })).toBeDefined()

  await $.command.run(TOGGLE)
  expect(await ui.find({ key: 'recap' })).toBeUndefined()

  await $.command.run(TOGGLE)
  expect(await ui.find({ key: 'recap' })).toBeDefined()
})

test('the helpers', () => {
  expect(autoLabel(1, HOUR)).toBe('1 ч (1 подогрев)')
  expect(autoLabel(2, HOUR)).toBe('2 ч (2 подогрева)')
  expect(autoLabel(8, HOUR)).toBe('8 ч (8 подогревов)')
  expect(warmsWord(21)).toBe('подогрев')
  expect(warmsWord(12)).toBe('подогревов')
  expect(parseHours('4 ч (4 подогрева)')).toBe(4)
  expect(parseHours('1,5')).toBe(1.5)
  expect(parseHours('много')).toBeUndefined()

  const previous = { at: 0, model: 'a', read: 0, write: 100_000, uncached: 0 }
  const current = { at: 10 * MINUTE, model: 'b', read: 0, write: 100_000, uncached: 0 }
  expect(learnTtl(previous, current)).toBeUndefined()
  expect(learnTtl(previous, { ...current, model: 'a' })).toBe(5 * MINUTE)
  expect(learnTtl(previous, { ...current, read: 90_000, write: 0 })).toBe(HOUR)
})
