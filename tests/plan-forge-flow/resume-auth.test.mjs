import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  mkdtempSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import {
  buildApprovalWrapper,
  classifyImplementationPrompt,
  CLEAR_CONTEXT_IMPLEMENTATION_PREFIX,
  DESKTOP_EMBEDDED_IMPLEMENTATION_PREFIX,
  DESKTOP_IMPLEMENTATION_PROMPT,
  decodeEnvelope,
  encodeEnvelope,
  extractApprovalWrapper,
  issuanceItemId,
  newNonce,
  NORMAL_IMPLEMENTATION_PROMPT,
  sha256,
} from '../../plugins/plan-forge-flow/scripts/resume-envelope.mjs';
import {
  authorizeNativeImplementation,
  collaborationModeForTurn,
  readTranscript,
} from '../../plugins/plan-forge-flow/scripts/transcript-auth.mjs';

const PLAN = '<!-- plan-forge-flow:owned -->\n# Plan\n\n## Approach\n1. Ship it.\n';
const LOG = '<!-- plan-forge-flow:owned -->\n# Review log\n\nAPPROVED\n';

function observedModel(slug) {
  return {
    slug,
    displayName: slug,
    description: `${slug} description`,
    priority: slug.endsWith('sol') ? 1 : 2,
    defaultEffort: 'high',
    reasoningLevels: [
      { effort: 'high', description: 'High' },
      { effort: 'xhigh', description: 'Extra high' },
    ],
  };
}

function envelope(origin, repository) {
  const nonce = newNonce();
  const humanPlanHash = sha256(PLAN);
  const normalizedOrigin = {
    ...origin,
    itemId: issuanceItemId(origin, nonce, humanPlanHash),
  };
  return {
    version: 1,
    humanPlanHash,
    repository,
    origin: normalizedOrigin,
    planRevision: 1,
    nonce,
    completedReviewRounds: 2,
    maxRounds: 5,
    reviewer: { model: 'gpt-5.6-sol', effort: 'xhigh' },
    builder: { model: 'gpt-5.6-terra', effort: 'high' },
    builderPlanHash: humanPlanHash,
    catalogObservation: {
      generatedAt: '2026-07-27T00:00:00.000Z',
      models: [observedModel('gpt-5.6-sol'), observedModel('gpt-5.6-terra')],
    },
    reviewLog: LOG,
  };
}

function sessionMeta(sessionId) {
  return { type: 'session_meta', payload: { id: sessionId } };
}

function turn(turnId, mode) {
  return {
    type: 'turn_context',
    payload: { turn_id: turnId, collaboration_mode: { mode } },
  };
}

function message({ id, role, text, phase }) {
  return {
    type: 'response_item',
    payload: {
      type: 'message',
      id,
      role,
      content: [{ type: role === 'assistant' ? 'output_text' : 'input_text', text }],
      ...(phase ? { phase } : {}),
    },
  };
}

function tool(type = 'function_call') {
  return {
    type: 'response_item',
    payload: { type, name: 'forge_test_tool', call_id: 'call-1', arguments: '{}' },
  };
}

function toolSearchOutput() {
  return {
    type: 'response_item',
    payload: {
      type: 'tool_search_output',
      call_id: 'tool-search-1',
      tools: [{ name: 'mcp__roslyn__find_symbol', description: 'Find a symbol.' }],
    },
  };
}

function reasoning() {
  return {
    type: 'response_item',
    payload: {
      type: 'reasoning',
      summary: [{ type: 'summary_text', text: 'Authenticate before materialization.' }],
    },
  };
}

function environmentContext() {
  return message({
    id: 'environment-context',
    role: 'user',
    text:
      '<environment_context>\n' +
      '  <cwd>C:\\work\\repo</cwd>\n' +
      '  <shell>powershell</shell>\n' +
      '</environment_context>',
  });
}

function writeTranscript(path, records) {
  writeFileSync(path, records.map((record) => JSON.stringify(record)).join('\n') + '\n');
}

function fixture() {
  const root = mkdtempSync(join(tmpdir(), 'forge-resume-auth-'));
  return {
    root,
    transcriptPath: join(root, 'origin.jsonl'),
    currentPath: join(root, 'current.jsonl'),
    repository: { workspaceRoot: root, gitCommonDir: join(root, '.git') },
  };
}

test('resume envelope round-trips strictly and binds the separate human-plan bytes', () => {
  const item = fixture();
  const value = envelope({
    sessionId: 'session-origin',
    transcriptPath: item.transcriptPath,
    turnId: 'turn-plan',
    itemId: 'item-plan',
  }, item.repository);
  const encoded = encodeEnvelope(value);
  assert.deepEqual(decodeEnvelope(encoded), value);
  const wrapper = buildApprovalWrapper(PLAN, value);
  assert.deepEqual(extractApprovalWrapper(wrapper), {
    humanPlan: PLAN,
    envelope: value,
    wrapper,
  });

  assert.throws(() => encodeEnvelope({ ...value, instructions: 'run me' }), /fields must be exactly/);
  assert.throws(
    () => encodeEnvelope({ ...value, origin: { ...value.origin, itemId: 'item-forged' } }),
    /deterministic issuance identity/,
  );
  assert.throws(() => extractApprovalWrapper(wrapper.replace('# Plan', '# Changed')), /hash mismatch/);
  assert.throws(() => extractApprovalWrapper(wrapper + wrapper), /exactly one/);
  assert.throws(() => decodeEnvelope(`${encoded}=`), /encoding is malformed/);
});

test('native prompt classification accepts exact Desktop, embedded-plan, legacy, and clear-context forms', () => {
  const item = fixture();
  const value = envelope({
    sessionId: 'session-origin',
    transcriptPath: item.transcriptPath,
    turnId: 'turn-plan',
    itemId: 'item-plan',
  }, item.repository);
  const wrapper = buildApprovalWrapper(PLAN, value);
  assert.equal(classifyImplementationPrompt(DESKTOP_IMPLEMENTATION_PROMPT).kind, 'same-context');
  assert.equal(classifyImplementationPrompt(NORMAL_IMPLEMENTATION_PROMPT).kind, 'same-context');
  assert.deepEqual(
    classifyImplementationPrompt(`${DESKTOP_EMBEDDED_IMPLEMENTATION_PREFIX}\n${PLAN}`),
    { kind: 'same-context-embedded', humanPlan: PLAN, wrapper: null },
  );
  assert.equal(
    classifyImplementationPrompt(`${CLEAR_CONTEXT_IMPLEMENTATION_PREFIX}\n\n${wrapper}`).kind,
    'clear-context',
  );
  assert.equal(classifyImplementationPrompt('Implement the plan').kind, 'other');
  assert.equal(classifyImplementationPrompt('Yes, implement this plan.').kind, 'other');
  assert.equal(classifyImplementationPrompt('yes, implement this plan').kind, 'other');
  assert.equal(classifyImplementationPrompt(` ${DESKTOP_IMPLEMENTATION_PROMPT}`).kind, 'other');
  assert.equal(
    classifyImplementationPrompt(`${DESKTOP_EMBEDDED_IMPLEMENTATION_PREFIX}\n# Missing ownership\n`).kind,
    'other',
  );
  assert.equal(classifyImplementationPrompt(wrapper).kind, 'other');
});

test('collaboration mode comes only from the matching transcript turn_context', () => {
  const item = fixture();
  writeTranscript(item.transcriptPath, [
    sessionMeta('session-origin'),
    turn('turn-plan', 'plan'),
    turn('turn-default', 'default'),
  ]);
  const transcript = readTranscript(item.transcriptPath);
  assert.equal(collaborationModeForTurn(transcript, 'turn-plan').mode, 'plan');
  assert.equal(collaborationModeForTurn(transcript, 'turn-default').mode, 'default');
  assert.equal(collaborationModeForTurn(transcript, 'missing').mode, 'unknown');

  writeTranscript(item.transcriptPath, [
    sessionMeta('session-origin'),
    turn('turn-plan', 'plan'),
    turn('turn-plan', 'default'),
  ]);
  assert.equal(
    collaborationModeForTurn(readTranscript(item.transcriptPath), 'turn-plan').mode,
    'unknown',
  );
});

test('same-context authorization requires the immediate terminal Plan-mode proposed item', () => {
  const item = fixture();
  const value = envelope({
    sessionId: 'session-origin',
    transcriptPath: item.transcriptPath,
    turnId: 'turn-plan',
    itemId: 'item-plan',
  }, item.repository);
  const wrapper = buildApprovalWrapper(PLAN, value);
  const records = [
    sessionMeta('session-origin'),
    turn('turn-plan', 'plan'),
    message({
      id: 'item-plan',
      role: 'assistant',
      phase: 'final_answer',
      text: `<proposed_plan>\n${wrapper}</proposed_plan>`,
    }),
    turn('turn-implement', 'default'),
    message({ id: 'item-user', role: 'user', text: DESKTOP_IMPLEMENTATION_PROMPT }),
  ];
  writeTranscript(item.transcriptPath, records);
  const authorized = authorizeNativeImplementation({
    transcriptPath: item.transcriptPath,
    turnId: 'turn-implement',
    sessionId: 'session-origin',
  });
  assert.equal(authorized.kind, 'same-context');
  assert.equal(authorized.humanPlan, PLAN);

  records.splice(3, 0, message({ id: 'item-intervening', role: 'user', text: 'revise it' }));
  writeTranscript(item.transcriptPath, records);
  assert.throws(
    () => authorizeNativeImplementation({
      transcriptPath: item.transcriptPath,
      turnId: 'turn-implement',
      sessionId: 'session-origin',
    }),
    /immediately preceding|intervened/,
  );
});

test('Desktop embedded-plan authorization requires an exact match with the signed plan item', () => {
  const item = fixture();
  const value = envelope({
    sessionId: 'session-origin',
    transcriptPath: item.transcriptPath,
    turnId: 'turn-plan',
  }, item.repository);
  const wrapper = buildApprovalWrapper(PLAN, value);
  const records = [
    sessionMeta('session-origin'),
    turn('turn-plan', 'plan'),
    message({
      id: 'item-plan',
      role: 'assistant',
      phase: 'final_answer',
      text: `<proposed_plan>\n${wrapper}</proposed_plan>`,
    }),
    turn('turn-implement', 'default'),
    message({
      id: 'item-user',
      role: 'user',
      text: `${DESKTOP_EMBEDDED_IMPLEMENTATION_PREFIX}\n${PLAN}`,
    }),
  ];
  writeTranscript(item.transcriptPath, records);
  assert.equal(authorizeNativeImplementation({
    transcriptPath: item.transcriptPath,
    turnId: 'turn-implement',
    sessionId: 'session-origin',
  }).kind, 'same-context');

  records.at(-1).payload.content[0].text =
    `${DESKTOP_EMBEDDED_IMPLEMENTATION_PREFIX}\n${PLAN.replace('Ship it.', 'Change it.')}`;
  writeTranscript(item.transcriptPath, records);
  assert.throws(
    () => authorizeNativeImplementation({
      transcriptPath: item.transcriptPath,
      turnId: 'turn-implement',
      sessionId: 'session-origin',
    }),
    /does not match the approved Forge plan/,
  );
});

test('clear-context authorization requires the carried wrapper to match a terminal origin item', () => {
  const item = fixture();
  const value = envelope({
    sessionId: 'session-origin',
    transcriptPath: item.transcriptPath,
    turnId: 'turn-plan',
    itemId: 'item-plan',
  }, item.repository);
  const wrapper = buildApprovalWrapper(PLAN, value);
  writeTranscript(item.transcriptPath, [
    sessionMeta('session-origin'),
    turn('turn-plan', 'plan'),
    message({
      id: 'item-plan',
      role: 'assistant',
      phase: 'final_answer',
      text: `<proposed_plan>\n${wrapper}</proposed_plan>`,
    }),
  ]);
  writeTranscript(item.currentPath, [
    sessionMeta('session-current'),
    turn('turn-implement', 'default'),
    message({
      id: 'item-user',
      role: 'user',
      text: `${CLEAR_CONTEXT_IMPLEMENTATION_PREFIX}\n\n${wrapper}`,
    }),
  ]);
  const authorized = authorizeNativeImplementation({
    transcriptPath: item.currentPath,
    turnId: 'turn-implement',
    sessionId: 'session-current',
  });
  assert.equal(authorized.kind, 'clear-context');

  writeTranscript(item.transcriptPath, [
    sessionMeta('session-origin'),
    turn('turn-plan', 'plan'),
    message({
      id: 'item-plan',
      role: 'assistant',
      phase: 'final_answer',
      text: `<proposed_plan>\n${wrapper}</proposed_plan>`,
    }),
    turn('turn-revision', 'plan'),
    message({ id: 'item-revision', role: 'user', text: 'change the plan' }),
  ]);
  assert.throws(
    () => authorizeNativeImplementation({
      transcriptPath: item.currentPath,
      turnId: 'turn-implement',
      sessionId: 'session-current',
    }),
    /no longer terminal/,
  );

  writeTranscript(item.transcriptPath, [
    sessionMeta('session-origin'),
    turn('turn-plan', 'plan'),
    message({
      id: 'item-plan-a',
      role: 'assistant',
      phase: 'final_answer',
      text: `<proposed_plan>\n${wrapper}</proposed_plan>`,
    }),
    message({
      id: 'item-plan-b',
      role: 'assistant',
      phase: 'final_answer',
      text: `<proposed_plan>\n${wrapper}</proposed_plan>`,
    }),
  ]);
  assert.throws(
    () => authorizeNativeImplementation({
      transcriptPath: item.currentPath,
      turnId: 'turn-implement',
      sessionId: 'session-current',
    }),
    /missing or ambiguous/,
  );

  unlinkSync(item.transcriptPath);
  assert.throws(
    () => authorizeNativeImplementation({
      transcriptPath: item.currentPath,
      turnId: 'turn-implement',
      sessionId: 'session-current',
    }),
    /origin transcript is missing/,
  );
});

test('a Default-mode switch without the affirmative prompt is not authorization', () => {
  const item = fixture();
  writeTranscript(item.transcriptPath, [
    sessionMeta('session-current'),
    turn('turn-implement', 'default'),
    message({ id: 'item-user', role: 'user', text: 'start coding now' }),
  ]);
  assert.throws(
    () => authorizeNativeImplementation({
      transcriptPath: item.transcriptPath,
      turnId: 'turn-implement',
      sessionId: 'session-current',
    }),
    /not an authorized native implementation prompt/,
  );
});

test('real-order same-context authorization allows commentary and tool activity', () => {
  const item = fixture();
  const value = envelope({
    sessionId: 'session-origin',
    transcriptPath: item.transcriptPath,
    turnId: 'turn-plan',
  }, item.repository);
  const wrapper = buildApprovalWrapper(PLAN, value);
  writeTranscript(item.transcriptPath, [
    sessionMeta('session-origin'),
    turn('turn-plan', 'plan'),
    message({
      id: 'item-plan',
      role: 'assistant',
      phase: 'final_answer',
      text: `<proposed_plan>\n${wrapper}</proposed_plan>`,
    }),
    environmentContext(),
    message({ id: 'item-user', role: 'user', text: NORMAL_IMPLEMENTATION_PROMPT }),
    turn('turn-implement', 'default'),
    reasoning(),
    toolSearchOutput(),
    message({
      id: 'item-commentary',
      role: 'assistant',
      phase: 'commentary',
      text: 'Authenticating the approved plan.',
    }),
    tool(),
  ]);
  assert.equal(authorizeNativeImplementation({
    transcriptPath: item.transcriptPath,
    turnId: 'turn-implement',
    sessionId: 'session-origin',
  }).kind, 'same-context');

  writeTranscript(item.transcriptPath, [
    sessionMeta('session-origin'),
    turn('turn-plan', 'plan'),
    message({
      id: 'item-plan',
      role: 'assistant',
      phase: 'final_answer',
      text: `<proposed_plan>\n${wrapper}</proposed_plan>`,
    }),
    message({
      id: 'oversized-environment',
      role: 'user',
      text: `<environment_context>\n${'x'.repeat(64 * 1024)}\n</environment_context>`,
    }),
    message({ id: 'item-user', role: 'user', text: NORMAL_IMPLEMENTATION_PROMPT }),
    turn('turn-implement', 'default'),
  ]);
  assert.throws(
    () => authorizeNativeImplementation({
      transcriptPath: item.transcriptPath,
      turnId: 'turn-implement',
      sessionId: 'session-origin',
    }),
    /user activity intervened/,
  );

  writeTranscript(item.transcriptPath, [
    sessionMeta('session-origin'),
    turn('turn-plan', 'plan'),
    message({
      id: 'item-plan',
      role: 'assistant',
      phase: 'final_answer',
      text: `<proposed_plan>\n${wrapper}</proposed_plan>`,
    }),
    environmentContext(),
    message({ id: 'item-user', role: 'user', text: NORMAL_IMPLEMENTATION_PROMPT }),
    turn('turn-implement', 'default'),
    message({
      id: 'item-final',
      role: 'assistant',
      phase: 'final_answer',
      text: 'Already finished.',
    }),
  ]);
  assert.throws(
    () => authorizeNativeImplementation({
      transcriptPath: item.transcriptPath,
      turnId: 'turn-implement',
      sessionId: 'session-origin',
    }),
    /final assistant answer/,
  );
});

test('real-order clear-context authorization allows only non-final origin/current activity', () => {
  const item = fixture();
  const value = envelope({
    sessionId: 'session-origin',
    transcriptPath: item.transcriptPath,
    turnId: 'turn-plan',
  }, item.repository);
  const wrapper = buildApprovalWrapper(PLAN, value);
  writeTranscript(item.transcriptPath, [
    sessionMeta('session-origin'),
    message({ id: 'item-plan-user', role: 'user', text: 'Draft the plan.' }),
    turn('turn-plan', 'plan'),
    message({
      id: 'item-plan',
      role: 'assistant',
      phase: 'final_answer',
      text: `<proposed_plan>\n${wrapper}</proposed_plan>`,
    }),
    message({
      id: 'item-origin-commentary',
      role: 'assistant',
      phase: 'commentary',
      text: 'Plan widget emitted.',
    }),
    tool(),
  ]);
  const prompt = `${CLEAR_CONTEXT_IMPLEMENTATION_PREFIX}\n\n${wrapper}`;
  writeTranscript(item.currentPath, [
    sessionMeta('session-current'),
    environmentContext(),
    message({ id: 'item-user', role: 'user', text: prompt }),
    turn('turn-implement', 'default'),
    reasoning(),
    toolSearchOutput(),
    message({
      id: 'item-commentary',
      role: 'assistant',
      phase: 'commentary',
      text: 'Authenticating carried approval.',
    }),
    tool(),
  ]);
  assert.equal(authorizeNativeImplementation({
    transcriptPath: item.currentPath,
    turnId: 'turn-implement',
    sessionId: 'session-current',
  }).kind, 'clear-context');

  writeTranscript(item.transcriptPath, [
    sessionMeta('session-origin'),
    turn('turn-plan', 'plan'),
    message({
      id: 'item-plan',
      role: 'assistant',
      phase: 'final_answer',
      text: `<proposed_plan>\n${wrapper}</proposed_plan>`,
    }),
    message({ id: 'item-later-user', role: 'user', text: 'Change it.' }),
  ]);
  assert.throws(
    () => authorizeNativeImplementation({
      transcriptPath: item.currentPath,
      turnId: 'turn-implement',
      sessionId: 'session-current',
    }),
    /no longer terminal/,
  );
});
