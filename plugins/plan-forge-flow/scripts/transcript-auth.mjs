import { existsSync, readFileSync, realpathSync, statSync } from 'node:fs';
import {
  classifyImplementationPrompt,
  extractApprovalWrapper,
} from './resume-envelope.mjs';

const MAX_TRANSCRIPT_BYTES = 64 * 1024 * 1024;
const MAX_ENVIRONMENT_CONTEXT_BYTES = 64 * 1024;

function fail(message) {
  throw new Error(`Forge transcript authorization failed: ${message}`);
}

function messageText(payload) {
  if (!Array.isArray(payload.content) || payload.content.length === 0) fail('message content is malformed');
  let result = '';
  for (const part of payload.content) {
    if (!part || typeof part !== 'object' ||
        (part.type !== 'input_text' && part.type !== 'output_text') ||
        typeof part.text !== 'string') {
      fail('message content uses an unsupported transcript schema');
    }
    result += part.text;
  }
  return result;
}

function isSyntheticEnvironmentMessage(record) {
  if (record.kind !== 'message' || record.role !== 'user' ||
      Buffer.byteLength(record.text, 'utf8') > MAX_ENVIRONMENT_CONTEXT_BYTES) {
    return false;
  }
  return /^<environment_context>\r?\n[\s\S]*\r?\n<\/environment_context>\s*$/.test(record.text);
}

export function readTranscript(path) {
  if (typeof path !== 'string' || !existsSync(path)) fail(`transcript is missing: ${path}`);
  const canonicalPath = realpathSync(path);
  const size = statSync(canonicalPath).size;
  if (size <= 0 || size > MAX_TRANSCRIPT_BYTES) fail('transcript size is invalid');
  const text = readFileSync(canonicalPath, 'utf8');
  const rawLines = text.split('\n');
  if (rawLines.at(-1) === '') rawLines.pop();
  const records = [];
  let currentTurn = null;
  let currentMode = 'unknown';
  let sessionId = null;
  for (let index = 0; index < rawLines.length; index++) {
    let record;
    try {
      record = JSON.parse(rawLines[index]);
    } catch {
      fail(`transcript line ${index + 1} is malformed JSON`);
    }
    if (!record || typeof record !== 'object' || Array.isArray(record) ||
        typeof record.type !== 'string' || !record.payload || typeof record.payload !== 'object') {
      fail(`transcript line ${index + 1} has an unsupported schema`);
    }
    if (record.type === 'session_meta') {
      const observed = record.payload.id ?? record.payload.session_id;
      if (typeof observed !== 'string' || !observed) fail('session metadata is malformed');
      if (sessionId && sessionId !== observed) fail('session metadata is ambiguous');
      sessionId = observed;
    }
    if (record.type === 'turn_context') {
      const turnId = record.payload.turn_id;
      if (typeof turnId !== 'string' || !turnId) fail(`turn_context at line ${index + 1} has no turn_id`);
      const collaboration = record.payload.collaboration_mode;
      const mode = collaboration && typeof collaboration === 'object' && !Array.isArray(collaboration)
        ? collaboration.mode
        : null;
      currentTurn = turnId;
      currentMode = mode === 'plan' || mode === 'default' ? mode : 'unknown';
      records.push({ kind: 'turn', index, turnId, mode: currentMode, raw: record });
      continue;
    }
    if (record.type === 'response_item' && record.payload.type === 'message') {
      const role = record.payload.role;
      if (role !== 'user' && role !== 'assistant' && role !== 'developer') {
        fail(`message at line ${index + 1} has an unsupported role`);
      }
      records.push({
        kind: 'message',
        index,
        id: typeof record.payload.id === 'string' ? record.payload.id : null,
        role,
        phase: record.payload.phase ?? null,
        text: messageText(record.payload),
        turnId: currentTurn,
        mode: currentMode,
        raw: record,
      });
      continue;
    }
    if (record.type === 'response_item' &&
        (/(?:^|_)(?:call|call_output)$/.test(record.payload.type) ||
          record.payload.type === 'tool_search_output')) {
      records.push({
        kind: 'tool',
        index,
        turnId: currentTurn,
        mode: currentMode,
        raw: record,
      });
      continue;
    }
    if (record.type === 'response_item' && record.payload.type === 'reasoning') {
      records.push({
        kind: 'reasoning',
        index,
        turnId: currentTurn,
        mode: currentMode,
        raw: record,
      });
      continue;
    }
    if (record.type === 'response_item') {
      records.push({ kind: 'unsupported-activity', index, raw: record });
      continue;
    }
    records.push({ kind: 'other', index, raw: record });
  }
  if (!sessionId) fail('transcript has no session metadata');
  return { path: canonicalPath, sessionId, records };
}

export function collaborationModeForTurn(transcript, turnId) {
  const matches = transcript.records.filter((record) => record.kind === 'turn' && record.turnId === turnId);
  if (matches.length !== 1) {
    return { mode: 'unknown', reason: matches.length === 0 ? 'missing-turn-context' : 'ambiguous-turn-context' };
  }
  const mode = matches[0].mode;
  return mode === 'plan' || mode === 'default'
    ? { mode, reason: null, record: matches[0] }
    : { mode: 'unknown', reason: 'unsupported-collaboration-mode', record: matches[0] };
}

function proposedPlanWrapper(message) {
  if (message.kind !== 'message' || message.role !== 'assistant' ||
      message.phase !== 'final_answer' || message.mode !== 'plan') {
    return null;
  }
  const match = /^<proposed_plan>\n([\s\S]*)<\/proposed_plan>\s*$/.exec(message.text);
  if (!match) return null;
  try {
    return extractApprovalWrapper(match[1]);
  } catch {
    return null;
  }
}

function assertOriginMatches(envelope, transcript, candidate) {
  const origin = envelope.origin;
  if (realpathSync(origin.transcriptPath) !== transcript.path) fail('origin transcript path mismatch');
  if (origin.sessionId !== transcript.sessionId) fail('origin session mismatch');
  if (origin.turnId !== candidate.turnId) fail('origin turn mismatch');
  if (candidate.mode !== 'plan') fail('origin proposed-plan turn was not in Plan mode');
}

function implementationTurn(transcript, turnId, expectedPrompt = null, allowMissingPrompt = false) {
  const context = collaborationModeForTurn(transcript, turnId);
  if (context.mode !== 'default') fail(`current collaboration mode is ${context.mode}`);
  const turns = transcript.records.filter((record) => record.kind === 'turn');
  const turnIndex = turns.findIndex((record) => record.index === context.record.index);
  const previous = turnIndex > 0 ? turns[turnIndex - 1] : null;
  const next = turnIndex + 1 < turns.length ? turns[turnIndex + 1] : null;
  if (next) fail('another turn intervened after the implementation submission');
  const beforeUsers = transcript.records.filter((record) =>
    record.kind === 'message' && record.role === 'user' &&
    !isSyntheticEnvironmentMessage(record) &&
    record.index > (previous?.index ?? -1) && record.index < context.record.index);
  const afterUsers = transcript.records.filter((record) =>
    record.kind === 'message' && record.role === 'user' &&
    !isSyntheticEnvironmentMessage(record) &&
    record.index > context.record.index && record.index < (next?.index ?? Infinity));
  const users = [...beforeUsers, ...afterUsers].sort((left, right) => left.index - right.index);
  if (users.length > 1 || (users.length === 0 && !allowMissingPrompt)) {
    fail('user activity intervened; the implementation turn must contain exactly one submission');
  }
  const prompt = users[0] ?? null;
  if (prompt && expectedPrompt !== null && prompt.text !== expectedPrompt) {
    fail('captured submission does not match the implementation prompt');
  }
  const start = Math.min(context.record.index, prompt?.index ?? context.record.index);
  const end = next?.index ?? Infinity;
  const activity = transcript.records.filter((record) => record.index >= start && record.index < end);
  for (const record of activity) {
    if (record.kind === 'turn' && record.index !== context.record.index) {
      fail('another turn intervened in the implementation submission');
    }
    if (record.kind === 'unsupported-activity') {
      fail('unsupported response activity intervened in the implementation submission');
    }
    if (record.kind === 'reasoning' || isSyntheticEnvironmentMessage(record)) continue;
    if (record.kind !== 'message') continue;
    if (record.role === 'user' && record.index !== prompt?.index) {
      fail('another user message intervened in the implementation submission');
    }
    if (record.role === 'developer') fail('developer activity intervened in the implementation submission');
    if (record.role === 'assistant' && record.phase !== 'commentary') {
      fail(record.phase === 'final_answer'
        ? 'a final assistant answer already consumed the implementation turn'
        : 'unsupported assistant activity intervened in the implementation submission');
    }
  }
  return { context: context.record, prompt, activity };
}

function sameContextCandidate(current, context, prompt, promptText) {
  const classification = classifyImplementationPrompt(promptText);
  if (classification.kind !== 'same-context' && classification.kind !== 'same-context-embedded') {
    fail('current prompt is not a supported exact implementation form');
  }
  const anchor = Math.min(context.index, prompt?.index ?? context.index);
  const priorActivity = current.records.filter((record) =>
    record.index < anchor &&
    (record.kind === 'message' || record.kind === 'tool' || record.kind === 'reasoning'));
  let candidate = priorActivity.at(-1);
  while (candidate && (
    candidate.kind === 'tool' || candidate.kind === 'reasoning' ||
    isSyntheticEnvironmentMessage(candidate) ||
    (candidate.kind === 'message' && candidate.role === 'assistant' &&
      candidate.phase === 'commentary')
  )) {
    candidate = priorActivity[priorActivity.indexOf(candidate) - 1];
  }
  const wrapper = candidate ? proposedPlanWrapper(candidate) : null;
  if (!wrapper) fail('immediately preceding assistant item is not an eligible Forge proposed plan');
  const intervening = current.records.filter((record) =>
    record.index > candidate.index && record.index < anchor);
  for (const record of intervening) {
    const allowed = record.kind === 'other' || record.kind === 'tool' ||
      record.kind === 'reasoning' || isSyntheticEnvironmentMessage(record) ||
      (record.kind === 'message' && record.role === 'assistant' &&
        record.phase === 'commentary');
    if (!allowed) fail('user, planning, or final activity intervened after the proposed plan');
  }
  assertOriginMatches(wrapper.envelope, current, candidate);
  if (classification.kind === 'same-context-embedded' &&
      classification.humanPlan !== wrapper.humanPlan) {
    fail('embedded implementation plan does not match the approved Forge plan');
  }
  return wrapper;
}

function authorizeSameContext(current, turnId) {
  const { context, prompt } = implementationTurn(current, turnId);
  const wrapper = sameContextCandidate(current, context, prompt, prompt.text);
  return {
    kind: 'same-context',
    humanPlan: wrapper.humanPlan,
    envelope: wrapper.envelope,
    wrapper: wrapper.wrapper,
    originTranscript: current,
    currentTranscript: current,
    currentTurnId: turnId,
  };
}

function authorizeSameContextSubmission(current, turnId, promptText) {
  const { context, prompt } = implementationTurn(current, turnId, promptText, true);
  const wrapper = sameContextCandidate(current, context, prompt, promptText);
  return {
    kind: 'same-context',
    humanPlan: wrapper.humanPlan,
    envelope: wrapper.envelope,
    wrapper: wrapper.wrapper,
  };
}

function authorizeClearContext(current, turnId) {
  const { prompt } = implementationTurn(current, turnId);
  const classification = classifyImplementationPrompt(prompt.text);
  if (classification.kind !== 'clear-context') fail('current prompt is not the exact clear-context implementation form');
  const carried = classification.wrapper;
  if (!existsSync(carried.envelope.origin.transcriptPath)) fail('origin transcript is missing');
  const origin = readTranscript(carried.envelope.origin.transcriptPath);
  if (origin.path === current.path) fail('clear-context implementation must use a distinct transcript');
  const candidates = origin.records.filter((record) => {
    if (record.kind !== 'message' || record.turnId !== carried.envelope.origin.turnId) return false;
    const proposed = proposedPlanWrapper(record);
    return proposed?.envelope.origin.itemId === carried.envelope.origin.itemId;
  });
  if (candidates.length !== 1) fail('origin proposed-plan item is missing or ambiguous');
  const candidate = candidates[0];
  const issued = proposedPlanWrapper(candidate);
  if (!issued || issued.wrapper !== carried.wrapper) fail('clear-context wrapper does not match the origin item');
  assertOriginMatches(carried.envelope, origin, candidate);
  const laterActivity = origin.records.filter((record) => record.index > candidate.index);
  if (laterActivity.some((record) => !(
    record.kind === 'other' || record.kind === 'tool' || record.kind === 'reasoning' ||
    (record.kind === 'message' && record.role === 'assistant' &&
      record.phase === 'commentary')
  ))) fail('origin proposed plan is no longer terminal');
  return {
    kind: 'clear-context',
    humanPlan: carried.humanPlan,
    envelope: carried.envelope,
    wrapper: carried.wrapper,
    originTranscript: origin,
    currentTranscript: current,
    currentTurnId: turnId,
  };
}

function authorizeClearContextSubmission(current, turnId, promptText) {
  implementationTurn(current, turnId, promptText, true);
  const classification = classifyImplementationPrompt(promptText);
  if (classification.kind !== 'clear-context') fail('current prompt is not the exact clear-context implementation form');
  const carried = classification.wrapper;
  if (!existsSync(carried.envelope.origin.transcriptPath)) fail('origin transcript is missing');
  const origin = readTranscript(carried.envelope.origin.transcriptPath);
  if (origin.path === current.path) fail('clear-context implementation must use a distinct transcript');
  const candidates = origin.records.filter((record) => {
    if (record.kind !== 'message' || record.turnId !== carried.envelope.origin.turnId) return false;
    const proposed = proposedPlanWrapper(record);
    return proposed?.envelope.origin.itemId === carried.envelope.origin.itemId;
  });
  if (candidates.length !== 1) fail('origin proposed-plan item is missing or ambiguous');
  const candidate = candidates[0];
  const issued = proposedPlanWrapper(candidate);
  if (!issued || issued.wrapper !== carried.wrapper) fail('clear-context wrapper does not match the origin item');
  assertOriginMatches(carried.envelope, origin, candidate);
  const laterActivity = origin.records.filter((record) => record.index > candidate.index);
  if (laterActivity.some((record) => !(
    record.kind === 'other' || record.kind === 'tool' || record.kind === 'reasoning' ||
    (record.kind === 'message' && record.role === 'assistant' &&
      record.phase === 'commentary')
  ))) fail('origin proposed plan is no longer terminal');
  return {
    kind: 'clear-context',
    humanPlan: carried.humanPlan,
    envelope: carried.envelope,
    wrapper: carried.wrapper,
  };
}

export function authorizeNativeImplementation({ transcriptPath, turnId, sessionId }) {
  const current = readTranscript(transcriptPath);
  if (typeof sessionId !== 'string' || current.sessionId !== sessionId) fail('current session mismatch');
  const { prompt } = implementationTurn(current, turnId);
  const classification = classifyImplementationPrompt(prompt.text);
  if (classification.kind === 'same-context' || classification.kind === 'same-context-embedded') {
    return authorizeSameContext(current, turnId);
  }
  if (classification.kind === 'clear-context') return authorizeClearContext(current, turnId);
  fail('prompt is not an authorized native implementation prompt');
}

export function authorizeNativeImplementationSubmission({
  transcriptPath,
  turnId,
  sessionId,
  prompt,
}) {
  const current = readTranscript(transcriptPath);
  if (typeof sessionId !== 'string' || current.sessionId !== sessionId) fail('current session mismatch');
  const classification = classifyImplementationPrompt(prompt);
  if (classification.kind === 'same-context' || classification.kind === 'same-context-embedded') {
    return authorizeSameContextSubmission(current, turnId, prompt);
  }
  if (classification.kind === 'clear-context') {
    return authorizeClearContextSubmission(current, turnId, prompt);
  }
  fail('hook context is emitted only for a proven immediate-predecessor Forge wrapper');
}
