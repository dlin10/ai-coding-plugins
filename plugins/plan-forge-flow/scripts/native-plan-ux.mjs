import {
  buildApprovalWrapper,
  canonicalHumanPlan,
  extractApprovalWrapper,
  issuanceItemId,
  newNonce,
  sha256,
  validateEnvelope,
} from './resume-envelope.mjs';
import { validateModelSelection } from './model-catalog.mjs';

const PICKER_PAGE_SIZE = 2;
const OWNED_MARKER = '<!-- plan-forge-flow:owned -->\n';
const MAX_HUMAN_PLAN_BYTES = 256 * 1024;

function fail(message) {
  throw new Error(`invalid native Plan UX state: ${message}`);
}

function fieldValue(value) {
  return value && typeof value === 'object' && 'known' in value
    ? value.known ? value.value : null
    : value;
}

function assertWellFormedUnicode(value) {
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(index + 1);
      if (next < 0xdc00 || next > 0xdfff) fail('human plan contains an unpaired surrogate');
      index++;
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      fail('human plan contains an unpaired surrogate');
    }
  }
}

export function normalizeHumanPlan(value) {
  if (typeof value !== 'string') fail('human plan must be a string');
  assertWellFormedUnicode(value);
  let normalized = value.replace(/^\uFEFF/, '').replace(/\r\n?/g, '\n');
  normalized = normalized.replace(/\n+$/g, '') + '\n';
  if (!normalized.startsWith(OWNED_MARKER)) fail('human plan lacks the Forge ownership marker');
  if (normalized.includes('<proposed_plan>') || normalized.includes('</proposed_plan>')) {
    fail('human plan must not contain native proposed-plan delimiters');
  }
  if (Buffer.byteLength(normalized, 'utf8') > MAX_HUMAN_PLAN_BYTES) {
    fail('human plan exceeds the size bound');
  }
  return canonicalHumanPlan(normalized);
}

export function humanPlanHash(value) {
  return sha256(normalizeHumanPlan(value));
}

function modelRows(catalog) {
  if (!catalog || !Array.isArray(catalog.models)) fail('model catalog is malformed');
  return catalog.models
    .map((model, index) => ({
      model,
      index,
      priority: fieldValue(model.priority),
      visibility: fieldValue(model.visibility),
    }))
    .filter(({ visibility }) => visibility === 'list')
    .sort((left, right) => {
      if (!Number.isFinite(left.priority) || !Number.isFinite(right.priority)) {
        fail('visible model priority is unavailable');
      }
      return left.priority - right.priority || left.index - right.index;
    })
    .map(({ model }) => model);
}

function page(items, cursor, kind) {
  if (!Number.isInteger(cursor) || cursor < 0 || cursor >= items.length) {
    fail(`${kind} picker cursor is invalid`);
  }
  const entries = items.slice(cursor, cursor + PICKER_PAGE_SIZE);
  const nextCursor = cursor + entries.length < items.length ? cursor + entries.length : null;
  return { entries, nextCursor };
}

function automaticRemainder(choices, cursor, kind) {
  if (!Number.isInteger(cursor) || cursor < 0 || cursor >= choices.length) {
    fail(`${kind} picker cursor is invalid`);
  }
  if (choices.length - cursor !== 1) return null;
  return {
    kind: 'automatic',
    selection: choices[cursor].value,
    request: null,
    nextCursor: null,
  };
}

function request(header, id, question, entries, nextCursor, description) {
  const options = entries.map(({ label, optionDescription }) => ({
    label,
    description: optionDescription,
  }));
  if (nextCursor !== null) {
    options.push({ label: 'More…', description });
  }
  return {
    questions: [{ header, id, question, options }],
  };
}

export function modelPicker(catalog, { role, cursor = 0 } = {}) {
  if (role !== 'reviewer' && role !== 'builder') fail('model picker role is invalid');
  const models = modelRows(catalog);
  if (models.length === 0) fail('model catalog has no visible choices');
  const choices = models.map((model) => {
    const displayName = fieldValue(model.displayName);
    const description = fieldValue(model.description);
    if (typeof displayName !== 'string' || !displayName.trim() ||
        typeof description !== 'string') {
      fail(`model ${model.slug ?? '<unknown>'} lacks CLI picker metadata`);
    }
    return {
      kind: 'model',
      value: model.slug,
      label: displayName,
      optionDescription: `${model.slug} — ${description}`,
    };
  });
  const automatic = automaticRemainder(choices, cursor, 'model');
  if (automatic) return automatic;
  const selectedPage = page(choices, cursor, 'model');
  return {
    kind: 'question',
    choices: selectedPage.entries.map(({ kind, value, label }) => ({ kind, value, label })),
    request: request(
      role === 'reviewer' ? 'Reviewer' : 'Builder',
      `${role}_model`,
      `Which ${role} model should Forge use?`,
      selectedPage.entries,
      selectedPage.nextCursor,
      'Show the next available models.',
    ),
    nextCursor: selectedPage.nextCursor,
  };
}

function effortChoices(model) {
  const defaultEffort = fieldValue(model.defaultEffort);
  const levels = fieldValue(model.supportedReasoningLevels);
  if (typeof defaultEffort !== 'string' || !Array.isArray(levels)) {
    fail(`model ${model.slug} lacks CLI effort metadata`);
  }
  const usable = levels.filter((level) => level?.effort !== 'ultra');
  if (usable.length === 0) fail(`model ${model.slug} has no non-ultra effort`);
  if (!usable.some((level) => level.effort === defaultEffort)) {
    fail(`model ${model.slug} default effort is unavailable`);
  }
  return [
    usable.find((level) => level.effort === defaultEffort),
    ...usable.filter((level) => level.effort !== defaultEffort),
  ].map((level) => {
    if (typeof level.effort !== 'string' || typeof level.description !== 'string') {
      fail(`model ${model.slug} has malformed effort metadata`);
    }
    return {
      kind: 'effort',
      value: level.effort,
      label: level.effort,
      optionDescription: level.description,
    };
  });
}

export function effortPicker(catalog, { role, model: slug, cursor = 0 } = {}) {
  if (role !== 'reviewer' && role !== 'builder') fail('effort picker role is invalid');
  const model = modelRows(catalog).find((candidate) => candidate.slug === slug);
  if (!model) fail(`selected model is absent from the visible catalog: ${slug}`);
  const choices = effortChoices(model);
  const automatic = automaticRemainder(choices, cursor, 'effort');
  if (automatic) return automatic;
  const selectedPage = page(choices, cursor, 'effort');
  return {
    kind: 'question',
    choices: selectedPage.entries.map(({ kind, value, label }) => ({ kind, value, label })),
    request: request(
      'Effort',
      `${role}_effort`,
      `How much reasoning effort should the ${role} use?`,
      selectedPage.entries,
      selectedPage.nextCursor,
      'Show the remaining effort levels.',
    ),
    nextCursor: selectedPage.nextCursor,
  };
}

export function createPlanUxState(humanPlan, planRevision = 1) {
  if (!Number.isInteger(planRevision) || planRevision < 1) fail('plan revision is invalid');
  const canonical = normalizeHumanPlan(humanPlan);
  return {
    version: 1,
    planRevision,
    humanPlan: canonical,
    humanPlanHash: sha256(canonical),
    previewedHash: null,
    builder: null,
    builderPlanHash: null,
    approvalEnvelope: null,
    approvalWrapper: null,
  };
}

export function recordFullPlanPreview(state, displayedMarkdown) {
  const displayed = canonicalHumanPlan(displayedMarkdown);
  if (displayed !== state.humanPlan) fail('preview must contain the complete canonical human plan');
  return { ...structuredClone(state), previewedHash: state.humanPlanHash };
}

export function recordBuilderSelection(state, builder, catalog) {
  if (state.previewedHash !== state.humanPlanHash) {
    fail('the full current plan must be previewed before builder selection');
  }
  const validation = validateModelSelection({ builder }, catalog);
  return {
    ...structuredClone(state),
    builder: validation.builder,
    builderPlanHash: state.humanPlanHash,
    approvalEnvelope: null,
    approvalWrapper: null,
  };
}

function observedModel(model) {
  return {
    slug: model.slug,
    displayName: fieldValue(model.displayName),
    description: fieldValue(model.description),
    priority: fieldValue(model.priority),
    defaultEffort: fieldValue(model.defaultEffort),
    reasoningLevels: fieldValue(model.supportedReasoningLevels)
      .filter((level) => level.effort !== 'ultra')
      .map((level) => ({ effort: level.effort, description: level.description })),
  };
}

function selectedCatalogObservation(catalog, selections) {
  const selectedSlugs = [...new Set(selections.map((selection) => selection.model))];
  const models = modelRows(catalog)
    .filter((model) => selectedSlugs.includes(model.slug))
    .map(observedModel);
  if (models.length !== selectedSlugs.length) fail('selected catalog observation is incomplete');
  return { generatedAt: catalog.generatedAt, models };
}

export function issueApprovalEnvelope(state, {
  reviewer,
  catalog,
  reviewLog,
  completedReviewRounds,
  maxRounds,
  repository,
  origin,
  nonce = newNonce(),
}) {
  if (state.previewedHash !== state.humanPlanHash ||
      state.builderPlanHash !== state.humanPlanHash || !state.builder) {
    fail('approval requires a previewed plan and current builder selection');
  }
  validateModelSelection({ reviewer, builder: state.builder }, catalog);
  const fullOrigin = {
    ...origin,
    itemId: issuanceItemId(origin, nonce, state.humanPlanHash),
  };
  const envelope = validateEnvelope({
    version: 1,
    humanPlanHash: state.humanPlanHash,
    repository,
    origin: fullOrigin,
    planRevision: state.planRevision,
    nonce,
    completedReviewRounds,
    maxRounds,
    reviewer,
    builder: state.builder,
    builderPlanHash: state.builderPlanHash,
    catalogObservation: selectedCatalogObservation(catalog, [reviewer, state.builder]),
    reviewLog,
  });
  const wrapper = buildApprovalWrapper(state.humanPlan, envelope);
  return {
    state: { ...structuredClone(state), approvalEnvelope: envelope, approvalWrapper: wrapper },
    envelope,
    wrapper,
  };
}

export function finalProposedPlanOutput(state) {
  if (!state.approvalEnvelope || !state.approvalWrapper) {
    fail('approval envelope has not been issued');
  }
  const extracted = extractApprovalWrapper(state.approvalWrapper);
  if (extracted.humanPlan !== state.humanPlan ||
      extracted.envelope.humanPlanHash !== state.humanPlanHash ||
      extracted.envelope.planRevision !== state.planRevision ||
      state.builderPlanHash !== state.humanPlanHash ||
      JSON.stringify(extracted.envelope.builder) !== JSON.stringify(state.builder)) {
    fail('approval wrapper does not match the current human plan');
  }
  return `<proposed_plan>\n${state.approvalWrapper}</proposed_plan>`;
}

export function extractFinalProposedPlan(output) {
  if (typeof output !== 'string') fail('final proposed-plan output must be a string');
  const match = /^<proposed_plan>\n([\s\S]*)<\/proposed_plan>$/.exec(output);
  if (!match) fail('final output must contain only one proposed_plan block');
  return extractApprovalWrapper(match[1]);
}

export function revisePlanUxState(state, replacementPlan) {
  const replacement = normalizeHumanPlan(replacementPlan);
  if (replacement === state.humanPlan) fail('replacement plan is byte-identical to the current plan');
  return createPlanUxState(replacement, state.planRevision + 1);
}
