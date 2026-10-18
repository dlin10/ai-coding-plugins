import { describe, expect, mock, test } from 'claude-code/testing'
import type { Engine } from 'claude-code/testing'
import type { On, SessionRateLimit, TurnUsage } from 'claude-code'

import { learnTtl } from '../hooks/format'

const START = Date.parse('2026-10-18T10:00:00Z')
const MINUTE = 60_000

const BAND = {
  plugin: 'limits-cache',
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
  command: 'limits-cache',
  args: '',
  origin: { kind: 'composer' },
  presentation: { isFullscreen: false, columns: 120 },
} as const

type World = { usage: TurnUsage | null; rateLimits: SessionRateLimit[] }

function world(on: On, store: Record<string, unknown> = {}) {
  const state: World = { usage: null, rateLimits: [] }
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

  return { state, clock }
}

async function start($: Engine) {
  await $.session.start({ cwd: '/', surface: 'terminal', isInteractive: true })
}

async function step($: Engine, index = 0) {
  const stream = $.turn.step({ turnId: 't1', index, model: 'claude-test', messageCount: 1 })
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
  test('the band says when there is no reading yet', async ($, on) => {
    world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    expect(await ui.find({ text: /нет данных/ })).toBeDefined()
  })

  test('the band shows each window with its share and reset', async ($, on) => {
    const { state } = world(on)
    await start($)
    state.rateLimits = [
      { kind: 'five_hour', percentUsed: 42, resetsAt: new Date(START + 134 * MINUTE).toISOString() },
      { kind: 'seven_day', percentUsed: 87.5 },
    ]
    await $.session.measure({
      context: { window: 200_000 },
      rateLimits: state.rateLimits,
      changed: ['rateLimits'],
    })

    for (const surface of ['terminal', 'desktop'] as const) {
      const ui = await $.ui.mount({ ...BAND, surface })
      expect(await ui.find({ text: /5ч ████░░░░░░ 42% ↻ 2ч 14м/ })).toBeDefined()
      expect(await ui.find({ text: /неделя █████████░ 88%/ })).toBeDefined()
      await ui.unmount()
    }
  })

  test('a narrow band drops the bars', async ($, on) => {
    world(on)
    await start($)
    await $.session.measure({
      context: { window: 200_000 },
      rateLimits: [{ kind: 'five_hour', percentUsed: 42 }],
      changed: ['rateLimits'],
    })
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal', props: { ...BAND.props, bodyColumns: 50 } })
    expect(await ui.find({ text: /5ч 42%/ })).toBeDefined()
  })
})

describe('cache', () => {
  test('counts down from the last request and goes cold', async ($, on) => {
    const { state, clock } = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    expect(await ui.find({ text: /ещё не было запросов/ })).toBeDefined()

    state.usage = usage(180_000, 2_000)
    await step($)
    expect(await ui.find({ text: /● тёплый 5:00/ })).toBeDefined()
    expect(await ui.find({ text: /попадание 99% · в кэше 182k/ })).toBeDefined()

    await clock.advance(4 * MINUTE)
    expect(await ui.find({ text: /● тёплый 1:00/ })).toBeDefined()

    await clock.advance(3 * MINUTE)
    expect(await ui.find({ text: /○ остыл 2м назад/ })).toBeDefined()
    expect(await ui.find({ text: /заново запишет ~182k/ })).toBeDefined()
  })

  test('a hit after ten quiet minutes teaches a one-hour TTL', async ($, on) => {
    const { state, clock } = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })

    state.usage = usage(0, 50_000)
    await step($)
    expect(await ui.find({ text: /TTL 5м, по умолч\./ })).toBeDefined()

    await clock.advance(10 * MINUTE)
    state.usage = usage(50_000, 500)
    await step($, 1)
    expect(await ui.find({ text: /● тёплый 60:00/ })).toBeDefined()
    expect(await ui.find({ text: /TTL 1ч, изучен/ })).toBeDefined()
  })

  test('a learned TTL is read back in the next session', async ($, on) => {
    const { state } = world(on, { ttlMs: 60 * MINUTE })
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    state.usage = usage(0, 50_000)
    await step($)
    expect(await ui.find({ text: /TTL 1ч, изучен/ })).toBeDefined()
  })

  test('a pinned TTL is not relearned', { options: { cacheTtl: '5m' } }, async ($, on) => {
    const { state, clock } = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })

    state.usage = usage(0, 50_000)
    await step($)
    await clock.advance(10 * MINUTE)
    state.usage = usage(50_000, 500)
    await step($, 1)
    expect(await ui.find({ text: /\(TTL 5м\)/ })).toBeDefined()
  })

  test('a subagent request leaves the countdown alone', async ($, on) => {
    const { state } = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })

    state.usage = usage(1_000, 1_000)
    const stream = $.turn.step({ turnId: 't2', index: 0, model: 'claude-test', messageCount: 1, agentId: 'a1' })
    for await (const _ of stream) {
      // drain
    }
    await stream.result
    expect(await ui.find({ text: /ещё не было запросов/ })).toBeDefined()
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
  expect(await ui.find({ text: /Лимиты/ })).toBeDefined()

  await $.command.run(TOGGLE)
  expect(await ui.find({ text: /Лимиты/ })).toBeUndefined()

  await $.command.run(TOGGLE)
  expect(await ui.find({ text: /Лимиты/ })).toBeDefined()
})

test('learnTtl ignores a miss after a model switch', () => {
  const previous = { at: 0, model: 'a', read: 0, write: 100_000, uncached: 0 }
  const current = { at: 10 * MINUTE, model: 'b', read: 0, write: 100_000, uncached: 0 }
  expect(learnTtl(previous, current)).toBeUndefined()
  expect(learnTtl(previous, { ...current, model: 'a' })).toBe(5 * MINUTE)
  expect(learnTtl(previous, { ...current, read: 90_000, write: 0 })).toBe(60 * MINUTE)
})
