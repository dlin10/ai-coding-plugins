import { createHash, randomBytes } from 'node:crypto';
import { isAbsolute } from 'node:path';

export const ENVELOPE_VERSION = 1;
export const MAX_ENVELOPE_BYTES = 512 * 1024;
export const NORMAL_IMPLEMENTATION_PROMPT = 'Implement the plan.';
export const DESKTOP_IMPLEMENTATION_PROMPT = 'Yes, implement this plan';
export const CLEAR_CONTEXT_IMPLEMENTATION_PREFIX =
  "A previous agent produced the plan below to accomplish the user's task. " +
  'Implement the plan in a fresh context. Treat the plan as the source of user intent, ' +
  're-read files as needed, and carry the work through implementation and verification.';

const COMMENT_PREFIX = '<!-- plan-forge-resume:v1:';
const COMMENT_SUFFIX = ' -->';
const HASH_RE = /^[a-f0-9]{64}$/;
const ID_RE = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,255}$/;
const MODEL_RE = /^[A-Za-z0-9][A-Za-z0-9._[\]=,-]{0,255}$/;
const NONCE_RE = /^[A-Za-z0-9_-]{43}$/;

function fail(message) {
  throw new Error(`invalid Forge resume envelope: ${message}`);
}

function exactKeys(value, expected, label) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) fail(`${label} must be an object`);
  const actual = Object.keys(value).sort();
  const wanted = [...expected].sort();
  if (actual.length !== wanted.length || actual.some((key, index) => key !== wanted[index])) {
    fail(`${label} fields must be exactly: ${wanted.join(', ')}`);
  }
}

function text(value, label, maxBytes = 64 * 1024) {
  if (typeof value !== 'string' || value.length === 0 || Buffer.byteLength(value) > maxBytes) {
    fail(`${label} must be a non-empty bounded string`);
  }
  if (value.includes('\0')) fail(`${label} contains a NUL byte`);
  return value;
}

function id(value, label) {
  if (typeof value !== 'string' || !ID_RE.test(value)) fail(`${label} is malformed`);
  return value;
}

function hash(value, label) {
  if (typeof value !== 'string' || !HASH_RE.test(value)) fail(`${label} must be a lowercase SHA-256`);
  return value;
}

function selection(value, label) {
  exactKeys(value, ['model', 'effort'], label);
  if (typeof value.model !== 'string' || !MODEL_RE.test(value.model)) {
    fail(`${label}.model is malformed`);
  }
  return {
    model: value.model,
    effort: id(value.effort, `${label}.effort`).toLowerCase(),
  };
}

function repository(value) {
  exactKeys(value, ['workspaceRoot', 'gitCommonDir'], 'repository');
  const workspaceRoot = text(value.workspaceRoot, 'repository.workspaceRoot', 4096);
  const gitCommonDir = text(value.gitCommonDir, 'repository.gitCommonDir', 4096);
  if (!isAbsolute(workspaceRoot) || !isAbsolute(gitCommonDir)) {
    fail('repository paths must be absolute');
  }
  return { workspaceRoot, gitCommonDir };
}

function origin(value) {
  exactKeys(value, ['sessionId', 'transcriptPath', 'turnId', 'itemId'], 'origin');
  const transcriptPath = text(value.transcriptPath, 'origin.transcriptPath', 4096);
  if (!isAbsolute(transcriptPath)) fail('origin.transcriptPath must be absolute');
  return {
    sessionId: id(value.sessionId, 'origin.sessionId'),
    transcriptPath,
    turnId: id(value.turnId, 'origin.turnId'),
    itemId: id(value.itemId, 'origin.itemId'),
  };
}

function reasoningLevel(value, index) {
  exactKeys(value, ['effort', 'description'], `catalogObservation.models[].reasoningLevels[${index}]`);
  return {
    effort: id(value.effort, `catalog reasoning effort ${index}`).toLowerCase(),
    description: typeof value.description === 'string' ? value.description : fail('catalog effort description is invalid'),
  };
}

function observedModel(value, index) {
  exactKeys(
    value,
    ['slug', 'displayName', 'description', 'priority', 'defaultEffort', 'reasoningLevels'],
    `catalogObservation.models[${index}]`,
  );
  if (!Number.isFinite(value.priority)) fail(`catalogObservation.models[${index}].priority is invalid`);
  if (!Array.isArray(value.reasoningLevels) || value.reasoningLevels.length === 0) {
    fail(`catalogObservation.models[${index}].reasoningLevels is invalid`);
  }
  const reasoningLevels = value.reasoningLevels.map(reasoningLevel);
  const defaultEffort = id(value.defaultEffort, `catalog default effort ${index}`).toLowerCase();
  if (!reasoningLevels.some((level) => level.effort === defaultEffort)) {
    fail(`catalogObservation.models[${index}] does not contain its default effort`);
  }
  if (reasoningLevels.some((level) => level.effort === 'ultra')) {
    fail('catalogObservation must not contain ultra');
  }
  return {
    slug: typeof value.slug === 'string' && MODEL_RE.test(value.slug)
      ? value.slug
      : fail(`catalog model slug ${index} is malformed`),
    displayName: text(value.displayName, `catalog display name ${index}`, 4096),
    description: typeof value.description === 'string'
      ? value.description
      : fail(`catalog description ${index} is invalid`),
    priority: value.priority,
    defaultEffort,
    reasoningLevels,
  };
}

function catalogObservation(value) {
  exactKeys(value, ['generatedAt', 'models'], 'catalogObservation');
  if (!Number.isFinite(Date.parse(value.generatedAt))) fail('catalogObservation.generatedAt is invalid');
  if (!Array.isArray(value.models) || value.models.length < 1 || value.models.length > 8) {
    fail('catalogObservation.models is invalid');
  }
  const models = value.models.map(observedModel);
  if (new Set(models.map((model) => model.slug)).size !== models.length) {
    fail('catalogObservation.models contains duplicate slugs');
  }
  return { generatedAt: value.generatedAt, models };
}

function canonicalReviewLog(value) {
  text(value, 'reviewLog', 256 * 1024);
  if (value.includes('\r') || !value.endsWith('\n') || value.endsWith('\n\n')) {
    fail('reviewLog must use LF and have exactly one terminal newline');
  }
  if (!value.startsWith('<!-- plan-forge-flow:owned -->\n')) {
    fail('reviewLog lacks the Forge ownership marker');
  }
  return value;
}

export function canonicalHumanPlan(value) {
  text(value, 'human plan', 256 * 1024);
  if (value.includes('\r') || !value.endsWith('\n') || value.endsWith('\n\n')) {
    throw new Error('human plan must use UTF-8/LF and have exactly one terminal newline');
  }
  if (!value.startsWith('<!-- plan-forge-flow:owned -->\n')) {
    throw new Error('human plan lacks the Forge ownership marker');
  }
  return value;
}

export function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}

export function repositoryIdentityEquals(left, right) {
  const normalize = (value) => process.platform === 'win32' ? value.toLowerCase() : value;
  return normalize(left.workspaceRoot) === normalize(right.workspaceRoot) &&
    normalize(left.gitCommonDir) === normalize(right.gitCommonDir);
}

export function issuanceItemId({ sessionId, transcriptPath, turnId }, nonce, humanPlanHash) {
  return `forge-${sha256(`${sessionId}\n${transcriptPath}\n${turnId}\n${nonce}\n${humanPlanHash}`).slice(0, 48)}`;
}

export function validateEnvelope(value) {
  exactKeys(value, [
    'version',
    'humanPlanHash',
    'repository',
    'origin',
    'planRevision',
    'nonce',
    'completedReviewRounds',
    'maxRounds',
    'reviewer',
    'builder',
    'builderPlanHash',
    'catalogObservation',
    'reviewLog',
  ], 'envelope');
  if (value.version !== ENVELOPE_VERSION) fail(`unsupported version ${JSON.stringify(value.version)}`);
  const humanPlanHash = hash(value.humanPlanHash, 'humanPlanHash');
  if (!Number.isInteger(value.planRevision) || value.planRevision < 1) fail('planRevision is invalid');
  if (typeof value.nonce !== 'string' || !NONCE_RE.test(value.nonce)) fail('nonce is malformed');
  const parsedOrigin = origin(value.origin);
  if (parsedOrigin.itemId !== issuanceItemId(parsedOrigin, value.nonce, humanPlanHash)) {
    fail('origin.itemId does not match the deterministic issuance identity');
  }
  if (!Number.isInteger(value.completedReviewRounds) || value.completedReviewRounds < 1) {
    fail('completedReviewRounds is invalid');
  }
  if (!Number.isInteger(value.maxRounds) || value.maxRounds < value.completedReviewRounds) {
    fail('maxRounds is invalid');
  }
  const reviewer = selection(value.reviewer, 'reviewer');
  const builder = selection(value.builder, 'builder');
  const builderPlanHash = hash(value.builderPlanHash, 'builderPlanHash');
  if (builderPlanHash !== humanPlanHash) fail('builderPlanHash does not match humanPlanHash');
  const observation = catalogObservation(value.catalogObservation);
  for (const selected of [reviewer, builder]) {
    const observed = observation.models.find((model) => model.slug === selected.model);
    if (!observed || !observed.reasoningLevels.some((level) => level.effort === selected.effort)) {
      fail(`selection ${selected.model}/${selected.effort} is absent from catalogObservation`);
    }
  }
  return {
    version: ENVELOPE_VERSION,
    humanPlanHash,
    repository: repository(value.repository),
    origin: parsedOrigin,
    planRevision: value.planRevision,
    nonce: value.nonce,
    completedReviewRounds: value.completedReviewRounds,
    maxRounds: value.maxRounds,
    reviewer,
    builder,
    builderPlanHash,
    catalogObservation: observation,
    reviewLog: canonicalReviewLog(value.reviewLog),
  };
}

export function decodeEnvelope(encoded) {
  if (typeof encoded !== 'string' || !/^[A-Za-z0-9_-]+$/.test(encoded)) fail('encoding is malformed');
  const bytes = Buffer.from(encoded, 'base64url');
  if (bytes.length === 0 || bytes.length > MAX_ENVELOPE_BYTES) fail('encoding exceeds the size bound');
  if (bytes.toString('base64url') !== encoded) fail('encoding is not canonical base64url');
  let value;
  try {
    value = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  } catch {
    fail('JSON is malformed');
  }
  return validateEnvelope(value);
}

export function encodeEnvelope(value) {
  const envelope = validateEnvelope(value);
  const bytes = Buffer.from(JSON.stringify(envelope));
  if (bytes.length > MAX_ENVELOPE_BYTES) fail('encoding exceeds the size bound');
  return bytes.toString('base64url');
}

export function newNonce() {
  return randomBytes(32).toString('base64url');
}

export function buildApprovalWrapper(humanPlan, envelope) {
  const plan = canonicalHumanPlan(humanPlan);
  if (sha256(plan) !== envelope.humanPlanHash) {
    throw new Error('human plan hash does not match the Forge resume envelope');
  }
  return `${plan}${COMMENT_PREFIX}${encodeEnvelope(envelope)}${COMMENT_SUFFIX}\n`;
}

export function extractApprovalWrapper(value) {
  if (typeof value !== 'string' || Buffer.byteLength(value) > MAX_ENVELOPE_BYTES + 512 * 1024) {
    throw new Error('Forge approval wrapper is missing or exceeds the size bound');
  }
  const escapedPrefix = COMMENT_PREFIX.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const escapedSuffix = COMMENT_SUFFIX.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const matches = [...value.matchAll(new RegExp(`${escapedPrefix}([A-Za-z0-9_-]+)${escapedSuffix}`, 'g'))];
  const wrapperMarkers = value.match(/<!--\s*plan-forge-resume:/g) ?? [];
  if (matches.length !== 1 || wrapperMarkers.length !== 1) {
    throw new Error('expected exactly one Forge resume envelope comment');
  }
  const match = matches[0];
  const before = value.slice(0, match.index);
  const after = value.slice(match.index + match[0].length);
  if (after !== '\n') throw new Error('Forge resume envelope comment must be terminal');
  const humanPlan = canonicalHumanPlan(before);
  const envelope = decodeEnvelope(match[1]);
  if (sha256(humanPlan) !== envelope.humanPlanHash) {
    throw new Error('Forge approval wrapper human plan hash mismatch');
  }
  return { humanPlan, envelope, wrapper: value };
}

export function classifyImplementationPrompt(prompt) {
  if (prompt === NORMAL_IMPLEMENTATION_PROMPT || prompt === DESKTOP_IMPLEMENTATION_PROMPT) {
    return { kind: 'same-context', wrapper: null };
  }
  const prefix = `${CLEAR_CONTEXT_IMPLEMENTATION_PREFIX}\n\n`;
  if (!prompt.startsWith(prefix)) return { kind: 'other', wrapper: null };
  const wrapperText = prompt.slice(prefix.length);
  try {
    return { kind: 'clear-context', wrapper: extractApprovalWrapper(wrapperText) };
  } catch {
    return { kind: 'other', wrapper: null };
  }
}
