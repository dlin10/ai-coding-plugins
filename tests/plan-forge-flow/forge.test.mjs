import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  realpathSync,
  writeFileSync,
} from 'node:fs';
import { execFileSync, spawn, spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import {
  chunkPathspecs,
  classifySensitiveContent,
  classifySensitivePath,
  parseApproachTasks,
  parseCoverage,
  parseFeatureEnabled,
  parseVerdict,
  removeExcludeBlock,
  sha256,
  upsertExcludeBlock,
  validateBuilderSelectionBinding,
  validateModelId,
} from '../../plugins/plan-forge-flow/scripts/forge.mjs';
import { sessionContextPathFor } from '../../plugins/plan-forge-flow/scripts/plugin-paths.mjs';

const FORGE = fileURLToPath(
  new URL('../../plugins/plan-forge-flow/scripts/forge.mjs', import.meta.url),
);
const PLUGIN = fileURLToPath(
  new URL('../../plugins/plan-forge-flow', import.meta.url),
);
const WORKFLOW = join(PLUGIN, 'skills', 'forge', 'references', 'workflow.md');
const NATIVE_UX = join(PLUGIN, 'skills', 'forge', 'references', 'native-plan-ux.md');
const SKILL = join(PLUGIN, 'skills', 'forge', 'SKILL.md');
const README = join(PLUGIN, 'README.md');
const SVG = join(PLUGIN, 'assets', 'plan-forge-workflow.svg');
const OWNED = '<!-- plan-forge-flow:owned -->';
const PLAN = `${OWNED}
# Plan: Test

## Goal
Exercise the durable post-approval workflow.

## Approach
1. Implement the first slice.
   Verify the first slice.
2. Implement the second slice.
   Verify the second slice.

## Key decisions & tradeoffs

## Risks / open questions

## Out of scope
`;
const LOG = `${OWNED}
# Plan Review Log

## Round 1 — Reviewer

VERDICT: APPROVED
`;

function git(cwd, args) {
  return execFileSync('git', ['-C', cwd, ...args], { encoding: 'utf8' }).trim();
}

function withPluginData(pluginData, callback) {
  const previous = process.env.FORGE_PLUGIN_DATA;
  process.env.FORGE_PLUGIN_DATA = pluginData;
  try {
    return callback();
  } finally {
    if (previous === undefined) delete process.env.FORGE_PLUGIN_DATA;
    else process.env.FORGE_PLUGIN_DATA = previous;
  }
}

function writeDefaultCapture(cwd, pluginData) {
  const target = withPluginData(pluginData, () => sessionContextPathFor(cwd));
  mkdirSync(join(pluginData, 'session-context'), { recursive: true });
  writeFileSync(target, JSON.stringify({
    version: 2,
    cwd,
    sessionId: 'session-test',
    turnId: 'turn-test',
    transcriptPath: join(pluginData, 'unused-transcript.jsonl'),
    collaborationMode: 'default',
    collaborationModeReason: null,
    observedAt: new Date().toISOString(),
  }, null, 2) + '\n');
}

function freshState(planHash) {
  const now = new Date().toISOString();
  return {
    version: 2,
    phase: 'signoff',
    amendmentReview: false,
    amendmentReviewConsumed: false,
    round: 1,
    maxRounds: 5,
    fixRound: 0,
    maxFixRounds: 3,
    maxBuildRetries: 3,
    maxVerdictRetries: 2,
    deadlock: false,
    modelSelection: {
      reviewer: { model: 'gpt-reviewer', effort: 'high' },
      builder: { model: 'gpt-builder', effort: 'medium' },
    },
    builderSelectionPlanHash: planHash,
    modelCatalog: null,
    modelResolutionTrace: [],
    builderAgentId: null,
    reviewerAgentIds: [],
    tasks: [],
    taskIndex: 0,
    taskCount: null,
    pendingDispatch: null,
    headBaseRef: null,
    headBaseSha: null,
    worktreeBaseRef: null,
    worktreeBaseSha: null,
    untrackedBaseline: [],
    implementationApproval: {
      kind: 'native-same-context',
      planHash,
      currentSessionId: 'session-test',
      currentTurnId: 'turn-test',
      originSessionId: 'session-test',
      originTurnId: 'turn-plan',
      originItemId: 'forge-item',
      approvedAt: now,
    },
    replayKeys: [],
    lastVerdict: {
      stage: 'plan',
      action: 'approved',
      round: 1,
      coverage: null,
      planHash,
    },
    verifyCommands: [],
    createdAt: now,
    updatedAt: now,
  };
}

function makeRun() {
  const root = mkdtempSync(join(tmpdir(), 'forge-cli-'));
  const cwd = join(root, 'repo');
  const pluginData = join(root, 'plugin-data');
  mkdirSync(cwd);
  git(cwd, ['init', '-q']);
  writeFileSync(join(cwd, 'tracked.txt'), 'baseline\n');
  git(cwd, ['add', 'tracked.txt']);
  git(cwd, ['-c', 'user.name=Forge Test', '-c', 'user.email=forge@example.test',
    'commit', '-q', '-m', 'baseline']);
  const canonical = realpathSync(cwd);
  writeFileSync(join(canonical, 'PLAN.md'), PLAN);
  writeFileSync(join(canonical, 'PLAN-REVIEW-LOG.md'), LOG);
  mkdirSync(join(canonical, '.forge', 'critiques'), { recursive: true });
  writeFileSync(
    join(canonical, '.forge', 'state.json'),
    JSON.stringify(freshState(sha256(PLAN)), null, 2) + '\n',
  );
  upsertExcludeBlock(join(canonical, '.git', 'info', 'exclude'));
  writeDefaultCapture(canonical, pluginData);
  return { root, cwd: canonical, pluginData };
}

function run(item, args, extraEnv = {}) {
  const result = spawnSync(process.execPath, [FORGE, ...args, '--cwd', item.cwd], {
    encoding: 'utf8',
    env: {
      ...process.env,
      FORGE_PLUGIN_DATA: item.pluginData,
      ...extraEnv,
    },
  });
  let json = null;
  try {
    json = JSON.parse(result.stdout.trim());
  } catch {
    // Assertions report raw output when JSON was expected.
  }
  return { code: result.status, json, stdout: result.stdout, stderr: result.stderr };
}

function runAsync(item, args, extraEnv = {}) {
  return new Promise((resolveResult) => {
    const child = spawn(process.execPath, [FORGE, ...args, '--cwd', item.cwd], {
      env: {
        ...process.env,
        FORGE_PLUGIN_DATA: item.pluginData,
        ...extraEnv,
      },
    });
    let stdout = '';
    let stderr = '';
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', (chunk) => { stdout += chunk; });
    child.stderr.on('data', (chunk) => { stderr += chunk; });
    child.on('close', (code) => {
      let json = null;
      try {
        json = JSON.parse(stdout.trim());
      } catch {
        // Assertions report raw output when JSON was expected.
      }
      resolveResult({ code, json, stdout, stderr });
    });
  });
}

function staleStateLock(item, token = 'original-stale-token') {
  const path = join(item.cwd, '.forge', 'lock');
  writeFileSync(path, JSON.stringify({
    pid: 2147483647,
    ts: Date.now() - 60 * 60 * 1000,
    token,
  }));
  return path;
}

function state(item) {
  return JSON.parse(readFileSync(join(item.cwd, '.forge', 'state.json'), 'utf8'));
}

function startBuild(item) {
  let result = run(item, ['lock-plan']);
  assert.equal(result.code, 0, result.stderr || result.stdout);
  assert.equal(result.json.taskCount, 2);
  result = run(item, ['begin-build']);
  assert.equal(result.code, 0, result.stderr || result.stdout);
  assert.equal(result.json.action, 'build-started');
  return result;
}

function reachCodeReview(item) {
  startBuild(item);
  assert.equal(run(item, [
    'builder-session',
    '--id', 'builder-native-1',
    '--observed-model', 'gpt-builder',
    '--observed-effort', 'medium',
  ]).code, 0);
  for (const task of [1, 2]) {
    assert.equal(run(item, ['dispatch', '--stage', 'build', '--task', String(task)]).code, 0);
    assert.equal(run(item, ['complete', '--task', String(task)]).code, 0);
  }
  const transition = run(item, ['set', 'phase=code-review']);
  assert.equal(transition.code, 0, transition.stderr || transition.stdout);
}

test('strict verdict and coverage parsers fail closed', () => {
  assert.equal(parseVerdict('Finding\nVERDICT: APPROVED\n'), 'APPROVED');
  assert.equal(parseVerdict('VERDICT: REVISE'), 'REVISE');
  assert.equal(parseVerdict('VERDICT: APPROVED\nextra'), null);
  assert.equal(parseVerdict('VERDICT: APPROVED\nVERDICT: REVISE'), 'REVISE');
  assert.equal(parseCoverage('COVERAGE: FULL\nVERDICT: APPROVED'), 'FULL');
  assert.equal(
    parseCoverage('COVERAGE: PARTIAL — binary withheld\nVERDICT: REVISE'),
    'PARTIAL',
  );
  assert.equal(parseCoverage('VERDICT: APPROVED'), null);
});

test('path, content, feature, and model classifiers remain deterministic', () => {
  assert.equal(classifySensitivePath('.env'), true);
  assert.equal(classifySensitivePath('src/env.ts'), false);
  assert.equal(
    classifySensitiveContent('token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789"'),
    'high-entropy-assignment',
  );
  assert.equal(classifySensitiveContent('ordinary text'), null);
  assert.equal(parseFeatureEnabled('multi_agent stable true\n'), true);
  assert.equal(parseFeatureEnabled('multi_agent stable false\n'), false);
  assert.equal(validateModelId('gpt-5.6-sol[fast]'), true);
  assert.equal(validateModelId('--danger'), false);
});

test('managed exclude editing is idempotent and preserves user entries', () => {
  const dir = mkdtempSync(join(tmpdir(), 'forge-exclude-'));
  const path = join(dir, 'exclude');
  writeFileSync(path, 'user-entry\n');
  upsertExcludeBlock(path);
  const once = readFileSync(path, 'utf8');
  upsertExcludeBlock(path);
  assert.equal(readFileSync(path, 'utf8'), once);
  assert.match(once, /user-entry/);
  assert.match(once, /\.forge\//);
  removeExcludeBlock(path);
  assert.equal(readFileSync(path, 'utf8').trim(), 'user-entry');
});

test('plan tasks are sequential, independently hashed, and pathspecs are bounded', () => {
  const tasks = parseApproachTasks(PLAN);
  assert.equal(tasks.length, 2);
  assert.equal(tasks[0].number, 1);
  assert.notEqual(tasks[0].hash, tasks[1].hash);
  assert.throws(
    () => parseApproachTasks(PLAN.replace('2. Implement', '3. Implement')),
    /numbered 1\.\.N in order/,
  );
  const paths = Array.from({ length: 40 }, (_, index) => `path ${index}/file#${index}.txt`);
  const chunks = chunkPathspecs(paths, 160);
  assert.ok(chunks.length > 1);
  assert.equal(chunks.flat().length, paths.length);
  assert.ok(chunks.flat().every((entry) => entry.startsWith(':(top,literal)')));
});

test('documentation exposes only the final native Plan-mode contract', () => {
  const workflow = readFileSync(WORKFLOW, 'utf8');
  const nativeUx = readFileSync(NATIVE_UX, 'utf8');
  const skill = readFileSync(SKILL, 'utf8');
  const readme = readFileSync(README, 'utf8');
  const svg = readFileSync(SVG, 'utf8');

  for (const text of [workflow, nativeUx, skill, readme]) {
    assert.doesNotMatch(text, /--native-models|cached catalog|static fallback/);
    assert.doesNotMatch(text, /reviewer may not be weaker|strength invariant/);
    assert.doesNotMatch(text, /confirm-signoff|typed sign-off|typed free-text/);
  }
  assert.match(workflow, /codex debug models.*only catalog source/s);
  assert.match(workflow, /Acts 1–2.*strictly read-only/s);
  assert.match(workflow, /complete canonical human-plan Markdown.*builder model picker/s);
  assert.match(workflow, /native widget is the sole sign-off surface/);
  assert.match(workflow, /initial plan-review cap is five/);
  assert.match(workflow, /initial verification retry cap is three/);
  assert.match(workflow, /Approval requires `COVERAGE: FULL`/);
  assert.match(workflow, /persistent builder/i);
  assert.match(nativeUx, /Show two model options per page/);
  assert.match(skill, /fresh `forge_reviewer` for every review round/);
  assert.match(readme, /nonce tombstone/);
  assert.match(svg, /Native plan widget/);
});

test('packaged hooks and native agents preserve the trust boundary', () => {
  const hooks = JSON.parse(readFileSync(join(PLUGIN, 'hooks', 'hooks.json'), 'utf8'));
  const command = hooks.hooks.UserPromptSubmit[0].hooks[0];
  assert.equal(command.type, 'command');
  assert.match(command.command, /capture-context\.mjs/);
  const reviewer = readFileSync(join(PLUGIN, 'agents', 'forge_reviewer.toml'), 'utf8');
  const builder = readFileSync(join(PLUGIN, 'agents', 'forge_builder.toml'), 'utf8');
  assert.match(reviewer, /sandbox_mode = "read-only"/);
  assert.match(reviewer, /COVERAGE: FULL/);
  assert.match(builder, /sandbox_mode = "workspace-write"/);
  assert.match(builder, /exactly the single locked plan step/);
  assert.match(builder, /Do not create commits or stage changes/);
});

test('obsolete pre-sign-off init and typed confirmation commands are not public', () => {
  const item = makeRun();
  for (const command of ['init', 'confirm-signoff']) {
    const result = run(item, [command]);
    assert.equal(result.code, 1);
    assert.match(result.json.error, /unknown command/);
  }
});

test('materialized native approval gates plan locking and build start', () => {
  const item = makeRun();
  const binding = validateBuilderSelectionBinding(state(item), sha256(PLAN));
  assert.deepEqual(binding, { model: 'gpt-builder', effort: 'medium' });
  startBuild(item);
  const current = state(item);
  assert.equal(current.phase, 'build');
  assert.equal(current.taskCount, 2);
  assert.equal(current.implementationApproval.planHash, sha256(PLAN));
  assert.ok(current.headBaseSha);
  assert.ok(current.worktreeBaseSha);
});

test('changed plans invalidate native approval and builder binding', () => {
  const item = makeRun();
  writeFileSync(join(item.cwd, 'PLAN.md'), `${PLAN}\nchanged\n`);
  let result = run(item, ['lock-plan']);
  assert.equal(result.code, 3);
  assert.match(result.json.error, /implementation approval/);
  const current = state(item);
  assert.throws(
    () => validateBuilderSelectionBinding(current, sha256(readFileSync(join(item.cwd, 'PLAN.md')))),
    /does not match/,
  );
});

test('persistent builder receives one ordered task at a time', () => {
  const item = makeRun();
  startBuild(item);
  let result = run(item, [
    'builder-session',
    '--id', 'builder-native-1',
    '--observed-model', 'different-reported-model',
    '--observed-effort', 'low',
  ]);
  assert.equal(result.code, 0);
  assert.match(result.stderr, /does not match pin/);

  result = run(item, ['dispatch', '--stage', 'build', '--task', '2']);
  assert.equal(result.code, 3);
  assert.match(result.json.error, /next task is 1/);
  result = run(item, ['dispatch', '--stage', 'build', '--task', '1']);
  assert.equal(result.code, 0);
  assert.equal(result.json.agent.persistentId, 'builder-native-1');
  assert.equal(run(item, ['dispatch', '--stage', 'build', '--task', '2']).code, 3);
  assert.equal(run(item, ['complete', '--task', '1']).code, 0);
  assert.equal(run(item, ['dispatch', '--stage', 'build', '--task', '2']).code, 0);
  assert.equal(run(item, ['complete', '--task', '2']).code, 0);
});

test('build retry cap requires an explicit user decision', () => {
  const item = makeRun();
  startBuild(item);
  assert.equal(run(item, ['dispatch', '--stage', 'build', '--task', '1']).code, 0);
  for (let retry = 1; retry <= 3; retry++) {
    const result = run(item, ['dispatch', '--stage', 'build', '--retry']);
    assert.equal(result.code, 0);
    assert.equal(result.json.retries, retry);
  }
  const capped = run(item, ['dispatch', '--stage', 'build', '--retry']);
  assert.equal(capped.code, 3);
  assert.match(capped.json.error, /retry cap/);
  assert.equal(run(item, ['complete', '--task', '1', '--user-override']).code, 1);
  const accepted = run(item, [
    'complete', '--task', '1', '--user-override',
    '--user-note', 'Accept this unverified result',
  ]);
  assert.equal(accepted.code, 0);
  assert.equal(accepted.json.acceptedRisk, true);
});

test('fresh code reviewers and full coverage gate completion', () => {
  const item = makeRun();
  reachCodeReview(item);
  let result = run(item, ['prepare-review']);
  assert.equal(result.code, 0, result.stderr || result.stdout);
  assert.equal(existsSync(join(item.cwd, '.forge', 'review-manifest.json')), true);

  result = run(item, ['dispatch', '--stage', 'code']);
  assert.equal(result.code, 0);
  const firstDispatch = result.json;
  assert.equal(firstDispatch.agent.fresh, true);
  assert.equal(run(item, [
    'reviewer-session',
    '--id', 'reviewer-code-1',
    '--dispatch-id', firstDispatch.dispatchId,
  ]).code, 0);
  writeFileSync(
    join(item.cwd, firstDispatch.critiqueFile),
    'No blocking findings.\nCOVERAGE: PARTIAL — one file unread\nVERDICT: APPROVED\n',
  );
  result = run(item, ['verdict', '--stage', 'code', '--file', firstDispatch.critiqueFile]);
  assert.equal(result.code, 2);
  assert.match(result.json.reason, /approval on partial evidence fails closed/);

  result = run(item, ['dispatch', '--stage', 'code', '--retry']);
  assert.equal(result.code, 0);
  const retryDispatch = result.json;
  assert.equal(run(item, [
    'reviewer-session',
    '--id', 'reviewer-code-2',
    '--dispatch-id', retryDispatch.dispatchId,
  ]).code, 0);
  writeFileSync(
    join(item.cwd, retryDispatch.critiqueFile),
    'No blocking findings.\nCOVERAGE: FULL\nVERDICT: APPROVED\n',
  );
  result = run(item, ['verdict', '--stage', 'code', '--file', retryDispatch.critiqueFile]);
  assert.equal(result.code, 0);
  assert.equal(result.json.action, 'approved');
  assert.equal(run(item, ['set', 'phase=done']).code, 0);
});

test('reviewer ids are never reused and mismatch observations warn without strength policy', () => {
  const item = makeRun();
  const current = state(item);
  current.phase = 'code-review';
  writeFileSync(join(item.cwd, '.forge', 'state.json'), JSON.stringify(current, null, 2) + '\n');
  let result = run(item, ['dispatch', '--stage', 'code']);
  assert.equal(result.code, 0);
  const first = result.json;
  result = run(item, [
    'reviewer-session',
    '--id', 'reviewer-reused',
    '--dispatch-id', first.dispatchId,
    '--observed-model', 'different-model',
    '--observed-effort', 'low',
  ]);
  assert.equal(result.code, 0);
  assert.match(result.stderr, /does not match pin/);
  assert.equal(run(item, ['dispatch', '--stage', 'code', '--cancel']).code, 0);

  const second = run(item, ['dispatch', '--stage', 'code']);
  assert.equal(second.code, 0);
  result = run(item, [
    'reviewer-session',
    '--id', 'reviewer-reused',
    '--dispatch-id', second.json.dispatchId,
  ]);
  assert.equal(result.code, 3);
  assert.match(result.json.error, /already used/);
});

test('status reports durable post-materialization progress', () => {
  const item = makeRun();
  startBuild(item);
  assert.equal(run(item, ['dispatch', '--stage', 'build', '--task', '1']).code, 0);
  const result = run(item, ['status']);
  assert.equal(result.code, 0);
  assert.equal(result.json.exists, true);
  assert.equal(result.json.phase, 'build');
  assert.equal(result.json.pendingDispatch.stage, 'build');
  assert.equal(result.json.nextTask.number, 1);
});

test('cleanup preserves owned artifacts and replay ledger by default', () => {
  const item = makeRun();
  mkdirSync(join(item.pluginData, 'nonce-tombstones'), { recursive: true });
  writeFileSync(join(item.pluginData, 'nonce-tombstones', 'nonce.json'), '{}\n');
  const result = run(item, ['cleanup']);
  assert.equal(result.code, 0, result.stderr || result.stdout);
  assert.equal(result.json.removedState, true);
  assert.deepEqual(result.json.keptArtifacts.sort(), ['PLAN-REVIEW-LOG.md', 'PLAN.md']);
  assert.equal(existsSync(join(item.cwd, '.forge')), false);
  assert.equal(existsSync(join(item.cwd, 'PLAN.md')), true);
  assert.equal(existsSync(join(item.cwd, 'PLAN-REVIEW-LOG.md')), true);
  assert.equal(existsSync(join(item.pluginData, 'nonce-tombstones', 'nonce.json')), true);
  assert.doesNotMatch(readFileSync(join(item.cwd, '.git', 'info', 'exclude'), 'utf8'), /\.forge\//);
});

test('cleanup artifact deletion remains explicit and pair-safe', () => {
  const owned = makeRun();
  let result = run(owned, ['cleanup', '--delete-artifacts']);
  assert.equal(result.code, 0);
  assert.deepEqual(result.json.removedArtifacts.sort(), ['PLAN-REVIEW-LOG.md', 'PLAN.md']);
  assert.equal(existsSync(join(owned.cwd, 'PLAN.md')), false);
  assert.equal(existsSync(join(owned.cwd, 'PLAN-REVIEW-LOG.md')), false);

  const foreign = makeRun();
  writeFileSync(join(foreign.cwd, 'PLAN-REVIEW-LOG.md'), '# user file\n');
  result = run(foreign, ['cleanup', '--delete-artifacts']);
  assert.equal(result.code, 0);
  assert.equal(result.json.artifactDeletionBlocked, true);
  assert.equal(existsSync(join(foreign.cwd, 'PLAN.md')), true);
  assert.equal(existsSync(join(foreign.cwd, 'PLAN-REVIEW-LOG.md')), true);
});

test('state-lock recovery resumes crashes after guard creation, unlink, and acquisition', () => {
  for (const crashAt of [
    'after-recovery-guard',
    'after-stale-lock-unlink',
    'after-recovery-lock-acquired',
  ]) {
    const item = makeRun();
    const lockPath = staleStateLock(item);
    const crashed = run(item, ['lock-plan'], { FORGE_STATE_LOCK_CRASH_AT: crashAt });
    assert.equal(crashed.code, 87, `${crashAt}: ${crashed.stderr || crashed.stdout}`);
    assert.equal(existsSync(`${lockPath}.recovery`), true, crashAt);
    const recovered = run(item, ['lock-plan']);
    assert.equal(recovered.code, 0, `${crashAt}: ${recovered.stderr || recovered.stdout}`);
    assert.equal(recovered.json.action, 'plan-locked');
    assert.equal(existsSync(lockPath), false);
    assert.equal(existsSync(`${lockPath}.recovery`), false);
  }
});

test('state-lock recovery rejects primary token replacement under a crashed guard', () => {
  const item = makeRun();
  const lockPath = staleStateLock(item);
  const crashed = run(
    item,
    ['lock-plan'],
    { FORGE_STATE_LOCK_CRASH_AT: 'after-recovery-guard' },
  );
  assert.equal(crashed.code, 87, crashed.stderr || crashed.stdout);
  writeFileSync(lockPath, JSON.stringify({
    pid: 2147483647,
    ts: Date.now() - 60 * 60 * 1000,
    token: 'substituted-token',
  }));
  const rejected = run(item, ['lock-plan']);
  assert.equal(rejected.code, 1);
  assert.match(rejected.json.error, /token or PID changed/);
  assert.equal(existsSync(`${lockPath}.recovery`), true);
  const repeated = run(item, ['lock-plan']);
  assert.equal(repeated.code, 1);
  assert.match(repeated.json.error, /token or PID changed/);
});

test('concurrent state-lock contenders cannot both cross stale recovery', async () => {
  const item = makeRun();
  assert.equal(run(item, ['lock-plan']).code, 0);
  staleStateLock(item);
  const contenders = await Promise.all([
    runAsync(item, ['begin-build']),
    runAsync(item, ['begin-build']),
  ]);
  assert.equal(contenders.filter((result) => result.code === 0).length, 1);
  const loser = contenders.find((result) => result.code !== 0);
  assert.ok(loser);
  assert.match(loser.json?.error ?? loser.stdout, /lock|recovery|requires phase/);
  assert.equal(existsSync(join(item.cwd, '.forge', 'lock.recovery')), false);
});

test('state-lock recovery rotates evidence beyond sixteen crashes and remains concurrent-safe', async () => {
  const item = makeRun();
  const lockPath = staleStateLock(item);
  for (let attempt = 0; attempt < 20; attempt++) {
    const crashed = run(
      item,
      ['lock-plan'],
      { FORGE_STATE_LOCK_CRASH_AT: 'after-recovery-guard' },
    );
    assert.equal(crashed.code, 87, `attempt ${attempt}: ${crashed.stderr || crashed.stdout}`);
  }
  const recovered = run(item, ['lock-plan']);
  assert.equal(recovered.code, 0, recovered.stderr || recovered.stdout);
  assert.equal(existsSync(lockPath), false);
  assert.equal(existsSync(`${lockPath}.recovery`), false);

  staleStateLock(item, 'post-rotation-stale-token');
  const contenders = await Promise.all([
    runAsync(item, ['begin-build']),
    runAsync(item, ['begin-build']),
  ]);
  assert.equal(contenders.filter((result) => result.code === 0).length, 1);
  const loser = contenders.find((result) => result.code !== 0);
  assert.ok(loser);
  assert.match(loser.json?.error ?? loser.stdout, /lock|recovery|requires phase/);
  assert.equal(existsSync(join(item.cwd, '.forge', 'lock.recovery')), false);
});
