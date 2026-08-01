import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  realpathSync,
  renameSync,
  symlinkSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { execFileSync, spawn, spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import {
  buildApprovalWrapper,
  CLEAR_CONTEXT_IMPLEMENTATION_PREFIX,
  DESKTOP_EMBEDDED_IMPLEMENTATION_PREFIX,
  extractApprovalWrapper,
  issuanceItemId,
  newNonce,
  NORMAL_IMPLEMENTATION_PROMPT,
  sha256,
} from '../../plugins/plan-forge-flow/scripts/resume-envelope.mjs';
import {
  canonicalRepositoryIdentity,
  sessionContextPathFor,
} from '../../plugins/plan-forge-flow/scripts/plugin-paths.mjs';

const FORGE = fileURLToPath(
  new URL('../../plugins/plan-forge-flow/scripts/forge.mjs', import.meta.url),
);
const HOOK = fileURLToPath(
  new URL('../../plugins/plan-forge-flow/scripts/capture-context.mjs', import.meta.url),
);
const PLAN = '<!-- plan-forge-flow:owned -->\n# Plan\n\n## Approach\n1. Implement the feature.\n';
const LOG = '<!-- plan-forge-flow:owned -->\n# Review log\n\nRound 1: APPROVED\n';
const REVISED_PLAN =
  '<!-- plan-forge-flow:owned -->\n# Plan\n\n## Approach\n1. Implement the feature.\n2. Verify the amendment.\n';
const SECOND_PLAN =
  '<!-- plan-forge-flow:owned -->\n# Different plan\n\n## Approach\n1. Build something else.\n';
const SECOND_LOG = '<!-- plan-forge-flow:owned -->\n# Review log\n\nRound 2: APPROVED\n';
const AMENDMENT_CRITIQUE = 'Amended plan is complete.\nVERDICT: APPROVED';
const AMENDMENT_LOG =
  `${LOG}\n## Round 2 — Reviewer\n\n${AMENDMENT_CRITIQUE}\n`;
const CRASH_LABELS = [
  'after-journal-pending',
  'after-stage-plan',
  'after-stage-log',
  'after-stage-state',
  'after-stage-active-marker',
  'after-stage-exclude',
  'after-stage-commit-marker',
  'after-promote-plan',
  'after-promote-log',
  'after-promote-state',
  'after-promote-active-marker',
  'after-promote-exclude',
  'after-create-critiques',
  'after-commit-marker',
  'after-journal-committed',
  'after-tombstone',
  'after-journal-acknowledged',
];

function git(cwd, args) {
  return execFileSync('git', ['-C', cwd, ...args], { encoding: 'utf8' }).trim();
}

function model(slug, priority) {
  return {
    slug,
    display_name: slug,
    description: `${slug} description`,
    default_reasoning_level: 'high',
    supported_reasoning_levels: [
      { effort: 'high', description: 'High' },
      { effort: 'xhigh', description: 'Extra high' },
    ],
    priority,
    visibility: 'list',
    multi_agent_version: 'v2',
  };
}

function observedModel(row) {
  return {
    slug: row.slug,
    displayName: row.display_name,
    description: row.description,
    priority: row.priority,
    defaultEffort: row.default_reasoning_level,
    reasoningLevels: row.supported_reasoning_levels,
  };
}

function writeTranscript(path, sessionId, wrapper) {
  const records = [
    { type: 'session_meta', payload: { id: sessionId } },
    {
      type: 'turn_context',
      payload: { turn_id: 'turn-plan', collaboration_mode: { mode: 'plan' } },
    },
    {
      type: 'response_item',
      payload: {
        type: 'message',
        id: 'item-plan',
        role: 'assistant',
        phase: 'final_answer',
        content: [{ type: 'output_text', text: `<proposed_plan>\n${wrapper}</proposed_plan>` }],
      },
    },
    {
      type: 'turn_context',
      payload: { turn_id: 'turn-implement', collaboration_mode: { mode: 'default' } },
    },
    {
      type: 'response_item',
      payload: {
        type: 'message',
        id: 'item-user',
        role: 'user',
        content: [{ type: 'input_text', text: NORMAL_IMPLEMENTATION_PROMPT }],
      },
    },
  ];
  writeFileSync(path, records.map((record) => JSON.stringify(record)).join('\n') + '\n');
}

function writeRecords(path, records) {
  writeFileSync(path, records.map((record) => JSON.stringify(record)).join('\n') + '\n');
}

function fixture() {
  const root = mkdtempSync(join(tmpdir(), 'forge-materialize-'));
  const repo = join(root, 'repo');
  const pluginData = join(root, 'plugin-data');
  mkdirSync(repo);
  git(repo, ['init', '-q']);
  git(repo, ['-c', 'user.name=Forge Test', '-c', 'user.email=forge@example.test',
    'commit', '--allow-empty', '-q', '-m', 'initial']);
  const { workspaceRoot, gitCommonDir } = canonicalRepositoryIdentity(repo);
  const transcriptPath = join(root, 'transcript.jsonl');
  const rows = [model('gpt-5.6-sol', 1), model('gpt-5.6-terra', 2)];
  const fakeCodex = join(root, 'fake-codex.js');
  writeFileSync(
    fakeCodex,
    `process.stdout.write(${JSON.stringify(JSON.stringify({ models: rows }))});\n`,
  );
  const sessionId = 'session-materialize';
  const nonce = newNonce();
  const humanPlanHash = sha256(PLAN);
  const origin = {
    sessionId,
    transcriptPath,
    turnId: 'turn-plan',
  };
  const envelope = {
    version: 1,
    humanPlanHash,
    repository: { workspaceRoot, gitCommonDir },
    origin: {
      ...origin,
      itemId: issuanceItemId(origin, nonce, humanPlanHash),
    },
    planRevision: 1,
    nonce,
    completedReviewRounds: 1,
    maxRounds: 5,
    reviewer: { model: 'gpt-5.6-sol', effort: 'xhigh' },
    builder: { model: 'gpt-5.6-terra', effort: 'high' },
    builderPlanHash: humanPlanHash,
    catalogObservation: {
      generatedAt: '2026-07-27T00:00:00.000Z',
      models: rows.map(observedModel),
    },
    reviewLog: LOG,
  };
  const wrapper = buildApprovalWrapper(PLAN, envelope);
  writeTranscript(transcriptPath, sessionId, wrapper);
  const previousPluginData = process.env.FORGE_PLUGIN_DATA;
  process.env.FORGE_PLUGIN_DATA = pluginData;
  const capturePath = sessionContextPathFor(workspaceRoot);
  if (previousPluginData === undefined) delete process.env.FORGE_PLUGIN_DATA;
  else process.env.FORGE_PLUGIN_DATA = previousPluginData;
  mkdirSync(join(pluginData, 'session-context'), { recursive: true });
  writeFileSync(capturePath, JSON.stringify({
    version: 2,
    sessionId,
    cwd: workspaceRoot,
    turnId: 'turn-implement',
    transcriptPath,
    collaborationMode: 'default',
    implementationCandidate: true,
    observedAt: new Date().toISOString(),
  }, null, 2) + '\n');
  return { workspaceRoot, gitCommonDir, pluginData, fakeCodex, envelope };
}

function resume(item, crashAt) {
  return spawnSync(process.execPath, [FORGE, 'resume', '--cwd', item.workspaceRoot], {
    encoding: 'utf8',
    env: {
      ...process.env,
      FORGE_PLUGIN_DATA: item.pluginData,
      FORGE_CODEX_PATH: item.fakeCodex,
      ...(crashAt ? { FORGE_CRASH_AT: crashAt } : {}),
    },
  });
}

function resumeAsync(item) {
  return new Promise((resolveResult) => {
    const child = spawn(process.execPath, [FORGE, 'resume', '--cwd', item.workspaceRoot], {
      env: {
        ...process.env,
        FORGE_PLUGIN_DATA: item.pluginData,
        FORGE_CODEX_PATH: item.fakeCodex,
      },
    });
    let stdout = '';
    let stderr = '';
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', (chunk) => { stdout += chunk; });
    child.stderr.on('data', (chunk) => { stderr += chunk; });
    child.on('close', (status) => resolveResult({ status, stdout, stderr }));
  });
}

function staleMaterializationLock(item, token = 'stale-materialization-token') {
  const path = join(item.gitCommonDir, 'plan-forge-materialize.lock');
  writeFileSync(path, JSON.stringify({
    version: 1,
    pid: 2147483647,
    token,
    createdAt: '2000-01-01T00:00:00.000Z',
  }) + '\n');
  return path;
}

function reviseApproval(item, plan, log = SECOND_LOG) {
  item.envelope.nonce = newNonce();
  item.envelope.planRevision += 1;
  item.envelope.humanPlanHash = sha256(plan);
  item.envelope.builderPlanHash = item.envelope.humanPlanHash;
  item.envelope.reviewLog = log;
  item.envelope.origin.itemId = issuanceItemId(
    item.envelope.origin,
    item.envelope.nonce,
    item.envelope.humanPlanHash,
  );
  const wrapper = buildApprovalWrapper(plan, item.envelope);
  writeTranscript(item.envelope.origin.transcriptPath, 'session-materialize', wrapper);
}

function prepareApprovedAmendment(item, builder = item.envelope.builder) {
  const statePath = join(item.workspaceRoot, '.forge', 'state.json');
  const state = JSON.parse(readFileSync(statePath, 'utf8'));
  state.phase = 'review';
  state.amendmentReview = true;
  state.amendmentReviewConsumed = true;
  state.round = 2;
  state.tasks = [{
    number: 1,
    title: 'Implement the feature.',
    hash: sha256('1. Implement the feature.'),
  }];
  state.taskIndex = 1;
  state.taskCount = 1;
  state.builderAgentId = 'persistent-builder-id';
  state.builderSessionObservation = {
    id: state.builderAgentId,
    model: item.envelope.builder.model,
    effort: item.envelope.builder.effort,
  };
  state.headBaseRef = 'refs/plan-forge/head-base';
  state.headBaseSha = 'a'.repeat(40);
  state.worktreeBaseRef = 'refs/plan-forge/worktree-base';
  state.worktreeBaseSha = 'b'.repeat(40);
  const planHash = sha256(REVISED_PLAN);
  const critiqueHash = sha256(AMENDMENT_CRITIQUE);
  state.replayKeys.push(`plan:${planHash}:${critiqueHash}`);
  state.lastVerdict = {
    stage: 'plan',
    action: 'approved',
    verdict: 'APPROVED',
    round: 2,
    coverage: null,
    planHash,
    critiqueHash,
    reviewLogHash: sha256(AMENDMENT_LOG),
  };
  writeFileSync(join(item.workspaceRoot, 'PLAN.md'), REVISED_PLAN);
  writeFileSync(join(item.workspaceRoot, 'PLAN-REVIEW-LOG.md'), AMENDMENT_LOG);
  writeFileSync(statePath, JSON.stringify(state, null, 2) + '\n');
  item.envelope.completedReviewRounds = 2;
  item.envelope.builder = builder;
  reviseApproval(item, REVISED_PLAN, AMENDMENT_LOG);
  return { state, statePath };
}

function issueViaCli(item) {
  const sessionId = 'session-public-issue';
  const transcriptPath = join(item.workspaceRoot, '..', 'public-origin.jsonl');
  writeRecords(transcriptPath, [
    { type: 'session_meta', payload: { id: sessionId } },
    {
      type: 'turn_context',
      payload: { turn_id: 'turn-plan', collaboration_mode: { mode: 'plan' } },
    },
  ]);
  const captureFile = readdirSync(join(item.pluginData, 'session-context'))[0];
  writeFileSync(join(item.pluginData, 'session-context', captureFile), JSON.stringify({
    version: 2,
    sessionId,
    cwd: item.workspaceRoot,
    turnId: 'turn-plan',
    transcriptPath,
    collaborationMode: 'plan',
    implementationCandidate: false,
    observedAt: new Date().toISOString(),
  }));
  const input = JSON.stringify({
    humanPlan: PLAN,
    reviewLog: LOG,
    completedReviewRounds: 1,
    maxRounds: 5,
    reviewer: { model: 'gpt-5.6-sol', effort: 'xhigh' },
    builder: { model: 'gpt-5.6-terra', effort: 'high' },
  });
  const issued = spawnSync(
    process.execPath,
    [FORGE, 'issue-approval', '--cwd', item.workspaceRoot],
    {
      encoding: 'utf8',
      input,
      env: {
        ...process.env,
        FORGE_PLUGIN_DATA: item.pluginData,
        FORGE_CODEX_PATH: item.fakeCodex,
      },
    },
  );
  assert.equal(issued.status, 0, issued.stderr || issued.stdout);
  const result = JSON.parse(issued.stdout);
  assert.deepEqual(Object.keys(result).sort(), [
    'action',
    'humanPlanHash',
    'planRevision',
    'proposedPlanOutput',
  ]);
  const match = /^<proposed_plan>\n([\s\S]*)<\/proposed_plan>$/.exec(result.proposedPlanOutput);
  assert.ok(match);
  const approval = extractApprovalWrapper(match[1]);
  return {
    ...result,
    envelope: approval.envelope,
    wrapper: approval.wrapper,
    sessionId,
    transcriptPath,
  };
}

function runHook(item, { sessionId, transcriptPath, turnId, prompt }) {
  return spawnSync(process.execPath, [HOOK], {
    encoding: 'utf8',
    input: JSON.stringify({
      session_id: sessionId,
      turn_id: turnId,
      transcript_path: transcriptPath,
      cwd: item.workspaceRoot,
      prompt,
    }),
    env: { ...process.env, FORGE_PLUGIN_DATA: item.pluginData },
  });
}

function assertCommitted(item) {
  assert.equal(readFileSync(join(item.workspaceRoot, 'PLAN.md'), 'utf8'), PLAN);
  assert.equal(readFileSync(join(item.workspaceRoot, 'PLAN-REVIEW-LOG.md'), 'utf8'), LOG);
  const state = JSON.parse(readFileSync(join(item.workspaceRoot, '.forge', 'state.json'), 'utf8'));
  assert.equal(state.phase, 'signoff');
  assert.deepEqual(state.modelSelection.builder, item.envelope.builder);
  assert.equal(state.builderSelectionPlanHash, item.envelope.humanPlanHash);
  assert.equal(state.implementationApproval.planHash, item.envelope.humanPlanHash);
  assert.equal(existsSync(join(item.workspaceRoot, '.forge', 'materialization-commit.json')), true);
  assert.equal(existsSync(join(item.workspaceRoot, '.forge', 'critiques')), true);
  assert.equal(existsSync(join(item.gitCommonDir, 'plan-forge-active.json')), true);
  assert.match(readFileSync(join(item.gitCommonDir, 'info', 'exclude'), 'utf8'), /\.forge\//);
  const journals = readdirSync(join(item.pluginData, 'materialization-journal'));
  const tombstones = readdirSync(join(item.pluginData, 'nonce-tombstones'));
  assert.equal(journals.length, 1);
  assert.equal(tombstones.length, 1);
  const journal = JSON.parse(
    readFileSync(join(item.pluginData, 'materialization-journal', journals[0]), 'utf8'),
  );
  assert.equal(journal.status, 'committed');
  assert.equal(journal.acknowledged, true);
}

test('authenticated resume materializes once and rejects nonce replay', () => {
  const item = fixture();
  const first = resume(item);
  assert.equal(first.status, 0, first.stderr || first.stdout);
  assert.equal(JSON.parse(first.stdout).action, 'forge-materialized');
  assertCommitted(item);

  const replay = resume(item);
  assert.equal(replay.status, 1);
  assert.match(replay.stdout, /already been consumed/);
  assertCommitted(item);

  const cleanup = spawnSync(process.execPath, [FORGE, 'cleanup', '--cwd', item.workspaceRoot], {
    encoding: 'utf8',
    env: { ...process.env, FORGE_PLUGIN_DATA: item.pluginData },
  });
  assert.equal(cleanup.status, 0, cleanup.stderr || cleanup.stdout);
  assert.equal(readdirSync(join(item.pluginData, 'nonce-tombstones')).length, 1);
  const postCleanupReplay = resume(item);
  assert.equal(postCleanupReplay.status, 1);
  assert.match(postCleanupReplay.stdout, /already been consumed/);
});

test('public picker and compact approval output drive Desktop embedded-plan resume', () => {
  const item = fixture();
  const picker = spawnSync(
    process.execPath,
    [FORGE, 'picker', '--cwd', item.workspaceRoot, '--role', 'reviewer'],
    {
      encoding: 'utf8',
      env: { ...process.env, FORGE_CODEX_PATH: item.fakeCodex },
    },
  );
  assert.equal(picker.status, 0, picker.stderr || picker.stdout);
  assert.equal(JSON.parse(picker.stdout).action, 'picker-metadata');

  const issued = issueViaCli(item);
  assert.equal(issued.envelope.repository.workspaceRoot, item.workspaceRoot);
  assert.equal(issued.envelope.origin.transcriptPath, realpathSync(issued.transcriptPath));
  const records = [
    { type: 'session_meta', payload: { id: issued.sessionId } },
    {
      type: 'turn_context',
      payload: { turn_id: 'turn-plan', collaboration_mode: { mode: 'plan' } },
    },
    {
      type: 'response_item',
      payload: {
        type: 'message',
        role: 'assistant',
        phase: 'final_answer',
        content: [{ type: 'output_text', text: issued.proposedPlanOutput }],
      },
    },
    {
      type: 'response_item',
      payload: {
        type: 'message',
        role: 'user',
        content: [{
          type: 'input_text',
          text: `${DESKTOP_EMBEDDED_IMPLEMENTATION_PREFIX}\n${PLAN}`,
        }],
      },
    },
    {
      type: 'turn_context',
      payload: { turn_id: 'turn-implement', collaboration_mode: { mode: 'default' } },
    },
    {
      type: 'response_item',
      payload: {
        type: 'message',
        role: 'assistant',
        phase: 'commentary',
        content: [{ type: 'output_text', text: 'Authenticating the approved plan.' }],
      },
    },
  ];
  writeRecords(issued.transcriptPath, records);
  const hook = runHook(item, {
    sessionId: issued.sessionId,
    transcriptPath: issued.transcriptPath,
    turnId: 'turn-implement',
    prompt: `${DESKTOP_EMBEDDED_IMPLEMENTATION_PREFIX}\n${PLAN}`,
  });
  assert.equal(hook.status, 0);
  assert.match(hook.stdout, /additionalContext/);
  item.envelope = issued.envelope;
  const result = resume(item);
  assert.equal(result.status, 0, result.stderr || result.stdout);
  assertCommitted(item);
});

test('public approval command drives authenticated clear-context resume', () => {
  const item = fixture();
  const issued = issueViaCli(item);
  writeRecords(issued.transcriptPath, [
    { type: 'session_meta', payload: { id: issued.sessionId } },
    {
      type: 'turn_context',
      payload: { turn_id: 'turn-plan', collaboration_mode: { mode: 'plan' } },
    },
    {
      type: 'response_item',
      payload: {
        type: 'message',
        role: 'assistant',
        phase: 'final_answer',
        content: [{ type: 'output_text', text: issued.proposedPlanOutput }],
      },
    },
  ]);
  const currentPath = join(item.workspaceRoot, '..', 'public-current.jsonl');
  const currentSession = 'session-public-current';
  const prompt = `${CLEAR_CONTEXT_IMPLEMENTATION_PREFIX}\n\n${issued.wrapper}`;
  const records = [
    { type: 'session_meta', payload: { id: currentSession } },
    {
      type: 'response_item',
      payload: {
        type: 'message',
        role: 'user',
        content: [{ type: 'input_text', text: prompt }],
      },
    },
    {
      type: 'turn_context',
      payload: { turn_id: 'turn-implement', collaboration_mode: { mode: 'default' } },
    },
    {
      type: 'response_item',
      payload: {
        type: 'message',
        role: 'assistant',
        phase: 'commentary',
        content: [{ type: 'output_text', text: 'Authenticating carried approval.' }],
      },
    },
  ];
  writeRecords(currentPath, records);
  const hook = runHook(item, {
    sessionId: currentSession,
    transcriptPath: currentPath,
    turnId: 'turn-implement',
    prompt,
  });
  assert.equal(hook.status, 0);
  assert.match(hook.stdout, /additionalContext/);
  item.envelope = issued.envelope;
  const result = resume(item);
  assert.equal(result.status, 0, result.stderr || result.stdout);
  assertCommitted(item);
});

test('changed public approvals increment revision before any materialization', () => {
  const item = fixture();
  const first = issueViaCli(item);
  assert.equal(first.planRevision, 1);
  assert.equal(existsSync(join(item.workspaceRoot, '.forge')), false);
  writeRecords(first.transcriptPath, [
    { type: 'session_meta', payload: { id: first.sessionId } },
    {
      type: 'turn_context',
      payload: { turn_id: 'turn-plan', collaboration_mode: { mode: 'plan' } },
    },
    {
      type: 'response_item',
      payload: {
        type: 'message',
        role: 'assistant',
        phase: 'final_answer',
        content: [{ type: 'output_text', text: first.proposedPlanOutput }],
      },
    },
    {
      type: 'response_item',
      payload: {
        type: 'message',
        role: 'user',
        content: [{ type: 'input_text', text: 'Revise the plan.' }],
      },
    },
    {
      type: 'turn_context',
      payload: { turn_id: 'turn-plan-2', collaboration_mode: { mode: 'plan' } },
    },
  ]);
  const captureFile = readdirSync(join(item.pluginData, 'session-context'))[0];
  writeFileSync(join(item.pluginData, 'session-context', captureFile), JSON.stringify({
    version: 2,
    sessionId: first.sessionId,
    cwd: item.workspaceRoot,
    turnId: 'turn-plan-2',
    transcriptPath: first.transcriptPath,
    collaborationMode: 'plan',
    implementationCandidate: false,
    observedAt: new Date().toISOString(),
  }));
  const input = JSON.stringify({
    humanPlan: REVISED_PLAN,
    reviewLog: SECOND_LOG,
    completedReviewRounds: 2,
    maxRounds: 5,
    reviewer: { model: 'gpt-5.6-sol', effort: 'xhigh' },
    builder: { model: 'gpt-5.6-terra', effort: 'high' },
  });
  const secondResult = spawnSync(
    process.execPath,
    [FORGE, 'issue-approval', '--cwd', item.workspaceRoot],
    {
      encoding: 'utf8',
      input,
      env: {
        ...process.env,
        FORGE_PLUGIN_DATA: item.pluginData,
        FORGE_CODEX_PATH: item.fakeCodex,
      },
    },
  );
  assert.equal(secondResult.status, 0, secondResult.stderr || secondResult.stdout);
  const second = JSON.parse(secondResult.stdout);
  const match = /^<proposed_plan>\n([\s\S]*)<\/proposed_plan>$/.exec(second.proposedPlanOutput);
  assert.ok(match);
  const secondApproval = extractApprovalWrapper(match[1]);
  assert.equal(second.planRevision, 2);
  assert.equal(secondApproval.envelope.humanPlanHash, sha256(REVISED_PLAN));
  assert.equal(existsSync(join(item.workspaceRoot, '.forge')), false);
  assert.equal(existsSync(join(item.pluginData, 'nonce-tombstones')), false);
});

test('every persistence crash boundary reconciles to one complete materialization', () => {
  for (const label of CRASH_LABELS) {
    const item = fixture();
    const crashed = resume(item, label);
    assert.equal(crashed.status, 86, `${label}: ${crashed.stderr || crashed.stdout}`);
    const retried = resume(item);
    if (label === 'after-journal-acknowledged') {
      assert.equal(retried.status, 1, `${label}: ${retried.stderr || retried.stdout}`);
      assert.match(retried.stdout, /already been consumed/);
    } else {
      assert.equal(retried.status, 0, `${label}: ${retried.stderr || retried.stdout}`);
    }
    assertCommitted(item);
  }
});

test('resume fails closed for stale capture and cross-repository envelope identity', () => {
  const stale = fixture();
  const captureFile = readdirSync(join(stale.pluginData, 'session-context'))[0];
  const capturePath = join(stale.pluginData, 'session-context', captureFile);
  const capture = JSON.parse(readFileSync(capturePath, 'utf8'));
  capture.observedAt = '2000-01-01T00:00:00.000Z';
  writeFileSync(capturePath, JSON.stringify(capture));
  const staleResult = resume(stale);
  assert.equal(staleResult.status, 1);
  assert.match(staleResult.stdout, /capture is stale/);
  assert.equal(existsSync(join(stale.workspaceRoot, '.forge')), false);

  const mismatch = fixture();
  const other = fixture();
  const transcript = readFileSync(join(mismatch.envelope.origin.transcriptPath), 'utf8');
  assert.ok(transcript);
  mismatch.envelope.repository = {
    workspaceRoot: other.workspaceRoot,
    gitCommonDir: other.gitCommonDir,
  };
  const wrapper = buildApprovalWrapper(PLAN, mismatch.envelope);
  writeTranscript(mismatch.envelope.origin.transcriptPath, 'session-materialize', wrapper);
  const mismatchResult = resume(mismatch);
  assert.equal(mismatchResult.status, 1);
  assert.match(mismatchResult.stdout, /different repository or worktree/);
  assert.equal(existsSync(join(mismatch.workspaceRoot, '.forge')), false);

  const worktreeMismatch = fixture();
  const siblingWorktree = join(worktreeMismatch.workspaceRoot, '..', 'other-worktree');
  git(worktreeMismatch.workspaceRoot, ['worktree', 'add', '-q', '-b', 'other-worktree', siblingWorktree]);
  worktreeMismatch.envelope.repository = {
    workspaceRoot: realpathSync(siblingWorktree),
    gitCommonDir: worktreeMismatch.gitCommonDir,
  };
  const worktreeWrapper = buildApprovalWrapper(PLAN, worktreeMismatch.envelope);
  writeTranscript(
    worktreeMismatch.envelope.origin.transcriptPath,
    'session-materialize',
    worktreeWrapper,
  );
  const worktreeResult = resume(worktreeMismatch);
  assert.equal(worktreeResult.status, 1);
  assert.match(worktreeResult.stdout, /different repository or worktree/);
  assert.equal(existsSync(join(worktreeMismatch.workspaceRoot, '.forge')), false);
});

test('retry fails closed when staged transaction content was changed', () => {
  const item = fixture();
  const crashed = resume(item, 'after-stage-plan');
  assert.equal(crashed.status, 86, crashed.stderr || crashed.stdout);
  const journalName = readdirSync(join(item.pluginData, 'materialization-journal'))[0];
  const journal = JSON.parse(
    readFileSync(join(item.pluginData, 'materialization-journal', journalName), 'utf8'),
  );
  writeFileSync(journal.expected.plan.staged, 'foreign staged content\n');
  const retry = resume(item);
  assert.equal(retry.status, 1);
  assert.match(retry.stdout, /foreign or corrupt/);
  assert.equal(existsSync(join(item.workspaceRoot, 'PLAN.md')), false);
});

test('commit-marker reconciliation rejects a changed promoted artifact', () => {
  const item = fixture();
  const crashed = resume(item, 'after-commit-marker');
  assert.equal(crashed.status, 86, crashed.stderr || crashed.stdout);
  writeFileSync(join(item.workspaceRoot, 'PLAN.md'), 'foreign promoted content\n');
  const retry = resume(item);
  assert.equal(retry.status, 1);
  assert.match(retry.stdout, /commit marker exists but plan is missing or mismatched/);
});

test('a pending journal preserves the original approval when the transcript changes', () => {
  const item = fixture();
  const crashed = resume(item, 'after-journal-pending');
  assert.equal(crashed.status, 86, crashed.stderr || crashed.stdout);
  item.envelope.planRevision = 2;
  const changedWrapper = buildApprovalWrapper(PLAN, item.envelope);
  writeTranscript(item.envelope.origin.transcriptPath, 'session-materialize', changedWrapper);
  const changedRetry = resume(item);
  assert.equal(changedRetry.status, 0, changedRetry.stderr || changedRetry.stdout);
  assert.equal(JSON.parse(changedRetry.stdout).planRevision, 1);
  assertCommitted(item);
});

test('a new-turn retry reconciles a pending journal during catalog outage', () => {
  const item = fixture();
  const crashed = resume(item, 'after-journal-pending');
  assert.equal(crashed.status, 86, crashed.stderr || crashed.stdout);
  const unrelatedTranscript = join(item.workspaceRoot, '..', 'new-turn.jsonl');
  writeFileSync(unrelatedTranscript, [
    JSON.stringify({ type: 'session_meta', payload: { id: 'new-session' } }),
    JSON.stringify({
      type: 'turn_context',
      payload: { turn_id: 'new-turn', collaboration_mode: { mode: 'default' } },
    }),
  ].join('\n') + '\n');
  const captureFile = readdirSync(join(item.pluginData, 'session-context'))[0];
  writeFileSync(join(item.pluginData, 'session-context', captureFile), JSON.stringify({
    version: 2,
    sessionId: 'new-session',
    cwd: item.workspaceRoot,
    turnId: 'new-turn',
    transcriptPath: unrelatedTranscript,
    collaborationMode: 'default',
    implementationCandidate: false,
    observedAt: new Date().toISOString(),
  }));
  writeFileSync(item.fakeCodex, 'process.exit(9);\n');
  const retried = resume(item);
  assert.equal(retried.status, 0, retried.stderr || retried.stdout);
  assert.equal(JSON.parse(retried.stdout).recovered, true);
  assertCommitted(item);
});

test('a pending journal retry is independent of later catalog drift', () => {
  const item = fixture();
  const crashed = resume(item, 'after-journal-pending');
  assert.equal(crashed.status, 86, crashed.stderr || crashed.stdout);
  const drifted = [model('gpt-entirely-different', 1)];
  writeFileSync(
    item.fakeCodex,
    `process.stdout.write(${JSON.stringify(JSON.stringify({ models: drifted }))});\n`,
  );
  const retried = resume(item);
  assert.equal(retried.status, 0, retried.stderr || retried.stdout);
  assert.deepEqual(JSON.parse(retried.stdout).builder, item.envelope.builder);
  assertCommitted(item);
});

test('recovery rejects transaction traversal and staging symlink escapes', () => {
  const traversal = fixture();
  assert.equal(resume(traversal, 'after-journal-pending').status, 86);
  const traversalJournalPath = join(
    traversal.pluginData,
    'materialization-journal',
    readdirSync(join(traversal.pluginData, 'materialization-journal'))[0],
  );
  const traversalJournal = JSON.parse(readFileSync(traversalJournalPath, 'utf8'));
  traversalJournal.transactionId = '..\\..\\outside';
  writeFileSync(traversalJournalPath, JSON.stringify(traversalJournal, null, 2) + '\n');
  const traversalRetry = resume(traversal);
  assert.equal(traversalRetry.status, 1);
  assert.match(traversalRetry.stdout, /pending repository journal is incompatible/);

  const escaped = fixture();
  assert.equal(resume(escaped, 'after-journal-pending').status, 86);
  const journalPath = join(
    escaped.pluginData,
    'materialization-journal',
    readdirSync(join(escaped.pluginData, 'materialization-journal'))[0],
  );
  const journal = JSON.parse(readFileSync(journalPath, 'utf8'));
  const outside = join(escaped.workspaceRoot, '..', 'outside-staging');
  mkdirSync(outside);
  mkdirSync(join(escaped.workspaceRoot, '.forge'));
  symlinkSync(
    outside,
    journal.durable.stagingDir,
    process.platform === 'win32' ? 'junction' : 'dir',
  );
  const escapedRetry = resume(escaped);
  assert.equal(escapedRetry.status, 1);
  assert.match(escapedRetry.stdout, /symlink or junction/);
  assert.equal(readdirSync(outside).length, 0);
});

test('pending recovery rejects nonce traversal, filename mismatch, and tombstone symlinks', () => {
  const traversal = fixture();
  assert.equal(resume(traversal, 'after-journal-pending').status, 86);
  const traversalDir = join(traversal.pluginData, 'materialization-journal');
  const traversalPath = join(traversalDir, readdirSync(traversalDir)[0]);
  const traversalJournal = JSON.parse(readFileSync(traversalPath, 'utf8'));
  traversalJournal.nonce = '..\\..\\escaped-tombstone';
  writeFileSync(traversalPath, JSON.stringify(traversalJournal, null, 2) + '\n');
  const traversalRetry = resume(traversal);
  assert.equal(traversalRetry.status, 1);
  assert.match(traversalRetry.stdout, /pending repository journal is incompatible/);
  assert.equal(existsSync(join(traversal.pluginData, 'escaped-tombstone.json')), false);

  const mismatched = fixture();
  assert.equal(resume(mismatched, 'after-journal-pending').status, 86);
  const mismatchDir = join(mismatched.pluginData, 'materialization-journal');
  const original = join(mismatchDir, readdirSync(mismatchDir)[0]);
  renameSync(original, join(mismatchDir, `${'A'.repeat(43)}.json`));
  const mismatchRetry = resume(mismatched);
  assert.equal(mismatchRetry.status, 1);
  assert.match(mismatchRetry.stdout, /pending repository journal is incompatible/);

  const rebound = fixture();
  assert.equal(resume(rebound, 'after-journal-pending').status, 86);
  const reboundDir = join(rebound.pluginData, 'materialization-journal');
  const reboundOriginal = join(reboundDir, readdirSync(reboundDir)[0]);
  const reboundJournal = JSON.parse(readFileSync(reboundOriginal, 'utf8'));
  reboundJournal.nonce = 'B'.repeat(43);
  const reboundPath = join(reboundDir, `${reboundJournal.nonce}.json`);
  writeFileSync(reboundOriginal, JSON.stringify(reboundJournal, null, 2) + '\n');
  renameSync(reboundOriginal, reboundPath);
  const reboundRetry = resume(rebound);
  assert.equal(reboundRetry.status, 1);
  assert.match(reboundRetry.stdout, /pending repository journal is incompatible/);

  const symlinked = fixture();
  const outside = join(symlinked.workspaceRoot, '..', 'outside-tombstones');
  mkdirSync(outside);
  symlinkSync(
    outside,
    join(symlinked.pluginData, 'nonce-tombstones'),
    process.platform === 'win32' ? 'junction' : 'dir',
  );
  const symlinkRetry = resume(symlinked);
  assert.equal(symlinkRetry.status, 1);
  assert.match(symlinkRetry.stdout, /nonce tombstone directory.*symlink or junction/);
  assert.equal(readdirSync(outside).length, 0);
});

test('journal creation and recovery reject plugin-data symlink escapes', () => {
  const fresh = fixture();
  const outsideRoot = join(fresh.workspaceRoot, '..', 'outside-journal-root');
  mkdirSync(outsideRoot);
  symlinkSync(
    outsideRoot,
    join(fresh.pluginData, 'materialization-journal'),
    process.platform === 'win32' ? 'junction' : 'dir',
  );
  const freshResult = resume(fresh);
  assert.equal(freshResult.status, 1);
  assert.match(freshResult.stdout, /materialization journal directory.*symlink or junction/);
  assert.equal(readdirSync(outsideRoot).length, 0);

  const recovered = fixture();
  assert.equal(resume(recovered, 'after-journal-pending').status, 86);
  const journalDir = join(recovered.pluginData, 'materialization-journal');
  const journalPath = join(journalDir, readdirSync(journalDir)[0]);
  const outsideFile = join(recovered.workspaceRoot, '..', 'outside-journal.json');
  writeFileSync(outsideFile, readFileSync(journalPath));
  unlinkSync(journalPath);
  symlinkSync(outsideFile, journalPath, 'file');
  const recoveredResult = resume(recovered);
  assert.equal(recoveredResult.status, 1);
  assert.match(recoveredResult.stdout, /recovered materialization journal path.*symlink or junction/);
});

test('concurrent contenders cannot both reclaim a stale repository lock', async () => {
  const item = fixture();
  const lockPath = staleMaterializationLock(item, 'stale-owner-token');
  const results = await Promise.all([resumeAsync(item), resumeAsync(item)]);
  assert.equal(results.filter((result) => result.status === 0).length, 1);
  const loser = results.find((result) => result.status !== 0);
  assert.ok(loser);
  assert.match(loser.stdout, /lock|recover|already been consumed/);
  assert.equal(existsSync(`${lockPath}.recovery`), false);
  assertCommitted(item);
});

test('materialization-lock recovery resumes crashes at guard, unlink, and acquisition', () => {
  for (const crashAt of [
    'after-materialization-recovery-guard',
    'after-materialization-stale-unlink',
    'after-materialization-recovery-acquired',
  ]) {
    const item = fixture();
    const lockPath = staleMaterializationLock(item);
    const crashed = resume(item, crashAt);
    assert.equal(crashed.status, 86, `${crashAt}: ${crashed.stderr || crashed.stdout}`);
    assert.equal(existsSync(`${lockPath}.recovery`), true, crashAt);
    const recovered = resume(item);
    assert.equal(recovered.status, 0, `${crashAt}: ${recovered.stderr || recovered.stdout}`);
    assert.equal(existsSync(lockPath), false);
    assert.equal(existsSync(`${lockPath}.recovery`), false);
    assertCommitted(item);
  }
});

test('materialization-lock recovery repeatedly rejects primary token replacement', () => {
  const item = fixture();
  const lockPath = staleMaterializationLock(item);
  const crashed = resume(item, 'after-materialization-recovery-guard');
  assert.equal(crashed.status, 86, crashed.stderr || crashed.stdout);
  writeFileSync(lockPath, JSON.stringify({
    version: 1,
    pid: 2147483647,
    token: 'substituted-materialization-token',
    createdAt: '2000-01-01T00:00:00.000Z',
  }) + '\n');
  for (let attempt = 0; attempt < 2; attempt++) {
    const rejected = resume(item);
    assert.equal(rejected.status, 1);
    assert.match(rejected.stdout, /token or PID changed/);
    assert.equal(existsSync(`${lockPath}.recovery`), true);
  }
  assert.equal(existsSync(join(item.workspaceRoot, '.forge')), false);
});

test('committed plan revisions are monotonic for an origin session', () => {
  const item = fixture();
  const first = resume(item);
  assert.equal(first.status, 0, first.stderr || first.stdout);
  item.envelope.nonce = newNonce();
  item.envelope.origin.itemId = issuanceItemId(
    item.envelope.origin,
    item.envelope.nonce,
    item.envelope.humanPlanHash,
  );
  const staleRevisionWrapper = buildApprovalWrapper(PLAN, item.envelope);
  writeTranscript(item.envelope.origin.transcriptPath, 'session-materialize', staleRevisionWrapper);
  const staleRevision = resume(item);
  assert.equal(staleRevision.status, 1);
  assert.match(staleRevision.stdout, /plan revision 1 is not newer than committed revision 1/);
});

test('authenticated amendment resume is crash-safe and preserves completed work and baselines', () => {
  const item = fixture();
  assert.equal(resume(item).status, 0);
  const { state, statePath } = prepareApprovedAmendment(item);

  const crashed = resume(item, 'after-promote-state');
  assert.equal(crashed.status, 86, crashed.stderr || crashed.stdout);
  const retried = resume(item);
  assert.equal(retried.status, 0, retried.stderr || retried.stdout);
  const amended = JSON.parse(readFileSync(statePath, 'utf8'));
  assert.equal(readFileSync(join(item.workspaceRoot, 'PLAN.md'), 'utf8'), REVISED_PLAN);
  assert.equal(readFileSync(join(item.workspaceRoot, 'PLAN-REVIEW-LOG.md'), 'utf8'), AMENDMENT_LOG);
  assert.equal(amended.phase, 'build');
  assert.equal(amended.amendmentReview, false);
  assert.equal(amended.taskIndex, 1);
  assert.equal(amended.taskCount, 2);
  assert.equal(amended.tasks[0].hash, state.tasks[0].hash);
  assert.equal(amended.builderAgentId, 'persistent-builder-id');
  assert.equal(amended.headBaseSha, 'a'.repeat(40));
  assert.equal(amended.worktreeBaseSha, 'b'.repeat(40));
  assert.equal(amended.materialization.planRevision, 2);
  assert.equal(amended.implementationApproval.planHash, sha256(REVISED_PLAN));
  assert.equal(amended.builderSelectionPlanHash, sha256(REVISED_PLAN));
});

test('amendment resume rejects missing approval evidence and durable-log mismatch', () => {
  const unapproved = fixture();
  assert.equal(resume(unapproved).status, 0);
  const prepared = prepareApprovedAmendment(unapproved);
  const state = JSON.parse(readFileSync(prepared.statePath, 'utf8'));
  state.amendmentReviewConsumed = false;
  writeFileSync(prepared.statePath, JSON.stringify(state, null, 2) + '\n');
  const rejected = resume(unapproved);
  assert.equal(rejected.status, 1);
  assert.match(rejected.stdout, /consumed APPROVED review/);
  state.amendmentReviewConsumed = true;
  state.replayKeys = [];
  writeFileSync(prepared.statePath, JSON.stringify(state, null, 2) + '\n');
  const missingEvidence = resume(unapproved);
  assert.equal(missingEvidence.status, 1);
  assert.match(missingEvidence.stdout, /consumed APPROVED review/);

  const mismatched = fixture();
  assert.equal(resume(mismatched).status, 0);
  prepareApprovedAmendment(mismatched);
  mismatched.envelope.reviewLog = SECOND_LOG;
  const wrapper = buildApprovalWrapper(REVISED_PLAN, mismatched.envelope);
  writeTranscript(mismatched.envelope.origin.transcriptPath, 'session-materialize', wrapper);
  const mismatch = resume(mismatched);
  assert.equal(mismatch.status, 1);
  assert.match(mismatch.stdout, /reviewed durable plan and review log/);
});

test('changed amendment builder clears the persistent agent before build dispatch', () => {
  const item = fixture();
  assert.equal(resume(item).status, 0);
  prepareApprovedAmendment(item, { model: 'gpt-5.6-sol', effort: 'high' });
  const amended = resume(item);
  assert.equal(amended.status, 0, amended.stderr || amended.stdout);
  const state = JSON.parse(
    readFileSync(join(item.workspaceRoot, '.forge', 'state.json'), 'utf8'),
  );
  assert.deepEqual(state.modelSelection.builder, { model: 'gpt-5.6-sol', effort: 'high' });
  assert.equal(state.builderAgentId, null);
  assert.equal(state.builderSessionObservation, null);

  const dispatched = spawnSync(
    process.execPath,
    [FORGE, 'dispatch', '--cwd', item.workspaceRoot, '--stage', 'build', '--task', '2'],
    {
      encoding: 'utf8',
      env: { ...process.env, FORGE_PLUGIN_DATA: item.pluginData },
    },
  );
  assert.equal(dispatched.status, 0, dispatched.stderr || dispatched.stdout);
  assert.equal(JSON.parse(dispatched.stdout).agent.persistentId, null);
});

test('cleanup-retained owned artifacts are pair-safely replaced by a later run', () => {
  const item = fixture();
  assert.equal(resume(item).status, 0);
  const cleanup = spawnSync(process.execPath, [FORGE, 'cleanup', '--cwd', item.workspaceRoot], {
    encoding: 'utf8',
    env: { ...process.env, FORGE_PLUGIN_DATA: item.pluginData },
  });
  assert.equal(cleanup.status, 0, cleanup.stderr || cleanup.stdout);
  assert.equal(existsSync(join(item.workspaceRoot, '.forge')), false);
  reviseApproval(item, SECOND_PLAN);

  const crashed = resume(item, 'after-promote-plan');
  assert.equal(crashed.status, 86, crashed.stderr || crashed.stdout);
  const retried = resume(item);
  assert.equal(retried.status, 0, retried.stderr || retried.stdout);
  assert.equal(readFileSync(join(item.workspaceRoot, 'PLAN.md'), 'utf8'), SECOND_PLAN);
  assert.equal(readFileSync(join(item.workspaceRoot, 'PLAN-REVIEW-LOG.md'), 'utf8'), SECOND_LOG);
  assert.equal(
    JSON.parse(readFileSync(join(item.workspaceRoot, '.forge', 'state.json'), 'utf8'))
      .materialization.planRevision,
    2,
  );
});
