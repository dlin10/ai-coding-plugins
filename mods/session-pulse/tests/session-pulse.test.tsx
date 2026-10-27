import { describe, expect, mock, test } from 'claude-code/testing'
import type { Engine } from 'claude-code/testing'
import type { ModelUsage, On, TurnUsage } from 'claude-code'

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

const DIALOG = {
  plugin: 'session-pulse',
  component: 'Pane',
  requestId: 'session-pulse-auto',
  props: {
    title: 'Auto-warm',
    isFocused: true,
    bodyColumns: 100,
    placement: 'inline',
    scroll: { offset: 0, bodyRows: 4 },
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
  // How long a request takes to its first chunk, then to its end.
  prefillMs: number
  streamMs: number
  forks: number
  forkRead: number
  opened: string[]
  closed: string[]
  asked: number
}

function world(on: On) {
  const state: World = {
    usage: null,
    prefillMs: 0,
    streamMs: 0,
    forks: 0,
    forkRead: 80_000,
    opened: [],
    closed: [],
    asked: 0,
  }
  const clock = mock.clock(on, { now: START })
  mock.store(on)
  on('session.start', ($, e) => ({ cwd: e.cwd }))
  on('session.end', ($, e) => ({ sessionId: e.sessionId }))
  on('command.register', ($, e) => ({ value: { command: e.name } }))
  on('ui.toast', () => ({ value: undefined }))
  on('ui.open', ($, e) => {
    state.opened.push(e.id)
    return { value: { isPlaced: true } }
  })
  on('ui.close', ($, e) => {
    state.closed.push(e.id)
    return { value: undefined }
  })
  on('turn.step', async function* ($, e) {
    if (state.prefillMs > 0) await clock.sleep(state.prefillMs)
    yield { kind: 'text', index: 0, text: 'ok' }
    if (state.streamMs > 0) await clock.sleep(state.streamMs)
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
    return { value: { isAnswered: true, text: 'ok', usage } }
  })
  on('tool.call', { tool: 'AskUserQuestion' }, () => {
    state.asked += 1
    return { deny: 'the mod asks in a dialog of its own' }
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

/** A main-thread or subagent request that streams `output` tokens, timed on the world's clock. */
async function streamedStep(
  $: Engine,
  { state, clock }: ReturnType<typeof world>,
  output: number,
  prefillMs: number,
  streamMs: number,
  index = 0,
  agentId?: string,
) {
  state.usage = { ...usage(79_000, 2_000), output_tokens: output }
  state.prefillMs = prefillMs
  state.streamMs = streamMs
  const done = step($, index, agentId)
  await clock.advance(prefillMs)
  await clock.advance(streamMs)
  await done
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

describe('band', () => {
  test('a narrowing band shortens the cache bar before it drops it, and keeps the speed', async ($, on) => {
    const w = world(on)
    await start($)
    await streamedStep($, w, 100, 0, 1_000)

    // The text takes 48 cells, the speed's 20 among them; the bar takes its width and a space.
    const rows: Array<[number, RegExp]> = [
      [57, /^████████ 60 min$/],
      [54, /^█████ 60 min$/],
      [51, /^60 min$/],
    ]
    for (const [bodyColumns, text] of rows) {
      const ui = await $.ui.mount({ ...BAND, props: { ...BAND.props, bodyColumns }, surface: 'desktop' })
      expect(await ui.find({ text })).toBeDefined()
      expect(await ui.find({ text: /^Avg speed 100 tok\/s$/ })).toBeDefined()
      await ui.unmount()
    }
  })

  test('Auto Warm sits at the right end of the main row', async ($, on) => {
    world(on)
    await start($)

    for (const surface of ['terminal', 'desktop'] as const) {
      const ui = await $.ui.mount({ ...BAND, surface })
      const spread = (await ui.findAll({ type: 'Box' })).filter(box => box.props.justifyContent === 'space-between')
      expect(spread).toHaveLength(1)
      expect(spread[0]?.children.at(-1)).toMatchObject({ type: 'Button', props: { key: 'auto' } })
      await ui.unmount()
    }
  })
})

describe('cache', () => {
  test('counts down from the last request, then goes cold, in the one row', async ($, on) => {
    const { state, clock } = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    expect(await ui.find({ text: /no requests yet/ })).toBeDefined()

    state.usage = usage(79_000, 2_000)
    await step($)
    expect(await ui.find({ text: /^████████ 60 min$/ })).toBeDefined()

    await clock.advance(42 * MINUTE)
    expect(await ui.find({ text: /^██░░░░░░ 18 min$/ })).toBeDefined()

    await clock.advance(14 * MINUTE)
    expect(await ui.find({ text: /^█░░░░░░░ 4 min$/ })).toBeDefined()
    expect(await ui.find({ text: /cools in|re-reads/ })).toBeUndefined()
    expect((await ui.findAll({ type: 'Button' })).map(button => button.key)).toEqual(['auto'])

    await clock.advance(6 * MINUTE)
    expect(await ui.find({ text: /^░░░░░░░░ cold$/ })).toBeDefined()
    expect(await ui.find({ text: /cold for/ })).toBeUndefined()
    expect((await ui.findAll({ type: 'Button' })).map(button => button.key)).toEqual(['auto'])
  })

  test('a subagent request leaves the countdown alone', async ($, on) => {
    const { state } = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    state.usage = usage(1_000, 1_000)
    await step($, 0, 'a1')
    expect(await ui.find({ text: /no requests yet/ })).toBeDefined()
  })

  test('a lone miss after quiet minutes keeps the hour: an entry can be evicted early', async ($, on) => {
    const { state, clock } = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    state.usage = usage(0, 50_000)
    await step($)
    await clock.advance(10 * MINUTE)
    await step($, 1)
    expect(await ui.find({ text: /^████████ 60 min$/ })).toBeDefined()
  })

  test('two misses in a row after quiet minutes teach a five-minute TTL', async ($, on) => {
    const { state, clock } = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    state.usage = usage(0, 50_000)
    await step($)
    await clock.advance(10 * MINUTE)
    await step($, 1)
    await clock.advance(MINUTE)
    state.usage = usage(50_000, 1_000)
    await step($, 2)
    await clock.advance(10 * MINUTE)
    state.usage = usage(0, 51_000)
    await step($, 3)
    expect(await ui.find({ text: /^████████ 5 min$/ })).toBeDefined()
  })

  test('a hit after quiet minutes between two misses keeps the hour', async ($, on) => {
    const { state, clock } = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    state.usage = usage(0, 50_000)
    await step($)
    await clock.advance(10 * MINUTE)
    await step($, 1)
    await clock.advance(10 * MINUTE)
    state.usage = usage(50_000, 1_000)
    await step($, 2)
    await clock.advance(10 * MINUTE)
    state.usage = usage(0, 51_000)
    await step($, 3)
    expect(await ui.find({ text: /^████████ 60 min$/ })).toBeDefined()
  })
})

describe('speed', () => {
  test('counts output tokens over streaming time, summed over the main thread', async ($, on) => {
    const w = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    expect(await ui.find({ text: /tok\/s/ })).toBeUndefined()

    // 100 tokens in 2 s of streaming; the 3 s before the first chunk do not count.
    await streamedStep($, w, 100, 3_000, 2_000)
    expect(await ui.find({ text: /^Avg speed 50 tok\/s$/ })).toBeDefined()

    // 400 more in 3 s: 500 in 5 s, not the mean of 50 and 133.
    await streamedStep($, w, 400, 1_000, 3_000, 1)
    expect(await ui.find({ text: /^Avg speed 100 tok\/s$/ })).toBeDefined()

    await streamedStep($, w, 1_000, 1_000, 1_000, 0, 'a1')
    expect(await ui.find({ text: /^Avg speed 100 tok\/s$/ })).toBeDefined()
  })

  test('/clear starts the count over', async ($, on) => {
    const w = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    await streamedStep($, w, 100, 1_000, 2_000)
    expect(await ui.find({ text: /^Avg speed 50 tok\/s$/ })).toBeDefined()

    await $.session.end({ reason: 'clear', sessionId: 's1', resume: { id: 's1' } })
    expect(await ui.find({ text: /tok\/s/ })).toBeUndefined()
  })
})

describe('auto-warming', () => {
  test('asks for how long in a dialog of its own, then warms before each cooling', async ($, on) => {
    const { state, clock } = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    state.usage = usage(79_000, 2_000)
    await step($)

    await ui.press({ key: 'auto' })
    expect(state.opened).toEqual(['session-pulse-auto'])
    const dialog = await $.ui.mount({ ...DIALOG, surface: 'terminal' })
    expect(await dialog.find({ key: 'auto-2', text: autoLabel(2, HOUR) })).toBeDefined()

    await dialog.press({ key: 'auto-2' })
    expect(state.closed).toEqual(['session-pulse-auto'])
    expect(await ui.find({ key: 'auto', text: 'Auto Warm 0/2' })).toBeDefined()

    await clock.advance(59 * MINUTE)
    expect(state.forks).toBe(1)
    expect(await ui.find({ key: 'auto', text: 'Auto Warm 1/2' })).toBeDefined()

    await clock.advance(58 * MINUTE)
    expect(state.forks).toBe(2)

    await clock.advance(10 * MINUTE)
    expect(state.forks).toBe(2)
    expect(await ui.find({ key: 'auto', text: 'Auto Warm' })).toBeDefined()
  })

  test('Cancel leaves it off, typed hours start it, and a second press turns it off', async ($, on) => {
    const { state } = world(on)
    await start($)
    const ui = await $.ui.mount({ ...BAND, surface: 'desktop' })
    state.usage = usage(79_000, 2_000)
    await step($)

    await ui.press({ key: 'auto' })
    const dialog = await $.ui.mount({ ...DIALOG, surface: 'desktop' })
    await dialog.press({ key: 'auto-cancel' })
    expect(state.closed).toEqual(['session-pulse-auto'])
    expect(await ui.find({ key: 'auto', text: 'Auto Warm' })).toBeDefined()

    await ui.press({ key: 'auto' })
    await dialog.input({ key: 'auto-hours', text: '3' })
    expect(await ui.find({ key: 'auto', text: 'Auto Warm 0/3' })).toBeDefined()
    await ui.press({ key: 'auto' })
    expect(await ui.find({ key: 'auto', text: 'Auto Warm' })).toBeDefined()
  })

  test('the dialog never goes through AskUserQuestion, on any surface', async ($, on) => {
    const { state } = world(on)
    await start($)

    for (const surface of ['terminal', 'desktop', 'mobile'] as const) {
      const ui = await $.ui.mount({ ...BAND, surface })
      await ui.press({ key: 'auto' })
      const dialog = await $.ui.mount({ ...DIALOG, surface })
      expect(await dialog.find({ key: 'auto-1' })).toBeDefined()
      await dialog.press({ key: 'auto-cancel' })
      await dialog.unmount()
      await ui.unmount()
    }
    expect(state.asked).toBe(0)
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
  expect(await ui.find({ key: 'auto' })).toBeDefined()

  await $.command.run(TOGGLE)
  expect(await ui.find({ key: 'auto' })).toBeUndefined()

  await $.command.run(TOGGLE)
  expect(await ui.find({ key: 'auto' })).toBeDefined()
})

test('the helpers', () => {
  expect(autoLabel(1, HOUR)).toBe('1 h (1 warm-up)')
  expect(autoLabel(2, HOUR)).toBe('2 h (2 warm-ups)')
  expect(autoLabel(8, 5 * MINUTE)).toBe('8 h (96 warm-ups)')
  expect(warmsWord(1)).toBe('warm-up')
  expect(parseHours('4 h (4 warm-ups)')).toBe(4)
  expect(parseHours('1,5')).toBe(1.5)
  expect(parseHours('many')).toBeUndefined()

  const previous = { at: 0, model: 'a', read: 0, write: 100_000, uncached: 0 }
  const current = { at: 10 * MINUTE, model: 'b', read: 0, write: 100_000, uncached: 0 }
  expect(learnTtl(previous, current)).toBeUndefined()
  expect(learnTtl(previous, { ...current, model: 'a' })).toBe(5 * MINUTE)
  expect(learnTtl(previous, { ...current, read: 90_000, write: 0 })).toBe(HOUR)
})
