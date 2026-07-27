import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  CodexDebugModelsProvider,
  ModelCatalogResolver,
  parseCodexDebugModels,
  permittedReviewers,
  validateModelSelection,
} from '../../plugins/plan-forge-flow/scripts/model-catalog.mjs';

const observedAt = '2026-07-24T00:00:00.000Z';

function model(overrides = {}) {
  return {
    slug: 'gpt-5.6-sol',
    display_name: 'GPT-5.6-Sol',
    description: 'Latest frontier agentic coding model.',
    default_reasoning_level: 'low',
    supported_reasoning_levels: [
      { effort: 'low', description: 'Fast responses with lighter reasoning' },
      { effort: 'medium', description: 'Balances speed and reasoning depth' },
      { effort: 'high', description: 'Greater reasoning depth' },
      { effort: 'ultra', description: 'Automatic task delegation' },
    ],
    priority: 1,
    visibility: 'list',
    ...overrides,
  };
}

function debugJson(rows) {
  return JSON.stringify({ models: rows });
}

test('parses real CLI fields and exposes the advertised default effort first for pickers', () => {
  const catalog = parseCodexDebugModels(debugJson([model({ default_reasoning_level: 'high' })]), observedAt);
  assert.equal(catalog.generatedAt, observedAt);
  assert.equal(catalog.models.length, 1);
  assert.equal(catalog.models[0].slug, 'gpt-5.6-sol');
  assert.equal(catalog.models[0].displayName.value, 'GPT-5.6-Sol');
  assert.equal(catalog.models[0].description.value, 'Latest frontier agentic coding model.');
  assert.equal(catalog.models[0].defaultEffort.value, 'high');
  assert.deepEqual(catalog.models[0].supportedEfforts.value, ['low', 'medium', 'high']);
  assert.deepEqual(catalog.models[0].supportedReasoningLevels.value, [
    { effort: 'low', description: 'Fast responses with lighter reasoning' },
    { effort: 'medium', description: 'Balances speed and reasoning depth' },
    { effort: 'high', description: 'Greater reasoning depth' },
  ]);
  assert.deepEqual(catalog.models[0].pickerReasoningLevels.value, [
    { effort: 'high', description: 'Greater reasoning depth' },
    { effort: 'low', description: 'Fast responses with lighter reasoning' },
    { effort: 'medium', description: 'Balances speed and reasoning depth' },
  ]);
  assert.equal(catalog.models[0].priority.value, 1);
  assert.equal(catalog.models[0].visibility.value, 'list');
  assert.equal('activeModel' in catalog, false);
  assert.equal('activeEffort' in catalog, false);
});

test('retains only list-visible models and excludes ultra efforts', () => {
  const catalog = parseCodexDebugModels(debugJson([
    model(),
    model({ slug: 'hidden', visibility: 'hidden', priority: 0 }),
  ]), observedAt);
  assert.deepEqual(catalog.models.map((item) => item.slug), ['gpt-5.6-sol']);
  assert.ok(!catalog.models[0].supportedEfforts.value.includes('ultra'));
});

test('sorts by ascending numeric priority and preserves source order for ties', () => {
  const catalog = parseCodexDebugModels(debugJson([
    model({ slug: 'second-tie', display_name: 'Second tie', priority: 2 }),
    model({ slug: 'weakest', display_name: 'Weakest', priority: 3 }),
    model({ slug: 'strongest', display_name: 'Strongest', priority: 1 }),
    model({ slug: 'fourth-tie', display_name: 'Fourth tie', priority: 2 }),
  ]), observedAt);
  assert.deepEqual(catalog.models.map((item) => item.slug), [
    'strongest',
    'second-tie',
    'fourth-tie',
    'weakest',
  ]);
});

test('falls back to the slug when display_name is absent or empty', () => {
  const absent = model();
  delete absent.display_name;
  const catalog = parseCodexDebugModels(debugJson([
    absent,
    model({ slug: 'empty-name', display_name: '', priority: 2 }),
  ]), observedAt);
  assert.equal(catalog.models[0].displayName.value, 'gpt-5.6-sol');
  assert.equal(catalog.models[1].displayName.value, 'empty-name');
});

test('resolver uses only codex debug models and propagates provider failure', async () => {
  let calls = 0;
  const failure = new Error('CLI unavailable');
  const resolver = new ModelCatalogResolver({
    debug: {
      async getCatalog() {
        calls++;
        throw failure;
      },
    },
  });
  await assert.rejects(() => resolver.getCatalog(), failure);
  assert.equal(calls, 1);
});

test('selection validation depends only on visible CLI membership and advertised non-ultra effort', () => {
  const catalog = parseCodexDebugModels(debugJson([
    model({ slug: 'stronger', display_name: 'Stronger', priority: 1 }),
    model({ slug: 'weaker', display_name: 'Weaker', priority: 2 }),
  ]), observedAt);
  assert.deepEqual(validateModelSelection({
    reviewer: { model: 'weaker', effort: 'medium' },
    builder: { model: 'stronger', effort: 'low' },
  }, catalog), {
    valid: true,
    reviewer: { model: 'weaker', effort: 'medium' },
    builder: { model: 'stronger', effort: 'low' },
    catalogGeneratedAt: observedAt,
  });
  assert.throws(
    () => validateModelSelection({ reviewer: { model: 'missing', effort: 'high' } }, catalog),
    /absent from the visible CLI catalog/,
  );
  assert.throws(
    () => validateModelSelection({ reviewer: { model: 'stronger', effort: 'max' } }, catalog),
    /does not advertise effort max/,
  );
  assert.throws(
    () => validateModelSelection({ reviewer: { model: 'stronger', effort: 'ultra' } }, catalog),
    /ultra is prohibited/,
  );
});

test('reviewer enumeration follows CLI model order and default-first effort metadata', () => {
  const catalog = parseCodexDebugModels(debugJson([
    model({ slug: 'second', display_name: 'Second', priority: 2, default_reasoning_level: 'high' }),
    model({ slug: 'first', display_name: 'First', priority: 1, default_reasoning_level: 'medium' }),
  ]), observedAt);
  const options = permittedReviewers(catalog);
  assert.deepEqual(options.slice(0, 3).map(({ model: slug, effort }) => [slug, effort]), [
    ['first', 'medium'],
    ['first', 'low'],
    ['first', 'high'],
  ]);
  assert.equal(options[3].model, 'second');
});

test('provider invokes only codex debug models', async () => {
  let invocation;
  const provider = new CodexDebugModelsProvider({
    exec(command, args) {
      invocation = { command, args };
      return debugJson([model()]);
    },
  });
  const catalog = await provider.getCatalog({ observedAt });
  assert.deepEqual(invocation, { command: 'codex', args: ['debug', 'models'] });
  assert.deepEqual(catalog.models.map((item) => item.slug), ['gpt-5.6-sol']);
});

test('fails closed for malformed or unusable CLI catalogs', () => {
  const malformed = [
    ['not JSON', /invalid JSON/],
    ['[]', /no models array/],
    ['{"unexpected":true}', /no models array/],
    [debugJson([]), /no visible usable models/],
    [debugJson([model({ visibility: 'hidden' })]), /no visible usable models/],
    [debugJson([model({ priority: '1' })]), /invalid priority/],
    [debugJson([model({ supported_reasoning_levels: [] })]), /invalid supported_reasoning_levels/],
    [debugJson([model({ default_reasoning_level: 'max' })]), /default_reasoning_level/],
    [debugJson([model({ slug: '' })]), /invalid slug/],
    [debugJson([model({ visibility: undefined })]), /invalid visibility/],
  ];
  for (const [input, expected] of malformed) {
    assert.throws(() => parseCodexDebugModels(input, observedAt), expected);
  }
});
