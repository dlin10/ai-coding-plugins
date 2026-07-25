import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import {
  CachedModelCatalogProvider,
  CodexDebugModelsProvider,
  ModelCatalogResolver,
  SessionModelCatalogProvider,
  StaticPolicyProvider,
  field,
  mergeCatalogs,
  parseCodexDebugModels,
  validateModelSelection,
} from '../../plugins/plan-forge-flow/scripts/model-catalog.mjs';

const observedAt = '2026-07-24T00:00:00.000Z';

function debugJson(rows) {
  return JSON.stringify({ models: rows });
}

function normalizedCatalog(models) {
  return {
    version: 1,
    generatedAt: observedAt,
    activeModel: field('gpt-sol', 'test', { observedAt }),
    activeEffort: field('high', 'test', { observedAt }),
    degraded: false,
    trace: [],
    models: models.map(({ slug, priority, efforts = ['low', 'medium', 'high', 'xhigh', 'max'] }) => ({
      slug,
      description: field('test', 'test', { observedAt }),
      supportedEfforts: field(efforts, 'test', { observedAt }),
      priority: priority === null
        ? field(null, 'test', { known: false, observedAt })
        : field(priority, 'test', { observedAt }),
      visibility: field('list', 'test', { observedAt }),
      spawnAvailable: field(true, 'native-schema', { observedAt }),
    })),
  };
}

test('resolver selects a complete session/native catalog without invoking CLI', async () => {
  let debugCalls = 0;
  const resolver = new ModelCatalogResolver({
    session: new SessionModelCatalogProvider(),
    debug: { async getCatalog() { debugCalls++; throw new Error('must not run'); } },
    staticPolicy: new StaticPolicyProvider(),
  });
  const result = await resolver.getCatalog({
    activeModel: 'gpt-sol',
    activeEffort: 'high',
    observedAt,
    nativeModels: [{ slug: 'gpt-sol', priority: 1, supportedEfforts: ['low', 'high'] }],
  });
  assert.equal(debugCalls, 0);
  assert.equal(result.models[0].priority.value, 1);
  assert.equal(result.models[0].spawnAvailable.source, 'native-schema');
});

test('resolver falls back to codex debug models for incomplete session data', async () => {
  const resolver = new ModelCatalogResolver({
    session: new SessionModelCatalogProvider(),
    debug: new CodexDebugModelsProvider({
      exec: () => debugJson([{
        slug: 'gpt-sol',
        priority: 1,
        visibility: 'list',
        supported_reasoning_levels: [{ effort: 'low' }, { effort: 'high' }],
      }]),
    }),
    staticPolicy: new StaticPolicyProvider(),
  });
  const result = await resolver.getCatalog({ activeModel: 'gpt-sol', observedAt });
  const model = result.models.find((item) => item.slug === 'gpt-sol');
  assert.equal(model.priority.value, 1);
  assert.deepEqual(model.supportedEfforts.value, ['low', 'high']);
});

test('resolver uses stale cache when CLI fails, regardless of cache age', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'forge-catalog-'));
  const cachePath = join(dir, 'catalog.json');
  writeFileSync(cachePath, JSON.stringify(normalizedCatalog([{ slug: 'gpt-sol', priority: 1 }])));
  const resolver = new ModelCatalogResolver({
    session: new SessionModelCatalogProvider(),
    debug: { async getCatalog() { throw new Error('CLI unavailable'); } },
    cache: new CachedModelCatalogProvider(cachePath),
    staticPolicy: new StaticPolicyProvider(),
  });
  const result = await resolver.getCatalog({ activeModel: 'gpt-sol', observedAt: '2030-01-01T00:00:00.000Z' });
  const model = result.models.find((item) => item.slug === 'gpt-sol');
  assert.equal(model.priority.source, 'cache');
  assert.equal(model.priority.stale, true);
  assert.equal(result.degraded, true);
  assert.ok(result.trace.some((item) => item.provider === 'codex-debug-models' && item.status === 'error'));
});

test('debug parser normalizes current and partial catalog formats', () => {
  const current = parseCodexDebugModels(debugJson([{
    slug: 'gpt-sol',
    description: 'frontier',
    priority: 1,
    visibility: 'list',
    supported_reasoning_levels: [{ effort: 'medium' }, { effort: 'max' }],
  }]), observedAt);
  assert.deepEqual(current.models[0].supportedEfforts.value, ['medium', 'max']);
  assert.equal(current.models[0].priority.known, true);

  const partial = parseCodexDebugModels(JSON.stringify([{ model: 'manual-model' }]), observedAt);
  assert.equal(partial.models[0].priority.known, false);
  assert.equal(partial.models[0].visibility.known, false);
});

test('partial catalogs merge by field and preserve provenance conflicts', () => {
  const session = normalizedCatalog([{ slug: 'gpt-sol', priority: 1 }]);
  session.models[0].description = field(null, 'session', { known: false, observedAt });
  session.models[0].visibility = field('list', 'native-schema', { observedAt });
  const debug = normalizedCatalog([{ slug: 'gpt-sol', priority: 9 }]);
  debug.models[0].description = field('from debug', 'codex-debug-models', { observedAt });
  debug.models[0].priority.source = 'codex-debug-models';
  const merged = mergeCatalogs([session, debug]);
  assert.equal(merged.models[0].description.value, 'from debug');
  assert.equal(merged.models[0].description.source, 'codex-debug-models');
  assert.equal(merged.models[0].priority.value, 1);
  assert.ok(merged.trace.some((item) => item.status === 'conflict' && item.field === 'priority'));
});

test('unknown priority requires explicit override with a reason', () => {
  const catalog = normalizedCatalog([
    { slug: 'gpt-sol', priority: null },
    { slug: 'gpt-builder', priority: 2 },
  ]);
  const selection = {
    orchestrator: { model: 'gpt-sol', effort: 'high' },
    reviewer: { model: 'gpt-sol', effort: 'high' },
    builder: { model: 'gpt-builder', effort: 'medium' },
  };
  assert.throws(() => validateModelSelection(selection, catalog), /priority is unknown/);
  assert.equal(
    validateModelSelection(selection, catalog, { userOverride: true, userNote: 'user accepts unknown ordering' }).valid,
    true,
  );
});

test('stale or non-native spawn availability requires explicit confirmation', () => {
  const catalog = normalizedCatalog([
    { slug: 'gpt-sol', priority: 1 },
    { slug: 'gpt-builder', priority: 2 },
  ]);
  for (const model of catalog.models) {
    model.spawnAvailable = field(true, 'cache', { observedAt, stale: true });
  }
  const selection = {
    orchestrator: { model: 'gpt-sol', effort: 'high' },
    reviewer: { model: 'gpt-sol', effort: 'high' },
    builder: { model: 'gpt-builder', effort: 'medium' },
  };
  assert.throws(() => validateModelSelection(selection, catalog), /spawn availability is unconfirmed/);
  const result = validateModelSelection(selection, catalog, {
    userOverride: true,
    userNote: 'confirmed from the current native spawn tool schema',
  });
  assert.equal(result.nativeSchemaOverride, true);
});

test('reviewer strength and same-model effort rules are enforced', () => {
  const catalog = normalizedCatalog([
    { slug: 'gpt-sol', priority: 1 },
    { slug: 'gpt-terra', priority: 2 },
  ]);
  assert.throws(() => validateModelSelection({
    orchestrator: { model: 'gpt-sol', effort: 'high' },
    reviewer: { model: 'gpt-terra', effort: 'high' },
    builder: { model: 'gpt-terra', effort: 'medium' },
  }, catalog), /at least as strong/);
  assert.throws(() => validateModelSelection({
    orchestrator: { model: 'gpt-sol', effort: 'xhigh' },
    reviewer: { model: 'gpt-sol', effort: 'high' },
    builder: { model: 'gpt-terra', effort: 'medium' },
  }, catalog), /must not be lower/);
});

test('unobservable current effort uses the highest same-model reviewer effort without asking', () => {
  const catalog = normalizedCatalog([
    { slug: 'gpt-sol', priority: 1 },
    { slug: 'gpt-builder', priority: 2 },
  ]);
  const base = {
    orchestrator: { model: 'gpt-sol', effort: null, usesCurrentSession: true },
    builder: { model: 'gpt-builder', effort: 'medium' },
  };
  assert.throws(() => validateModelSelection({
    ...base,
    reviewer: { model: 'gpt-sol', effort: 'high' },
  }, catalog), /must use max/);
  const result = validateModelSelection({
    ...base,
    reviewer: { model: 'gpt-sol', effort: 'max' },
  }, catalog);
  assert.equal(result.valid, true);
  assert.equal(result.orchestratorEffortKnown, false);
});

test('ultra is prohibited even when a provider advertises it', () => {
  const catalog = normalizedCatalog([
    { slug: 'gpt-sol', priority: 1, efforts: ['high', 'ultra'] },
    { slug: 'gpt-builder', priority: 2 },
  ]);
  assert.throws(() => validateModelSelection({
    orchestrator: { model: 'gpt-sol', effort: 'high' },
    reviewer: { model: 'gpt-sol', effort: 'ultra' },
    builder: { model: 'gpt-builder', effort: 'medium' },
  }, catalog), /ultra is prohibited/);
});

test('degraded static mode exposes unknown fields instead of inventing them', async () => {
  const resolver = new ModelCatalogResolver({
    session: { async getCatalog() { throw new Error('no session'); } },
    debug: { async getCatalog() { throw new Error('no CLI'); } },
    cache: { async getCatalog() { throw new Error('no cache'); } },
    staticPolicy: new StaticPolicyProvider(),
  });
  const result = await resolver.getCatalog({ activeModel: 'manual-model', manualModels: ['builder-model'], observedAt });
  assert.equal(result.degraded, true);
  const model = result.models.find((item) => item.slug === 'manual-model');
  assert.equal(model.priority.known, false);
  assert.equal(model.visibility.known, false);
  assert.equal(model.supportedEfforts.known, true);
});

test('incompatible codex debug output fails closed and cache remains readable', async () => {
  assert.throws(() => parseCodexDebugModels('{"unexpected":true}', observedAt), /no models array/);
  const dir = mkdtempSync(join(tmpdir(), 'forge-catalog-'));
  const cachePath = join(dir, 'catalog.json');
  writeFileSync(cachePath, JSON.stringify(normalizedCatalog([{ slug: 'gpt-sol', priority: 1 }])));
  const provider = new CachedModelCatalogProvider(cachePath);
  const cached = await provider.getCatalog();
  assert.equal(cached.models[0].priority.value, 1);
  assert.equal(JSON.parse(readFileSync(cachePath, 'utf8')).models[0].priority.source, 'test');
});
