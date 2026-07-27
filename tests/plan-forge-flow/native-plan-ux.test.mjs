import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  mkdtempSync,
  readFileSync,
  writeFileSync,
} from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { field } from '../../plugins/plan-forge-flow/scripts/model-catalog.mjs';
import {
  createPlanUxState,
  effortPicker,
  extractFinalProposedPlan,
  finalProposedPlanOutput,
  humanPlanHash,
  issueApprovalEnvelope,
  modelPicker,
  normalizeHumanPlan,
  recordBuilderSelection,
  recordFullPlanPreview,
  revisePlanUxState,
} from '../../plugins/plan-forge-flow/scripts/native-plan-ux.mjs';
import {
  CLEAR_CONTEXT_IMPLEMENTATION_PREFIX,
  NORMAL_IMPLEMENTATION_PROMPT,
} from '../../plugins/plan-forge-flow/scripts/resume-envelope.mjs';
import { authorizeNativeImplementation } from '../../plugins/plan-forge-flow/scripts/transcript-auth.mjs';

const PLAN = '<!-- plan-forge-flow:owned -->\n# Plan\n\n## Approach\n1. Ship it.\n';
const REVISED_PLAN =
  '<!-- plan-forge-flow:owned -->\n# Plan\n\n## Approach\n1. Ship it safely.\n';
const REVIEW_LOG = '<!-- plan-forge-flow:owned -->\n# Review log\n\nVERDICT: APPROVED\n';
const OBSERVED_AT = '2026-07-27T00:00:00.000Z';
const SKILL = fileURLToPath(
  new URL('../../plugins/plan-forge-flow/skills/forge/SKILL.md', import.meta.url),
);
const WORKFLOW = fileURLToPath(
  new URL(
    '../../plugins/plan-forge-flow/skills/forge/references/workflow.md',
    import.meta.url,
  ),
);
const NATIVE_UX = fileURLToPath(
  new URL(
    '../../plugins/plan-forge-flow/skills/forge/references/native-plan-ux.md',
    import.meta.url,
  ),
);

function model(slug, displayName, priority, defaultEffort, levels) {
  return {
    slug,
    displayName: field(displayName, 'test', { observedAt: OBSERVED_AT }),
    description: field(`${slug} CLI description`, 'test', { observedAt: OBSERVED_AT }),
    defaultEffort: field(defaultEffort, 'test', { observedAt: OBSERVED_AT }),
    supportedEfforts: field(
      levels.filter((level) => level.effort !== 'ultra').map((level) => level.effort),
      'test',
      { observedAt: OBSERVED_AT },
    ),
    supportedReasoningLevels: field(levels, 'test', { observedAt: OBSERVED_AT }),
    pickerReasoningLevels: field(levels, 'test', { observedAt: OBSERVED_AT }),
    priority: field(priority, 'test', { observedAt: OBSERVED_AT }),
    visibility: field('list', 'test', { observedAt: OBSERVED_AT }),
  };
}

function catalog(models = [
  model('gpt-terra', 'Terra', 20, 'medium', [
    { effort: 'low', description: 'Low from CLI' },
    { effort: 'medium', description: 'Medium from CLI' },
    { effort: 'high', description: 'High from CLI' },
    { effort: 'ultra', description: 'Forbidden' },
  ]),
  model('gpt-sol', 'Sol', 10, 'high', [
    { effort: 'high', description: 'High from CLI' },
    { effort: 'xhigh', description: 'Extra high from CLI' },
  ]),
  model('gpt-moon', 'Moon', 30, 'high', [
    { effort: 'high', description: 'High from CLI' },
  ]),
]) {
  return { version: 1, generatedAt: OBSERVED_AT, models };
}

function issue(originPath) {
  const source = catalog();
  let state = createPlanUxState(PLAN);
  state = recordFullPlanPreview(state, PLAN);
  state = recordBuilderSelection(
    state,
    { model: 'gpt-terra', effort: 'medium' },
    source,
  );
  return issueApprovalEnvelope(state, {
    reviewer: { model: 'gpt-sol', effort: 'xhigh' },
    catalog: source,
    reviewLog: REVIEW_LOG,
    completedReviewRounds: 2,
    maxRounds: 5,
    repository: {
      workspaceRoot: join(originPath, 'repo'),
      gitCommonDir: join(originPath, 'repo', '.git'),
    },
    origin: {
      sessionId: 'session-origin',
      transcriptPath: join(originPath, 'origin.jsonl'),
      turnId: 'turn-plan',
    },
    nonce: 'A'.repeat(43),
  });
}

function records(sessionId, items) {
  return [
    { type: 'session_meta', payload: { id: sessionId } },
    ...items,
  ];
}

function turn(turnId, mode) {
  return {
    type: 'turn_context',
    payload: { turn_id: turnId, collaboration_mode: { mode } },
  };
}

function message(id, role, text, phase) {
  return {
    type: 'response_item',
    payload: {
      type: 'message',
      id,
      role,
      ...(phase ? { phase } : {}),
      content: [{ type: role === 'assistant' ? 'output_text' : 'input_text', text }],
    },
  };
}

function writeTranscript(path, transcriptRecords) {
  writeFileSync(path, transcriptRecords.map((record) => JSON.stringify(record)).join('\n') + '\n');
}

test('human plan normalization defines one immutable UTF-8/LF byte domain', () => {
  const source = `\uFEFF${PLAN.replace(/\n/g, '\r\n')}\r\n\r\n`;
  assert.equal(normalizeHumanPlan(source), PLAN);
  assert.equal(humanPlanHash(source), humanPlanHash(PLAN));
  assert.throws(() => normalizeHumanPlan('# Plan\r\n'), /ownership marker/);
  assert.throws(
    () => normalizeHumanPlan(`<!-- plan-forge-flow:owned -->\n\ud800\n`),
    /unpaired surrogate/,
  );
  assert.throws(
    () => normalizeHumanPlan('<!-- plan-forge-flow:owned -->\n</proposed_plan>\n'),
    /must not contain/,
  );
});

test('model picker uses priority order, two choices plus More, and clean last pages', () => {
  const first = modelPicker(catalog(), { role: 'reviewer' });
  assert.equal(first.kind, 'question');
  assert.deepEqual(
    first.request.questions[0].options.map((option) => option.label),
    ['Sol', 'Terra', 'More…'],
  );
  assert.match(first.request.questions[0].options[0].description, /^gpt-sol — gpt-sol CLI description$/);
  assert.equal(first.nextCursor, 2);

  const last = modelPicker(catalog(), { role: 'reviewer', cursor: first.nextCursor });
  assert.deepEqual(last, {
    kind: 'automatic',
    selection: 'gpt-moon',
    request: null,
    nextCursor: null,
  });

  const star = model('gpt-star', 'Star', 40, 'high', [
    { effort: 'high', description: 'High from CLI' },
  ]);
  const finalPair = modelPicker(catalog([...catalog().models, star]), {
    role: 'reviewer',
    cursor: 2,
  });
  assert.deepEqual(
    finalPair.request.questions[0].options.map((option) => option.label),
    ['Moon', 'Star'],
  );
  assert.equal(finalPair.nextCursor, null);

  const only = modelPicker(catalog([catalog().models[1]]), { role: 'builder' });
  assert.deepEqual(only, {
    kind: 'automatic',
    selection: 'gpt-sol',
    request: null,
    nextCursor: null,
  });
});

test('effort picker puts the CLI default first, paginates, and never exposes ultra', () => {
  const first = effortPicker(catalog(), { role: 'builder', model: 'gpt-terra' });
  assert.deepEqual(first.request.questions[0].options, [
    { label: 'medium', description: 'Medium from CLI' },
    { label: 'low', description: 'Low from CLI' },
    { label: 'More…', description: 'Show the remaining effort levels.' },
  ]);
  const last = effortPicker(catalog(), {
    role: 'builder',
    model: 'gpt-terra',
    cursor: first.nextCursor,
  });
  assert.deepEqual(last, {
    kind: 'automatic',
    selection: 'high',
    request: null,
    nextCursor: null,
  });
  assert.equal(
    first.request.questions[0].options.some((option) => option.label === 'ultra'),
    false,
  );

  const only = effortPicker(catalog(), { role: 'builder', model: 'gpt-moon' });
  assert.equal(only.kind, 'automatic');
  assert.equal(only.selection, 'high');
});

test('builder selection is impossible before the complete current plan preview', () => {
  const source = catalog();
  const initial = createPlanUxState(PLAN);
  assert.throws(
    () => recordBuilderSelection(initial, { model: 'gpt-terra', effort: 'medium' }, source),
    /must be previewed/,
  );
  assert.throws(
    () => recordFullPlanPreview(initial, REVISED_PLAN),
    /complete canonical human plan/,
  );
  assert.throws(
    () => recordFullPlanPreview(initial, PLAN.replace(/\n/g, '\r\n')),
    /UTF-8\/LF/,
  );
  const previewed = recordFullPlanPreview(initial, PLAN);
  const selected = recordBuilderSelection(
    previewed,
    { model: 'gpt-terra', effort: 'medium' },
    source,
  );
  assert.equal(selected.builderPlanHash, initial.humanPlanHash);
});

test('approval emits only a proposed_plan block with identical human content and one envelope', () => {
  const root = mkdtempSync(join(tmpdir(), 'forge-native-ux-'));
  const issued = issue(root);
  const output = finalProposedPlanOutput(issued.state);
  assert.equal(output.startsWith('<proposed_plan>\n'), true);
  assert.equal(output.endsWith('</proposed_plan>'), true);
  assert.equal((output.match(/plan-forge-resume:/g) ?? []).length, 1);
  const extracted = extractFinalProposedPlan(output);
  assert.equal(extracted.humanPlan, PLAN);
  assert.equal(extracted.envelope.humanPlanHash, humanPlanHash(PLAN));
  assert.deepEqual(extracted.envelope.builder, { model: 'gpt-terra', effort: 'medium' });
  assert.throws(
    () => extractFinalProposedPlan(`Builder selected.\n${output}`),
    /only one proposed_plan block/,
  );
});

test('a complete plan revision invalidates preview, builder, wrapper, and envelope', () => {
  const root = mkdtempSync(join(tmpdir(), 'forge-native-revision-'));
  const issued = issue(root);
  const revised = revisePlanUxState(issued.state, REVISED_PLAN);
  assert.equal(revised.planRevision, 2);
  assert.equal(revised.previewedHash, null);
  assert.equal(revised.builder, null);
  assert.equal(revised.builderPlanHash, null);
  assert.equal(revised.approvalEnvelope, null);
  assert.equal(revised.approvalWrapper, null);
  assert.throws(() => finalProposedPlanOutput(revised), /has not been issued/);
  assert.throws(
    () => revisePlanUxState(revised, REVISED_PLAN),
    /byte-identical/,
  );
});

test('native final output round-trips through same- and clear-context authorization', () => {
  const sameRoot = mkdtempSync(join(tmpdir(), 'forge-native-same-'));
  const same = issue(sameRoot);
  const sameOutput = finalProposedPlanOutput(same.state);
  const samePath = same.envelope.origin.transcriptPath;
  writeTranscript(samePath, records('session-origin', [
    turn('turn-plan', 'plan'),
    message('native-item', 'assistant', sameOutput, 'final_answer'),
    turn('turn-implementation', 'default'),
    message('user-item', 'user', NORMAL_IMPLEMENTATION_PROMPT),
  ]));
  assert.equal(authorizeNativeImplementation({
    transcriptPath: samePath,
    turnId: 'turn-implementation',
    sessionId: 'session-origin',
  }).kind, 'same-context');

  const clearRoot = mkdtempSync(join(tmpdir(), 'forge-native-clear-'));
  const clear = issue(clearRoot);
  const clearOutput = finalProposedPlanOutput(clear.state);
  const clearOriginPath = clear.envelope.origin.transcriptPath;
  writeTranscript(clearOriginPath, records('session-origin', [
    turn('turn-plan', 'plan'),
    message('native-item', 'assistant', clearOutput, 'final_answer'),
  ]));
  const currentPath = join(clearRoot, 'current.jsonl');
  writeTranscript(currentPath, records('session-current', [
    turn('turn-implementation', 'default'),
    message(
      'user-item',
      'user',
      `${CLEAR_CONTEXT_IMPLEMENTATION_PREFIX}\n\n${clear.wrapper}`,
    ),
  ]));
  assert.equal(authorizeNativeImplementation({
    transcriptPath: currentPath,
    turnId: 'turn-implementation',
    sessionId: 'session-current',
  }).kind, 'clear-context');
});

test('skill instructions enforce preview, picker, and final-only native ordering', () => {
  const skill = readFileSync(SKILL, 'utf8');
  const workflow = readFileSync(WORKFLOW, 'utf8');
  const nativeUx = readFileSync(NATIVE_UX, 'utf8');
  assert.match(skill, /native-plan-ux\.md/);
  assert.match(
    workflow,
    /Show the complete canonical human-plan Markdown[\s\S]*Only after that preview is visible.*builder model picker/,
  );
  assert.match(nativeUx, /Show two model options per page/);
  assert.match(nativeUx, /CLI default effort first/);
  assert.match(nativeUx, /Do not call a tool, add commentary, or emit any text before or after/);
  assert.match(nativeUx, /invalidate the previous preview binding, builder\s+selection, wrapper, and envelope/);
});
