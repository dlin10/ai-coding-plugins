import { existsSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join, resolve } from 'node:path';
import process from 'node:process';

function observedAt(context = {}) {
  return context.observedAt ?? new Date().toISOString();
}

export function field(value, source, options = {}) {
  const known = options.known ?? (value !== undefined && value !== null);
  return {
    value: known ? value : null,
    known,
    source,
    observedAt: options.observedAt ?? new Date().toISOString(),
    stale: false,
  };
}

function catalogError(index, message) {
  throw new Error(`codex debug models model at index ${index} ${message}`);
}

function requiredString(row, name, index, options = {}) {
  const value = row[name];
  if (typeof value !== 'string' || (!options.allowEmpty && !value.trim())) {
    catalogError(index, `has invalid ${name}`);
  }
  return value;
}

function parseVisibleModel(row, index, timestamp) {
  const source = 'codex-debug-models';
  const slug = requiredString(row, 'slug', index).trim();
  let displayName = row.display_name;
  if (displayName === undefined || displayName === null ||
      (typeof displayName === 'string' && !displayName.trim())) {
    displayName = slug;
  } else if (typeof displayName !== 'string') {
    catalogError(index, 'has invalid display_name');
  }

  const description = requiredString(row, 'description', index, { allowEmpty: true });
  const defaultEffort = requiredString(row, 'default_reasoning_level', index).trim().toLowerCase();
  if (!Array.isArray(row.supported_reasoning_levels) || row.supported_reasoning_levels.length === 0) {
    catalogError(index, 'has invalid supported_reasoning_levels');
  }

  const seenEfforts = new Set();
  const supportedReasoningLevels = [];
  for (let effortIndex = 0; effortIndex < row.supported_reasoning_levels.length; effortIndex++) {
    const level = row.supported_reasoning_levels[effortIndex];
    if (!level || typeof level !== 'object' || Array.isArray(level)) {
      catalogError(index, `has invalid supported_reasoning_levels[${effortIndex}]`);
    }
    const effort = requiredString(level, 'effort', index).trim().toLowerCase();
    if (seenEfforts.has(effort)) {
      catalogError(index, `advertises duplicate reasoning effort ${effort}`);
    }
    seenEfforts.add(effort);
    const effortDescription = requiredString(level, 'description', index, { allowEmpty: true });
    if (effort !== 'ultra') {
      supportedReasoningLevels.push({ effort, description: effortDescription });
    }
  }
  if (supportedReasoningLevels.length === 0) {
    catalogError(index, 'has no supported non-ultra reasoning efforts');
  }
  if (defaultEffort === 'ultra' || !supportedReasoningLevels.some((level) => level.effort === defaultEffort)) {
    catalogError(index, 'has a default_reasoning_level that is not a supported non-ultra effort');
  }
  if (typeof row.priority !== 'number' || !Number.isFinite(row.priority)) {
    catalogError(index, 'has invalid priority');
  }
  const pickerReasoningLevels = [
    supportedReasoningLevels.find((level) => level.effort === defaultEffort),
    ...supportedReasoningLevels.filter((level) => level.effort !== defaultEffort),
  ];

  return {
    slug,
    displayName: field(displayName, source, { observedAt: timestamp }),
    description: field(description, source, { observedAt: timestamp }),
    defaultEffort: field(defaultEffort, source, { observedAt: timestamp }),
    supportedEfforts: field(
      supportedReasoningLevels.map((level) => level.effort),
      source,
      { observedAt: timestamp },
    ),
    supportedReasoningLevels: field(supportedReasoningLevels, source, { observedAt: timestamp }),
    pickerReasoningLevels: field(pickerReasoningLevels, source, { observedAt: timestamp }),
    priority: field(row.priority, source, { observedAt: timestamp }),
    visibility: field('list', source, { observedAt: timestamp }),
    sourceIndex: index,
  };
}

export function parseCodexDebugModels(text, timestamp = new Date().toISOString()) {
  let body;
  try {
    body = JSON.parse(text);
  } catch (error) {
    throw new Error(`codex debug models returned invalid JSON: ${error.message}`);
  }
  if (!body || typeof body !== 'object' || Array.isArray(body) || !Array.isArray(body.models)) {
    throw new Error('codex debug models output has no models array');
  }

  const visible = [];
  const visibleSlugs = new Set();
  for (let index = 0; index < body.models.length; index++) {
    const row = body.models[index];
    if (!row || typeof row !== 'object' || Array.isArray(row)) {
      catalogError(index, 'is not an object');
    }
    if (typeof row.visibility !== 'string') {
      catalogError(index, 'has invalid visibility');
    }
    if (row.visibility !== 'list') continue;
    const model = parseVisibleModel(row, index, timestamp);
    if (visibleSlugs.has(model.slug)) {
      catalogError(index, `duplicates visible slug ${model.slug}`);
    }
    visibleSlugs.add(model.slug);
    visible.push(model);
  }
  if (visible.length === 0) {
    throw new Error('codex debug models output contains no visible usable models');
  }

  visible.sort((left, right) => left.priority.value - right.priority.value || left.sourceIndex - right.sourceIndex);
  for (const model of visible) delete model.sourceIndex;
  const source = 'codex-debug-models';
  return {
    version: 1,
    generatedAt: timestamp,
    models: visible,
    degraded: false,
    trace: [{ provider: source, status: 'ok', modelCount: visible.length, observedAt: timestamp }],
  };
}

export class CodexDebugModelsProvider {
  constructor(options = {}) {
    this.exec = options.exec ?? ((command, args) => runCodexCli(args, { command }));
  }

  async getCatalog(context = {}) {
    const timestamp = observedAt(context);
    const output = this.exec(context.codexPath ?? 'codex', ['debug', 'models']);
    return parseCodexDebugModels(output, timestamp);
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

export class ModelCatalogResolver {
  constructor(options = {}) {
    this.debug = options.debug ?? new CodexDebugModelsProvider();
  }

  async getCatalog(context = {}) {
    return this.debug.getCatalog(context);
  }
}

function modelBySlug(catalog, slug) {
  return (catalog.models ?? []).find((model) => model.slug === slug);
}

function fieldValue(value) {
  return value && typeof value === 'object' && 'known' in value
    ? value.known ? value.value : null
    : value;
}

function validateRoleSelection(role, selected, catalog) {
  if (!selected) return null;
  if (typeof selected.model !== 'string' || !selected.model.trim()) {
    throw new Error(`${role} model is required`);
  }
  if (typeof selected.effort !== 'string' || !selected.effort.trim()) {
    throw new Error(`${role} effort is required`);
  }
  const modelSlug = selected.model.trim();
  const effort = selected.effort.trim().toLowerCase();
  if (effort === 'ultra') {
    throw new Error(`${role} effort ultra is prohibited`);
  }
  const model = modelBySlug(catalog, modelSlug);
  if (!model || fieldValue(model.visibility) !== 'list') {
    throw new Error(`${role} model is absent from the visible CLI catalog: ${modelSlug}`);
  }
  const supported = fieldValue(model.supportedEfforts);
  if (!Array.isArray(supported) || !supported.includes(effort)) {
    throw new Error(`${role} model ${modelSlug} does not advertise effort ${effort}`);
  }
  return { model: modelSlug, effort };
}

/** Every visible reviewer model/effort pair, in CLI priority and picker order. */
export function permittedReviewers(catalog) {
  const options = [];
  for (const model of catalog.models ?? []) {
    if (fieldValue(model.visibility) !== 'list') continue;
    const levels = fieldValue(model.pickerReasoningLevels) ?? [];
    for (const { effort, description } of levels) {
      options.push({
        model: model.slug,
        effort,
        effortDescription: description,
        priority: fieldValue(model.priority),
      });
    }
  }
  return options;
}

export function validateModelSelection(selection, catalog) {
  if (!selection || (!selection.reviewer && !selection.builder)) {
    throw new Error('at least one reviewer or builder selection is required');
  }
  const reviewer = validateRoleSelection('reviewer', selection.reviewer, catalog);
  const builder = validateRoleSelection('builder', selection.builder, catalog);
  return {
    valid: true,
    reviewer,
    builder,
    catalogGeneratedAt: catalog.generatedAt ?? null,
  };
}
