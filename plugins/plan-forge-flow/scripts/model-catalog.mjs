import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { dirname, join, resolve } from 'node:path';
import process from 'node:process';

export const EFFORT_ORDER = ['low', 'medium', 'high', 'xhigh', 'max'];

function now(context = {}) {
  return context.observedAt ?? new Date().toISOString();
}

export function field(value, source, options = {}) {
  const known = options.known ?? (value !== undefined && value !== null);
  return {
    value: known ? value : null,
    known,
    source,
    observedAt: options.observedAt ?? new Date().toISOString(),
    stale: Boolean(options.stale),
  };
}

function unknown(source, observedAt, stale = false) {
  return field(null, source, { known: false, observedAt, stale });
}

function normalizeEfforts(value) {
  if (!Array.isArray(value)) return null;
  return [...new Set(value.map((item) =>
    String(typeof item === 'object' && item !== null ? item.effort : item).toLowerCase()))];
}

function normalizeModel(raw, source, observedAt, stale = false) {
  const supported = raw.supportedEfforts ?? raw.supported_reasoning_levels;
  const visibility = raw.visibility ?? (raw.hidden === true ? 'hidden' : null);
  const priority = Number.isFinite(raw.priority) ? raw.priority : null;
  const spawnAvailable =
    typeof raw.spawnAvailable === 'boolean'
      ? raw.spawnAvailable
      : typeof raw.spawn_available === 'boolean'
        ? raw.spawn_available
        : null;
  return {
    slug: String(raw.slug ?? raw.model ?? ''),
    description: raw.description
      ? field(String(raw.description), source, { observedAt, stale })
      : unknown(source, observedAt, stale),
    supportedEfforts: Array.isArray(supported)
      ? field(normalizeEfforts(supported), source, { observedAt, stale })
      : unknown(source, observedAt, stale),
    priority: priority === null
      ? unknown(source, observedAt, stale)
      : field(priority, source, { observedAt, stale }),
    visibility: visibility
      ? field(String(visibility), source, { observedAt, stale })
      : unknown(source, observedAt, stale),
    spawnAvailable: spawnAvailable === null
      ? unknown(source, observedAt, stale)
      : field(spawnAvailable, source, { observedAt, stale }),
  };
}

function makeCatalog(source, models, context = {}, options = {}) {
  const observedAt = options.observedAt ?? now(context);
  return {
    version: 1,
    generatedAt: observedAt,
    models: models.filter((model) => model.slug),
    activeModel: context.activeModel
      ? field(context.activeModel, context.activeModelSource ?? source, { observedAt, stale: options.stale })
      : unknown(source, observedAt, options.stale),
    activeEffort: context.activeEffort
      ? field(context.activeEffort, context.activeEffortSource ?? source, { observedAt, stale: options.stale })
      : unknown(source, observedAt, options.stale),
    degraded: Boolean(options.degraded),
    trace: options.trace ?? [],
  };
}

/**
 * @typedef {{getCatalog(context: object): Promise<object>}} ModelCatalogProvider
 */
export class SessionModelCatalogProvider {
  async getCatalog(context = {}) {
    const observedAt = now(context);
    const bySlug = new Map();
    if (context.activeModel) {
      bySlug.set(
        context.activeModel,
        normalizeModel({ slug: context.activeModel }, context.activeModelSource ?? 'session', observedAt),
      );
    }
    for (const raw of context.nativeModels ?? []) {
      const model = normalizeModel(raw, 'native-schema', observedAt);
      model.spawnAvailable = field(true, 'native-schema', { observedAt });
      bySlug.set(model.slug, model);
    }
    return makeCatalog('session', [...bySlug.values()], context, {
      observedAt,
      trace: [{ provider: 'session', status: 'ok', modelCount: bySlug.size, observedAt }],
    });
  }
}

export function parseCodexDebugModels(text, observedAt = new Date().toISOString()) {
  let body;
  try {
    body = JSON.parse(text);
  } catch (error) {
    throw new Error(`codex debug models returned invalid JSON: ${error.message}`);
  }
  const rows = Array.isArray(body) ? body : body?.models;
  if (!Array.isArray(rows)) {
    throw new Error('codex debug models output has no models array');
  }
  const models = rows
    .filter((row) => row && typeof row === 'object' && (row.slug || row.model))
    .map((row) => normalizeModel(row, 'codex-debug-models', observedAt));
  if (models.length === 0 && rows.length !== 0) {
    throw new Error('codex debug models output contains no recognizable models');
  }
  return makeCatalog('codex-debug-models', models, {}, {
    observedAt,
    trace: [{ provider: 'codex-debug-models', status: 'ok', modelCount: models.length, observedAt }],
  });
}

export class CodexDebugModelsProvider {
  constructor(options = {}) {
    this.exec = options.exec ?? ((command, args) => runCodexCli(args, { command }));
  }

  async getCatalog(context = {}) {
    const observedAt = now(context);
    const output = this.exec(context.codexPath ?? 'codex', ['debug', 'models']);
    return parseCodexDebugModels(output, observedAt);
  }
}

export function runCodexCli(args, options = {}) {
  if (typeof options.command === 'string' && options.command !== 'codex') {
    return execFileSync(options.command, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  }
  const explicit = process.env.FORGE_CODEX_PATH;
  if (explicit) {
    const path = resolve(explicit);
    return path.endsWith('.js')
      ? execFileSync(process.execPath, [path, ...args], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] })
      : execFileSync(path, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  }
  if (process.platform === 'win32' && process.env.APPDATA) {
    const npmEntrypoint = join(process.env.APPDATA, 'npm', 'node_modules', '@openai', 'codex', 'bin', 'codex.js');
    if (existsSync(npmEntrypoint)) {
      return execFileSync(process.execPath, [npmEntrypoint, ...args], {
        encoding: 'utf8',
        stdio: ['ignore', 'pipe', 'pipe'],
      });
    }
  }
  return execFileSync('codex', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
}

function markFieldStale(input, source, observedAt) {
  if (!input || typeof input !== 'object' || !('known' in input)) {
    return unknown(source, observedAt, true);
  }
  return {
    value: input.known ? input.value : null,
    known: Boolean(input.known),
    source,
    observedAt: input.observedAt ?? observedAt,
    stale: true,
  };
}

export class CachedModelCatalogProvider {
  constructor(cachePath) {
    this.cachePath = cachePath;
  }

  async getCatalog() {
    if (!this.cachePath || !existsSync(this.cachePath)) throw new Error('model catalog cache is unavailable');
    const raw = JSON.parse(readFileSync(this.cachePath, 'utf8'));
    const observedAt = raw.generatedAt ?? new Date().toISOString();
    const models = (raw.models ?? []).map((model) => ({
      slug: model.slug,
      description: markFieldStale(model.description, 'cache', observedAt),
      supportedEfforts: markFieldStale(model.supportedEfforts, 'cache', observedAt),
      priority: markFieldStale(model.priority, 'cache', observedAt),
      visibility: markFieldStale(model.visibility, 'cache', observedAt),
      spawnAvailable: markFieldStale(model.spawnAvailable, 'cache', observedAt),
    }));
    return {
      version: 1,
      generatedAt: observedAt,
      models,
      activeModel: markFieldStale(raw.activeModel, 'cache', observedAt),
      activeEffort: markFieldStale(raw.activeEffort, 'cache', observedAt),
      degraded: true,
      trace: [{ provider: 'cache', status: 'ok', modelCount: models.length, observedAt, stale: true }],
    };
  }
}

export class StaticPolicyProvider {
  async getCatalog(context = {}) {
    const observedAt = now(context);
    const slugs = [...new Set([context.activeModel, ...(context.manualModels ?? [])].filter(Boolean))];
    const models = slugs.map((slug) => {
      const model = normalizeModel({ slug }, 'static-policy', observedAt);
      model.supportedEfforts = field(EFFORT_ORDER, 'static-policy', { observedAt });
      return model;
    });
    return makeCatalog('static-policy', models, context, {
      observedAt,
      degraded: true,
      trace: [{ provider: 'static-policy', status: 'ok', modelCount: models.length, observedAt }],
    });
  }
}

function chooseField(existing, candidate, trace, slug, name) {
  if (!candidate) return existing;
  if (!existing || !existing.known) return candidate;
  if (!candidate.known) return existing;
  if (JSON.stringify(existing.value) !== JSON.stringify(candidate.value)) {
    trace.push({
      provider: candidate.source,
      status: 'conflict',
      model: slug,
      field: name,
      kept: existing.value,
      keptSource: existing.source,
      ignored: candidate.value,
    });
  }
  return existing;
}

export function mergeCatalogs(catalogs, extraTrace = []) {
  const trace = [...extraTrace];
  const models = new Map();
  let activeModel;
  let activeEffort;
  let generatedAt = new Date().toISOString();
  for (const catalog of catalogs) {
    if (!catalog) continue;
    generatedAt = catalog.generatedAt ?? generatedAt;
    trace.push(...(catalog.trace ?? []));
    activeModel = chooseField(activeModel, catalog.activeModel, trace, '<session>', 'activeModel');
    activeEffort = chooseField(activeEffort, catalog.activeEffort, trace, '<session>', 'activeEffort');
    for (const candidate of catalog.models ?? []) {
      const current = models.get(candidate.slug);
      if (!current) {
        models.set(candidate.slug, structuredClone(candidate));
        continue;
      }
      for (const name of ['description', 'supportedEfforts', 'priority', 'visibility', 'spawnAvailable']) {
        current[name] = chooseField(current[name], candidate[name], trace, candidate.slug, name);
      }
    }
  }
  const result = {
    version: 1,
    generatedAt,
    models: [...models.values()],
    activeModel: activeModel ?? unknown('resolver', generatedAt),
    activeEffort: activeEffort ?? unknown('resolver', generatedAt),
    degraded: false,
    trace,
  };
  result.degraded = result.models.length === 0 ||
    result.activeModel.stale ||
    result.activeEffort.stale ||
    result.models.some((model) =>
      !model.priority.known ||
      !model.supportedEfforts.known ||
      ['description', 'supportedEfforts', 'priority', 'visibility', 'spawnAvailable']
        .some((name) => model[name]?.stale));
  return result;
}

function writeJsonAtomic(path, value) {
  mkdirSync(dirname(path), { recursive: true });
  const temp = `${path}.${process.pid}.tmp`;
  writeFileSync(temp, JSON.stringify(value, null, 2) + '\n');
  renameSync(temp, path);
}

export class ModelCatalogResolver {
  constructor(options = {}) {
    this.session = options.session ?? new SessionModelCatalogProvider();
    this.debug = options.debug ?? new CodexDebugModelsProvider();
    this.cache = options.cache ?? null;
    this.staticPolicy = options.staticPolicy ?? new StaticPolicyProvider();
    this.cachePath = options.cachePath ?? null;
  }

  async getCatalog(context = {}) {
    const catalogs = [];
    const trace = [];
    let sessionCatalog = null;
    try {
      sessionCatalog = await this.session.getCatalog(context);
      catalogs.push(sessionCatalog);
    } catch (error) {
      trace.push({ provider: 'session', status: 'error', error: error.message });
    }

    const sessionComplete = Boolean(sessionCatalog?.activeModel?.known) &&
      sessionCatalog.models.length > 0 &&
      sessionCatalog.models.every((model) =>
        model.priority.known && model.supportedEfforts.known && model.spawnAvailable.known);
    let debugSucceeded = false;
    if (sessionComplete) {
      trace.push({ provider: 'codex-debug-models', status: 'skipped', reason: 'session/native catalog is complete' });
    } else {
      try {
        catalogs.push(await this.debug.getCatalog(context));
        debugSucceeded = true;
      } catch (error) {
        trace.push({ provider: 'codex-debug-models', status: 'error', error: error.message });
        if (this.cache) {
          try {
            catalogs.push(await this.cache.getCatalog(context));
          } catch (cacheError) {
            trace.push({ provider: 'cache', status: 'error', error: cacheError.message });
          }
        }
      }
    }

    try {
      catalogs.push(await this.staticPolicy.getCatalog(context));
    } catch (error) {
      trace.push({ provider: 'static-policy', status: 'error', error: error.message });
    }
    const result = mergeCatalogs(catalogs, trace);
    if ((debugSucceeded || sessionComplete) && this.cachePath) writeJsonAtomic(this.cachePath, result);
    return result;
  }
}

function modelBySlug(catalog, slug) {
  return (catalog.models ?? []).find((model) => model.slug === slug);
}

function validateEffort(model, effort, role) {
  if (!EFFORT_ORDER.includes(effort)) {
    throw new Error(`${role} effort must be one of ${EFFORT_ORDER.join(', ')}; ultra is prohibited`);
  }
  if (model?.supportedEfforts?.known && !model.supportedEfforts.value.includes(effort)) {
    throw new Error(`${role} model ${model.slug} does not advertise effort ${effort}`);
  }
}

export function validateModelSelection(selection, catalog, options = {}) {
  const orchestrator = modelBySlug(catalog, selection.orchestrator.model);
  const reviewer = modelBySlug(catalog, selection.reviewer.model);
  const builder = selection.builder ? modelBySlug(catalog, selection.builder.model) : null;
  if (!orchestrator) throw new Error(`orchestrator model is absent from the catalog: ${selection.orchestrator.model}`);
  if (!reviewer) throw new Error(`reviewer model is absent from the catalog: ${selection.reviewer.model}`);
  if (selection.builder && !builder) {
    throw new Error(`builder model is absent from the catalog: ${selection.builder.model}`);
  }
  const orchestratorEffortKnown = typeof selection.orchestrator.effort === 'string';
  if (orchestratorEffortKnown) {
    validateEffort(orchestrator, selection.orchestrator.effort, 'orchestrator');
  }
  validateEffort(reviewer, selection.reviewer.effort, 'reviewer');
  if (builder) validateEffort(builder, selection.builder.effort, 'builder');

  const spawnedRoles = [['reviewer', reviewer]];
  if (builder) spawnedRoles.push(['builder', builder]);
  for (const [role, model] of spawnedRoles) {
    if (model.spawnAvailable.known && model.spawnAvailable.value === false) {
      throw new Error(`${role} model ${model.slug} is not available in the native spawn schema`);
    }
  }
  const unconfirmedSpawn = spawnedRoles.filter(([, model]) =>
    !model.spawnAvailable.known ||
    model.spawnAvailable.stale ||
    model.spawnAvailable.source !== 'native-schema');
  if (unconfirmedSpawn.length && !(options.userOverride && options.userNote)) {
    throw new Error(
      `native spawn availability is unconfirmed for ${unconfirmedSpawn.map(([role]) => role).join(', ')}; ` +
      'explicit --user-override with --user-note is required',
    );
  }

  const unknownPriority = !orchestrator.priority.known || !reviewer.priority.known;
  if (unknownPriority && !(options.userOverride && options.userNote)) {
    throw new Error('reviewer/orchestrator priority is unknown; explicit --user-override with --user-note is required');
  }
  if (!unknownPriority && reviewer.priority.value > orchestrator.priority.value) {
    throw new Error('reviewer model must be at least as strong as the orchestrator (lower priority is stronger)');
  }
  if (selection.reviewer.model === selection.orchestrator.model) {
    const reviewerEffort = EFFORT_ORDER.indexOf(selection.reviewer.effort);
    if (orchestratorEffortKnown) {
      const orchestratorEffort = EFFORT_ORDER.indexOf(selection.orchestrator.effort);
      if (reviewerEffort < orchestratorEffort) {
        throw new Error('reviewer effort must not be lower than orchestrator effort for the same model');
      }
    } else {
      const advertised = orchestrator.supportedEfforts.known
        ? orchestrator.supportedEfforts.value.filter((effort) => EFFORT_ORDER.includes(effort))
        : EFFORT_ORDER;
      const conservativeEffort = [...advertised]
        .sort((left, right) => EFFORT_ORDER.indexOf(left) - EFFORT_ORDER.indexOf(right))
        .at(-1);
      if (!conservativeEffort || reviewerEffort < EFFORT_ORDER.indexOf(conservativeEffort)) {
        throw new Error(
          `current orchestrator effort is not observable; same-model reviewer must use ${conservativeEffort ?? 'the highest allowed effort'}`,
        );
      }
    }
  }
  return {
    valid: true,
    orchestratorEffortKnown,
    orchestratorUsesCurrentSession: Boolean(selection.orchestrator.usesCurrentSession),
    unknownPriorityOverride: unknownPriority,
    nativeSchemaOverride: unconfirmedSpawn.length > 0,
    provenance: {
      orchestratorPriority: orchestrator.priority,
      reviewerPriority: reviewer.priority,
      reviewerEfforts: reviewer.supportedEfforts,
      builderEfforts: builder?.supportedEfforts ?? null,
    },
  };
}
