import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, readFileSync, existsSync, mkdirSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import {
  parseVerdict,
  parseCoverage,
  validateModelId,
  upsertExcludeBlock,
  removeExcludeBlock,
  parseApproachTasks,
  sha256,
} from './forge.mjs';

const FORGE = fileURLToPath(new URL('./forge.mjs', import.meta.url));
const WORKFLOW = fileURLToPath(new URL('../skills/forge/references/workflow.md', import.meta.url));
const SKILL = fileURLToPath(new URL('../skills/forge/SKILL.md', import.meta.url));
const README = fileURLToPath(new URL('../README.md', import.meta.url));
const MARKETPLACE = fileURLToPath(new URL('../../../.agents/plugins/marketplace.json', import.meta.url));

// ---------------------------------------------------------------------------
// Pure functions

test('parseVerdict: valid verdicts on the last non-empty line', () => {
  assert.equal(parseVerdict('stuff\nVERDICT: APPROVED'), 'APPROVED');
  assert.equal(parseVerdict('stuff\nVERDICT: REVISE\n\n  \n'), 'REVISE');
  assert.equal(parseVerdict('VERDICT:   APPROVED  '), 'APPROVED');
});

test('parseVerdict: decorated, mid-text, or missing verdicts are rejected', () => {
  assert.equal(parseVerdict('**VERDICT: APPROVED**'), null);
  assert.equal(parseVerdict('VERDICT: APPROVED\ntrailing prose'), null);
  assert.equal(parseVerdict('VERDICT: MAYBE'), null);
  assert.equal(parseVerdict(''), null);
  assert.equal(parseVerdict('   \n \n'), null);
});

test('parseCoverage: exactly one line, before the verdict', () => {
  assert.equal(parseCoverage('body\nCOVERAGE: FULL\nVERDICT: APPROVED'), 'FULL');
  assert.equal(parseCoverage('COVERAGE: PARTIAL — patch truncated\nVERDICT: REVISE'), 'PARTIAL');
  assert.equal(parseCoverage('no coverage line\nVERDICT: APPROVED'), null);
  assert.equal(parseCoverage('COVERAGE: FULL\nCOVERAGE: FULL\nVERDICT: APPROVED'), null);
  // coverage after the verdict line breaks the contract
  assert.equal(parseCoverage('VERDICT: APPROVED\nCOVERAGE: FULL'), null);
});

test('Act 4 review contract requires performance analysis and uses the .NET skill when available', () => {
  const workflow = readFileSync(WORKFLOW, 'utf8');
  assert.match(workflow, /Performance review is required/);
  assert.match(workflow, /`Analyzing Dotnet Performance` skill is available/);
  assert.match(workflow, /speculative micro-optimizations/i);
});

test('sign-off contract requires explicit confirmation before Act 3', () => {
  const workflow = readFileSync(WORKFLOW, 'utf8');
  assert.match(workflow, /## Sign-off — final user gate before code/);
  assert.match(workflow, /Only an explicit affirmative answer grants sign-off/);
  assert.match(workflow, /confirm-signoff --user-note/);
});

test('Act 3 verification is an orchestrator-owned procedural gate with concrete fallbacks', () => {
  const workflow = readFileSync(WORKFLOW, 'utf8');
  assert.match(workflow, /Verification is the orchestrator's responsibility/);
  assert.match(workflow, /`forge\.mjs` does not run compilation, typechecking, or tests/);
  assert.match(workflow, /every verification command or check listed in[\s\S]*locked plan step/);
  assert.match(workflow, /applicable build and\/or\s+typecheck plus relevant tests/);
  assert.match(workflow, /append the check, the reason it could not run,[\s\S]*`PLAN-REVIEW-LOG\.md`/);
  assert.match(workflow, /do not count[\s\S]*as successful/);
});

test('hard rules prohibit bypasses and require a three-way user decision at caps', () => {
  const skill = readFileSync(SKILL, 'utf8');
  const workflow = readFileSync(WORKFLOW, 'utf8');
  assert.match(skill, /## Hard rules/);
  assert.match(skill, /## What NOT to do/);
  assert.match(skill, /authorize exactly one additional\s+round, accept risk and advance, or stop/i);
  assert.match(skill, /nested `codex exec`/);
  assert.match(workflow, /Stop the workflow: leave state unchanged/);
  assert.match(workflow, /Accept the unverified result and advance/);
});

test('README presents the plugin through the requested six-section structure', () => {
  const readme = readFileSync(README, 'utf8');
  assert.match(readme, /## 1\. Description and goal/);
  assert.match(readme, /## 2\. Workflow/);
  assert.match(readme, /```mermaid\s+flowchart TD[\s\S]*Act 1[\s\S]*Act 4[\s\S]*```/);
  assert.doesNotMatch(readme, /### Responsibilities/);
  assert.match(readme, /## 3\. Requirements/);
  assert.match(readme, /## 4\. Installation/);
  assert.match(readme, /codex plugin marketplace add https:\/\/github\.com\/dlin10\/CodexPlugins\.git/);
  assert.match(readme, /plan-forge-flow@dlin10-codex-plugins/);
  assert.match(readme, /## 5\. Usage/);
  assert.match(readme, /### Start a new workflow[\s\S]*Use \$forge to plan/);
  assert.match(readme, /### Resume an existing workflow[\s\S]*Use \$forge to resume/);
  assert.doesNotMatch(readme, /### Setup and discovery/);
  assert.match(readme, /## 6\. Attribution/);
});

test('repository marketplace uses the distributable GitHub marketplace identity', () => {
  const marketplace = JSON.parse(readFileSync(MARKETPLACE, 'utf8'));
  assert.equal(marketplace.name, 'dlin10-codex-plugins');
  assert.equal(marketplace.interface.displayName, 'dlin10 Codex Plugins');
  assert.equal(marketplace.plugins[0].name, 'plan-forge-flow');
});

test('model slug grammar: accepts real ids, rejects injection', () => {
  assert.equal(validateModelId('inherit'), true);
  assert.equal(validateModelId('claude-opus-4-8[effort=high]'), true);
  assert.equal(validateModelId('composer-2.5[fast=false,effort=high]'), true);
  assert.equal(validateModelId('gpt-5.5'), true);
  assert.equal(validateModelId('bad model'), false);
  assert.equal(validateModelId('a\nreadonly: false'), false);
  assert.equal(validateModelId('---'), false);
  assert.equal(validateModelId(''), false);
});

test('upsertExcludeBlock: idempotent and preserves user lines', () => {
  const dir = mkdtempSync(join(tmpdir(), 'forge-ex-'));
  const p = join(dir, 'exclude');
  writeFileSync(p, '# user line\nnode_modules/\n');
  upsertExcludeBlock(p);
  const once = readFileSync(p, 'utf8');
  upsertExcludeBlock(p);
  upsertExcludeBlock(p);
  assert.equal(readFileSync(p, 'utf8'), once);
  assert.match(once, /# user line\nnode_modules\//);
  assert.match(once, /\.forge\//);
  removeExcludeBlock(p);
  const after = readFileSync(p, 'utf8');
  assert.match(after, /node_modules\//);
  assert.doesNotMatch(after, /plan-forge-flow/);
});

test('parseApproachTasks: numbered steps with bodies, sequential enforcement', () => {
  const plan = `# Plan: x\n\n## Goal\ng\n\n## Approach\n1. First\n   detail line\n2. Second\n\n## Key decisions & tradeoffs\n- k\n`;
  const tasks = parseApproachTasks(plan);
  assert.equal(tasks.length, 2);
  assert.equal(tasks[0].number, 1);
  assert.equal(tasks[0].title, 'First');
  assert.notEqual(tasks[0].hash, tasks[1].hash);
  assert.throws(() => parseApproachTasks('# P\n## Approach\n2. skipped one\n'), /numbered 1\.\.N/);
  assert.throws(() => parseApproachTasks('# P\n## Goal\nno approach\n'), /no "## Approach"/);
  assert.throws(() => parseApproachTasks('# P\n## Approach\nprose only\n'), /no numbered steps/);
});

// ---------------------------------------------------------------------------
// CLI lifecycle (child process, temp git repos)

const gitEnvDir = mkdtempSync(join(tmpdir(), 'forge-gitenv-'));
writeFileSync(join(gitEnvDir, 'gitconfig'), '[user]\n\tname = Forge Test\n\temail = forge@test.local\n');
// Point the global agents dir at an empty temp dir so tests never read or write
// the real ~/.codex/agents; tests that exercise install/cleanup override it.
const EMPTY_AGENTS_DIR = mkdtempSync(join(tmpdir(), 'forge-empty-agents-'));
const BASE_ENV = {
  ...process.env,
  GIT_CONFIG_GLOBAL: join(gitEnvDir, 'gitconfig'),
  GIT_CONFIG_SYSTEM: join(gitEnvDir, 'empty'),
  FORGE_AGENTS_DIR: EMPTY_AGENTS_DIR,
  FORGE_PLUGIN_DATA: mkdtempSync(join(tmpdir(), 'forge-plugin-data-')),
};

function rawRun(cwd, args, env = {}) {
  const res = spawnSync(process.execPath, [FORGE, ...args], {
    cwd,
    encoding: 'utf8',
    env: { ...BASE_ENV, ...env },
  });
  const lines = res.stdout.trim().split('\n').filter(Boolean);
  let json = null;
  try {
    json = JSON.parse(lines[lines.length - 1] ?? 'null');
  } catch {
    /* non-JSON output is a test failure surfaced via assertions */
  }
  return { code: res.status, json, stderr: res.stderr };
}

function testCatalog() {
  const observedAt = new Date().toISOString();
  const f = (value, source = 'test') => ({ value, known: true, source, observedAt, stale: false });
  return {
    version: 1,
    generatedAt: observedAt,
    activeModel: f('gpt-5.6-sol'),
    activeEffort: f('high'),
    degraded: false,
    trace: [{ provider: 'test', status: 'ok' }],
    models: [
      {
        slug: 'gpt-5.6-sol',
        description: f('test'),
        supportedEfforts: f(['low', 'medium', 'high', 'xhigh', 'max']),
        priority: f(1),
        visibility: f('visible'),
        spawnAvailable: f(true, 'native-schema'),
      },
      {
        slug: 'gpt-5.6-terra',
        description: f('test'),
        supportedEfforts: f(['low', 'medium', 'high', 'xhigh', 'max']),
        priority: f(2),
        visibility: f('visible'),
        spawnAvailable: f(true, 'native-schema'),
      },
    ],
  };
}

function configureTestReviewer(cwd, env = {}) {
  const catalogPath = join(cwd, '.forge', 'test-catalog.json');
  writeFileSync(catalogPath, JSON.stringify(testCatalog()));
  return rawRun(cwd, [
    'configure-reviewer',
    '--catalog', catalogPath,
    '--reviewer-model', 'gpt-5.6-sol',
    '--reviewer-effort', 'high',
  ], env);
}

function configureTestBuilder(cwd, env = {}) {
  const catalogPath = join(cwd, '.forge', 'test-catalog.json');
  writeFileSync(catalogPath, JSON.stringify(testCatalog()));
  return rawRun(cwd, [
    'configure-builder',
    '--catalog', catalogPath,
    '--builder-model', 'gpt-5.6-terra',
    '--builder-effort', 'medium',
  ], env);
}

let reviewerSequence = 0;
function run(cwd, args, env = {}) {
  const result = rawRun(cwd, args, env);
  if (args[0] === 'set' && args.includes('phase=review') && result.code === 0) {
    const status = rawRun(cwd, ['status'], env);
    if (!status.json?.modelSelection) {
      const configured = configureTestReviewer(cwd, env);
      assert.equal(configured.code, 0, configured.json?.error ?? configured.stderr);
    }
  }
  if (args[0] === 'begin-build') {
    const status = rawRun(cwd, ['status'], env);
    if (!status.json?.modelSelection?.builder) {
      const configured = configureTestBuilder(cwd, env);
      assert.equal(configured.code, 0, configured.json?.error ?? configured.stderr);
      return rawRun(cwd, args, env);
    }
  }
  if (
    args[0] === 'dispatch' &&
    result.code === 0 &&
    result.json?.dispatchId &&
    (result.json.stage === 'plan' || result.json.stage === 'code')
  ) {
    reviewerSequence += 1;
    const registered = rawRun(cwd, [
      'reviewer-session',
      '--id', `test-reviewer-${reviewerSequence}`,
      '--dispatch-id', result.json.dispatchId,
    ], env);
    assert.equal(registered.code, 0, registered.json?.error ?? registered.stderr);
  }
  return result;
}

function sh(cwd, cmd, args) {
  const res = spawnSync(cmd, args, { cwd, encoding: 'utf8', env: BASE_ENV });
  assert.equal(res.status, 0, `${cmd} ${args.join(' ')} failed: ${res.stderr}`);
  return res.stdout.trim();
}

function makeRepo() {
  const dir = mkdtempSync(join(tmpdir(), 'forge-repo-'));
  sh(dir, 'git', ['init', '-q']);
  writeFileSync(join(dir, 'app.txt'), 'v1\n');
  sh(dir, 'git', ['add', '.']);
  sh(dir, 'git', ['commit', '-q', '-m', 'init']);
  return dir;
}

const PLAN_BODY = `<!-- plan-forge-flow:owned -->
# Plan: demo

## Goal
Demo goal.

## Approach
1. First step
   with detail
2. Second step

## Key decisions & tradeoffs
- none

## Risks / open questions

## Out of scope
`;

function writeCritique(dir, file, text) {
  const p = join(dir, file);
  mkdirSync(join(dir, '.forge', 'critiques'), { recursive: true });
  writeFileSync(p, text);
  return file;
}

test('init: requires git, refuses re-init, --force archives owned files only', () => {
  const noGit = mkdtempSync(join(tmpdir(), 'forge-nogit-'));
  assert.equal(run(noGit, ['init']).code, 1);

  const dir = makeRepo();
  assert.equal(run(dir, ['init', '--rounds', '6']).code, 1);
  let r = run(dir, ['init', '--rounds', '2']);
  assert.equal(r.code, 0);
  assert.equal(r.json.action, 'initialized');
  assert.equal(r.json.maxRounds, 2);
  assert.ok(existsSync(join(dir, 'PLAN.md')));
  assert.ok(existsSync(join(dir, '.forge', 'state.json')));

  r = run(dir, ['init']);
  assert.equal(r.code, 3);
  assert.equal(r.json.action, 'state-exists');

  r = run(dir, ['init', '--force', '--rounds', '4']);
  assert.equal(r.code, 0);
  const archives = readFileSync(join(dir, '.forge', 'state.json'), 'utf8');
  assert.match(archives, /"maxRounds": 4/);

  // a foreign (unmarked) PLAN.md is never touched, even with --force
  const dir2 = makeRepo();
  writeFileSync(join(dir2, 'PLAN.md'), '# my own plan\n');
  r = run(dir2, ['init']);
  assert.equal(r.code, 3);
  r = run(dir2, ['init', '--force']);
  assert.equal(r.code, 3);
  assert.equal(readFileSync(join(dir2, 'PLAN.md'), 'utf8'), '# my own plan\n');
});

test('init: git-excludes .forge via the managed block', () => {
  const dir = makeRepo();
  assert.equal(run(dir, ['init']).code, 0);
  const status = sh(dir, 'git', ['status', '--porcelain']);
  assert.doesNotMatch(status, /\.forge/);
  const exclude = readFileSync(join(dir, '.git', 'info', 'exclude'), 'utf8');
  assert.match(exclude, /plan-forge-flow \(managed\)/);
});

test('init: linked worktree resolves the exclude file via --git-path', () => {
  const dir = makeRepo();
  const wt = join(dir, '..', `forge-wt-${Date.now()}`);
  sh(dir, 'git', ['worktree', 'add', '-q', wt, 'HEAD']);
  assert.equal(run(wt, ['init']).code, 0);
  const excludePath = sh(wt, 'git', ['rev-parse', '--git-path', 'info/exclude']);
  const resolved = excludePath.startsWith('/') || /^[A-Za-z]:/.test(excludePath) ? excludePath : join(wt, excludePath);
  assert.match(readFileSync(resolved, 'utf8'), /plan-forge-flow \(managed\)/);
});

test('install-agents: idempotent global install, reload flag, foreign files preserved', () => {
  const dir = makeRepo();
  const agentsDir = mkdtempSync(join(tmpdir(), 'forge-agents-'));
  const env = { FORGE_AGENTS_DIR: agentsDir };

  // first install writes both static, model-free files and asks for a reload
  let r = run(dir, ['install-agents'], env);
  assert.equal(r.code, 0);
  assert.equal(r.json.results.reviewer, 'installed');
  assert.equal(r.json.results.builder, 'installed');
  assert.equal(r.json.reloadRequired, true);
  const reviewer = readFileSync(join(agentsDir, 'forge_reviewer.toml'), 'utf8');
  assert.doesNotMatch(reviewer, /^model\s*=/m);
  assert.doesNotMatch(reviewer, /^model_reasoning_effort\s*=/m);
  assert.match(reviewer, /sandbox_mode = "read-only"/);
  assert.match(reviewer, /actively try to falsify/i);
  assert.match(reviewer, /Do not\s+manufacture objections/i);
  assert.match(reviewer, /material performance\s+regressions/i);
  assert.match(reviewer, /Do not edit files, create commits, stage changes/i);
  assert.doesNotMatch(reviewer, /\{\{MODEL\}\}/);
  const builder = readFileSync(join(agentsDir, 'forge_builder.toml'), 'utf8');
  assert.match(builder, /Do not create commits or stage changes/i);

  // re-running is a no-op — nothing changed, no reload needed
  r = run(dir, ['install-agents'], env);
  assert.equal(r.json.results.reviewer, 'unchanged');
  assert.equal(r.json.results.builder, 'unchanged');
  assert.equal(r.json.reloadRequired, false);

  // a stale generated file is refreshed and re-asks for a reload
  writeFileSync(join(agentsDir, 'forge_builder.toml'), '# plan-forge-flow:generated\nold\n');
  r = run(dir, ['install-agents'], env);
  assert.equal(r.json.results.builder, 'updated');
  assert.equal(r.json.reloadRequired, true);

  // a hand-authored file (no generated marker) is never overwritten
  writeFileSync(join(agentsDir, 'forge_reviewer.toml'), 'name = "forge_reviewer"\n# mine\n');
  r = run(dir, ['install-agents'], env);
  assert.equal(r.json.results.reviewer, 'foreign');
  assert.deepEqual(r.json.foreign, ['reviewer']);
  assert.equal(readFileSync(join(agentsDir, 'forge_reviewer.toml'), 'utf8'), 'name = "forge_reviewer"\n# mine\n');
});

test('configure-reviewer: runs in Act 2 and enforces strength, effort order, and pinning', () => {
  const dir = makeRepo();
  run(dir, ['init']);
  const catalogPath = join(dir, '.forge', 'test-catalog.json');
  writeFileSync(catalogPath, JSON.stringify(testCatalog()));
  let r = rawRun(dir, [
    'configure-reviewer', '--catalog', catalogPath,
    '--reviewer-model', 'gpt-5.6-sol', '--reviewer-effort', 'high',
  ]);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /beginning of Act 2/);
  run(dir, ['set', 'phase=review']);
  let status = run(dir, ['status']);
  assert.equal(status.json.modelSelection.orchestrator.model, 'gpt-5.6-sol');
  assert.equal(status.json.modelSelection.orchestrator.effort, 'high');
  assert.equal(status.json.modelSelection.orchestrator.usesCurrentSession, true);
  assert.equal(status.json.modelSelection.builder, null);
  r = rawRun(dir, [
    'configure-reviewer', '--catalog', catalogPath,
    '--orchestrator-model', 'gpt-5.6-terra',
    '--reviewer-model', 'gpt-5.6-sol', '--reviewer-effort', 'high',
  ]);
  assert.equal(r.code, 1);
  assert.match(r.json.error, /current Codex session/);
  r = rawRun(dir, [
    'configure-reviewer', '--catalog', catalogPath,
    '--reviewer-model', 'gpt-5.6-terra', '--reviewer-effort', 'high',
    '--user-override', '--user-note', 'try weaker reviewer',
  ]);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /at least as strong/);

  r = rawRun(dir, [
    'configure-reviewer', '--catalog', catalogPath,
    '--reviewer-model', 'gpt-5.6-sol', '--reviewer-effort', 'medium',
    '--user-override', '--user-note', 'try lower effort',
  ]);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /must not be lower/);

  r = rawRun(dir, [
    'configure-reviewer', '--catalog', catalogPath,
    '--reviewer-model', 'gpt-5.6-sol', '--reviewer-effort', 'xhigh',
  ]);
  assert.equal(r.code, 3, 'changing a pinned selection requires a user reason');
  r = rawRun(dir, [
    'configure-reviewer', '--catalog', catalogPath,
    '--reviewer-model', 'gpt-5.6-sol', '--reviewer-effort', 'xhigh',
    '--user-override', '--user-note', 'user requested stronger review',
  ]);
  assert.equal(r.code, 0);
});

test('models: degrades gracefully when CLI and session catalog are unavailable', () => {
  const dir = makeRepo();
  const r = rawRun(dir, ['models'], {
    PATH: '',
    FORGE_CODEX_PATH: join(dir, 'missing-codex'),
  });
  assert.equal(r.code, 0);
  assert.equal(r.json.available, false);
  assert.equal(r.json.catalog.degraded, true);
});

/** Drive a repo to an approved plan waiting at the user sign-off gate. */
function repoAwaitingSignoff() {
  const dir = makeRepo();
  run(dir, ['init', '--rounds', '5']);
  writeFileSync(join(dir, 'PLAN.md'), PLAN_BODY);
  assert.equal(run(dir, ['set', 'phase=review']).code, 0);
  let r = run(dir, ['dispatch', '--stage', 'plan']);
  assert.equal(r.code, 0);
  writeCritique(dir, r.json.critiqueFile, 'Looks solid.\n\nVERDICT: APPROVED\n');
  r = run(dir, ['verdict', '--file', r.json.critiqueFile, '--stage', 'plan']);
  assert.equal(r.json.action, 'approved');
  assert.equal(run(dir, ['set', 'phase=signoff']).code, 0);
  return dir;
}

/** Drive a repo to an explicitly signed-off, locked, build-ready state. */
function repoAtSignoff() {
  const dir = repoAwaitingSignoff();
  let r = run(dir, ['confirm-signoff', '--user-note', 'Yes, build this reviewed plan']);
  assert.equal(r.code, 0);
  r = run(dir, ['lock-plan']);
  assert.equal(r.code, 0);
  assert.equal(r.json.taskCount, 2);
  return dir;
}

test('review loop: dispatch/verdict pairing, replay guard, hash drift, deadlock, user-note gate', () => {
  const dir = makeRepo();
  run(dir, ['init', '--rounds', '2']);
  writeFileSync(join(dir, 'PLAN.md'), PLAN_BODY);

  // verdict without a dispatch is refused
  writeCritique(dir, '.forge/critiques/stray.md', 'x\nVERDICT: APPROVED\n');
  assert.equal(run(dir, ['set', 'phase=review']).code, 0);
  let r = run(dir, ['verdict', '--file', '.forge/critiques/stray.md', '--stage', 'plan']);
  assert.equal(r.code, 3);

  // round 1: REVISE
  r = run(dir, ['dispatch', '--stage', 'plan']);
  const f1 = writeCritique(dir, r.json.critiqueFile, 'Flaw A.\n\nVERDICT: REVISE\n');
  r = run(dir, ['verdict', '--file', f1, '--stage', 'plan']);
  assert.equal(r.json.action, 'revise');
  assert.equal(r.json.round, 1);

  // plan-hash drift: dispatch, then edit the plan, then verdict → stale
  r = run(dir, ['dispatch', '--stage', 'plan']);
  writeFileSync(join(dir, 'PLAN.md'), PLAN_BODY + '\nrevised after round 1\n');
  const f2 = writeCritique(dir, r.json.critiqueFile, 'Flaw B.\n\nVERDICT: REVISE\n');
  r = run(dir, ['verdict', '--file', f2, '--stage', 'plan']);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /stale/);

  // a stale pending dispatch blocks fresh dispatches until cancelled
  assert.equal(run(dir, ['dispatch', '--stage', 'plan']).code, 3);
  assert.equal(run(dir, ['dispatch', '--stage', 'plan', '--cancel']).code, 0);
  r = run(dir, ['dispatch', '--stage', 'plan']);
  assert.equal(r.code, 0);
  const f3 = writeCritique(dir, r.json.critiqueFile, 'Flaw B.\n\nVERDICT: REVISE\n');
  r = run(dir, ['verdict', '--file', f3, '--stage', 'plan']);
  assert.equal(r.json.action, 'deadlock', 'REVISE at round 2 of 2 is a deadlock');

  // extending the cap is a sensitive mutation → requires --user-note
  r = run(dir, ['set', 'maxRounds=3', 'deadlock=false']);
  assert.equal(r.code, 1);
  r = run(dir, ['set', 'maxRounds=3', 'deadlock=false', '--user-note', 'user granted a round']);
  assert.equal(r.code, 0);

  // replay guard: identical critique for the SAME plan revision is rejected...
  r = run(dir, ['dispatch', '--stage', 'plan']);
  const f4 = writeCritique(dir, r.json.critiqueFile, 'Flaw B.\n\nVERDICT: REVISE\n');
  r = run(dir, ['verdict', '--file', f4, '--stage', 'plan']);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /replay/);

  // ...but the same text is legal after the plan changes (key includes plan hash)
  run(dir, ['dispatch', '--stage', 'plan', '--cancel']);
  writeFileSync(join(dir, 'PLAN.md'), PLAN_BODY + '\nrevised again\n');
  r = run(dir, ['dispatch', '--stage', 'plan']);
  assert.equal(r.code, 0);
  const f5 = writeCritique(dir, r.json.critiqueFile, 'Flaw B.\n\nVERDICT: REVISE\n');
  r = run(dir, ['verdict', '--file', f5, '--stage', 'plan']);
  assert.equal(r.code, 0, 'same critique text must be accepted for a different plan hash');
  assert.equal(r.json.action, 'deadlock');

  // caps are soft gates: after each reached cap the user can add exactly one round,
  // including beyond the default cap of five
  for (const nextCap of [4, 5, 6]) {
    r = run(dir, [`set`, `maxRounds=${nextCap}`, '--user-note', `user granted round ${nextCap}`]);
    assert.equal(r.code, 0);
    assert.equal(r.json.maxRounds, nextCap);
    if (nextCap === 6) break;
    writeFileSync(join(dir, 'PLAN.md'), `${PLAN_BODY}\nrevision for round ${nextCap}\n`);
    r = run(dir, ['dispatch', '--stage', 'plan']);
    const critique = writeCritique(
      dir,
      r.json.critiqueFile,
      `Distinct finding at round ${nextCap}.\n\nVERDICT: REVISE\n`,
    );
    r = run(dir, ['verdict', '--file', critique, '--stage', 'plan']);
    assert.equal(r.json.action, 'deadlock');
  }
});

test('invalid verdicts: exit 2, no mutation, retry once', () => {
  const dir = makeRepo();
  run(dir, ['init']);
  writeFileSync(join(dir, 'PLAN.md'), PLAN_BODY);
  run(dir, ['set', 'phase=review']);
  let r = run(dir, ['dispatch', '--stage', 'plan']);
  const f = writeCritique(dir, r.json.critiqueFile, 'stuff\n**VERDICT: APPROVED**\n');
  r = run(dir, ['verdict', '--file', f, '--stage', 'plan']);
  assert.equal(r.code, 2);
  assert.equal(r.json.action, 'invalid-verdict');

  // pending dispatch survived; the initial format retry cap is one
  r = run(dir, ['dispatch', '--stage', 'plan', '--retry']);
  assert.equal(r.code, 0);
  assert.equal(r.json.retries, 1);
  const retryFile = writeCritique(dir, r.json.critiqueFile, 'still malformed\n');
  r = run(dir, ['verdict', '--file', retryFile, '--stage', 'plan']);
  assert.equal(r.code, 2);
  r = run(dir, ['dispatch', '--stage', 'plan', '--retry']);
  assert.equal(r.code, 3);

  // at the reached cap only an explicit one-round extension is accepted
  r = run(dir, ['set', 'maxVerdictRetries=2']);
  assert.equal(r.code, 1);
  r = run(dir, [
    'set', 'maxVerdictRetries=2', '--user-note', 'user wants one more format retry',
  ]);
  assert.equal(r.code, 0);
  r = run(dir, ['dispatch', '--stage', 'plan', '--retry']);
  assert.equal(r.code, 0);
  assert.equal(r.json.retries, 2);
  assert.equal(r.json.retryCap, 2);
});

test('reviewer-session: each plan/code dispatch requires a never-reused native agent id', () => {
  const dir = makeRepo();
  run(dir, ['init']);
  writeFileSync(join(dir, 'PLAN.md'), PLAN_BODY);
  run(dir, ['set', 'phase=review']);
  let dispatched = rawRun(dir, ['dispatch', '--stage', 'plan']);
  assert.equal(dispatched.code, 0);
  let r = rawRun(dir, [
    'reviewer-session', '--id', 'reviewer-a', '--dispatch-id', dispatched.json.dispatchId,
  ]);
  assert.equal(r.code, 0);
  run(dir, ['dispatch', '--stage', 'plan', '--cancel']);
  dispatched = rawRun(dir, ['dispatch', '--stage', 'plan']);
  r = rawRun(dir, [
    'reviewer-session', '--id', 'reviewer-a', '--dispatch-id', dispatched.json.dispatchId,
  ]);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /already used/);
});

test('transition table: guarded edges are enforced', () => {
  const dir = makeRepo();
  run(dir, ['init']);
  writeFileSync(join(dir, 'PLAN.md'), PLAN_BODY);
  // review → signoff without an approved verdict
  run(dir, ['set', 'phase=review']);
  let r = run(dir, ['set', 'phase=signoff']);
  assert.equal(r.code, 3);
  // grill-time jumps
  r = run(dir, ['set', 'phase=done']);
  assert.equal(r.code, 3);
  // user-override needs a note, then wins
  r = run(dir, ['set', 'phase=signoff', '--user-override']);
  assert.equal(r.code, 1);
  r = run(dir, ['set', 'phase=signoff', '--user-override', '--user-note', 'deadlock signoff']);
  assert.equal(r.code, 0);
  // signoff → build must go through begin-build
  r = run(dir, ['set', 'phase=build']);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /begin-build/);
});

test('explicit user sign-off is persisted, hash-bound, and gates Act 3', () => {
  const dir = repoAwaitingSignoff();

  let r = rawRun(dir, ['confirm-signoff']);
  assert.equal(r.code, 1);
  assert.match(r.json.error, /exact affirmative words/);

  r = configureTestBuilder(dir);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /explicit user sign-off/);

  r = rawRun(dir, ['lock-plan']);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /explicit user sign-off/);

  r = rawRun(dir, ['begin-build']);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /explicit user sign-off/);

  r = rawRun(dir, ['confirm-signoff', '--user-note', 'Yes, build it']);
  assert.equal(r.code, 0);
  assert.equal(r.json.action, 'user-signoff-confirmed');
  assert.equal(r.json.userSignoff.userNote, 'Yes, build it');
  assert.equal(r.json.userSignoff.planHash, run(dir, ['status']).json.userSignoff.planHash);

  writeFileSync(join(dir, 'PLAN.md'), PLAN_BODY.replace('Second step', 'Revised second step'));
  r = configureTestBuilder(dir);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /changed after user sign-off/);
  r = rawRun(dir, ['lock-plan']);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /changed after user sign-off/);

  r = rawRun(dir, ['confirm-signoff', '--user-note', 'Yes, build the revised plan']);
  assert.equal(r.code, 0);
  assert.equal(configureTestBuilder(dir).code, 0);
  assert.equal(rawRun(dir, ['lock-plan']).code, 0);
});

test('configure-builder: Act 3 asks for and pins builder model/effort before begin-build', () => {
  const dir = repoAtSignoff();
  let r = rawRun(dir, ['begin-build']);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /ask the user for the builder model\/effort/);

  r = rawRun(dir, ['configure-builder']);
  assert.equal(r.code, 1);
  r = configureTestBuilder(dir);
  assert.equal(r.code, 0);
  assert.equal(r.json.action, 'builder-configured');
  assert.deepEqual(r.json.selection.builder, { model: 'gpt-5.6-terra', effort: 'medium' });

  const catalogPath = join(dir, '.forge', 'test-catalog.json');
  r = rawRun(dir, [
    'configure-builder', '--catalog', catalogPath,
    '--builder-model', 'gpt-5.6-sol', '--builder-effort', 'low',
  ]);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /builder choice is pinned/);
});

test('begin-build: pins initial HEAD and a tracked working-tree snapshot', () => {
  // dirty tracked
  const dir = repoAtSignoff();
  writeFileSync(join(dir, 'app.txt'), 'v2 uncommitted\n');
  let r = run(dir, ['begin-build']);
  assert.equal(r.code, 0);
  assert.equal(r.json.snapshotted, true);
  const head = sh(dir, 'git', ['rev-parse', 'HEAD']);
  assert.equal(r.json.headBaseSha, head);
  assert.notEqual(r.json.worktreeBaseSha, head);
  assert.equal(sh(dir, 'git', ['rev-parse', 'refs/plan-forge/head-base']), head);
  assert.equal(sh(dir, 'git', ['rev-parse', 'refs/plan-forge/worktree-base']), r.json.worktreeBaseSha);

  // untracked-only dirt
  const dir2 = repoAtSignoff();
  writeFileSync(join(dir2, 'notes.txt'), 'pre-existing untracked\n');
  r = run(dir2, ['begin-build']);
  assert.equal(r.code, 0);
  assert.equal(r.json.snapshotted, false);
  assert.equal(r.json.headBaseSha, sh(dir2, 'git', ['rev-parse', 'HEAD']));
  assert.equal(r.json.worktreeBaseSha, r.json.headBaseSha);
  // PLAN.md and the log are untracked workflow artifacts — they belong in the
  // baseline too (Act 4 must never treat them as build output).
  assert.deepEqual(r.json.untrackedBaseline, ['PLAN-REVIEW-LOG.md', 'PLAN.md', 'notes.txt']);
});

test('build lifecycle: retry cap pauses for user extension or risk acceptance', () => {
  const dir = repoAtSignoff();
  run(dir, ['begin-build']);

  // caps cannot be raised preemptively
  let r = run(dir, [
    'set', 'maxBuildRetries=4', '--user-note', 'preemptive extension should fail',
  ]);
  assert.equal(r.code, 3);

  // wrong task number refused
  r = run(dir, ['dispatch', '--stage', 'build', '--task', '2']);
  assert.equal(r.code, 3);

  r = run(dir, ['dispatch', '--stage', 'build', '--task', '1']);
  assert.equal(r.code, 0);
  // task 2 cannot dispatch while task 1 is pending
  assert.equal(run(dir, ['dispatch', '--stage', 'build', '--task', '2']).code, 3);
  // complete for the wrong task refused
  assert.equal(run(dir, ['complete', '--task', '2']).code, 3);
  r = run(dir, ['complete', '--task', '1']);
  assert.equal(r.code, 0);
  assert.equal(r.json.taskIndex, 1);
  assert.equal(r.json.nextTask, 2);

  // conflict path: dispatch task 2, resolve without advancing
  r = run(dir, ['dispatch', '--stage', 'build', '--task', '2']);
  assert.equal(r.code, 0);
  r = run(dir, ['resolve-build', '--conflict']);
  assert.equal(r.code, 0);
  assert.equal(r.json.taskIndex, 1, 'conflict must not advance taskIndex');

  // same task re-dispatches; the initial retry cap is 3 for build
  r = run(dir, ['dispatch', '--stage', 'build', '--task', '2']);
  assert.equal(run(dir, ['dispatch', '--stage', 'build', '--retry']).code, 0);
  assert.equal(run(dir, ['dispatch', '--stage', 'build', '--retry']).code, 0);
  assert.equal(run(dir, ['dispatch', '--stage', 'build', '--retry']).code, 0);
  assert.equal(run(dir, ['dispatch', '--stage', 'build', '--retry']).code, 3);
  assert.equal(run(dir, ['set', 'maxBuildRetries=4']).code, 1);
  r = run(dir, [
    'set', 'maxBuildRetries=4', '--user-note', 'user authorizes one more verification retry',
  ]);
  assert.equal(r.code, 0);
  r = run(dir, ['dispatch', '--stage', 'build', '--retry']);
  assert.equal(r.code, 0);
  assert.equal(r.json.retries, 4);
  assert.equal(run(dir, ['dispatch', '--stage', 'build', '--retry']).code, 3);

  assert.equal(run(dir, ['complete', '--task', '2', '--user-override']).code, 1);
  r = run(dir, [
    'complete', '--task', '2', '--user-override',
    '--user-note', 'user accepts the unverified task result',
  ]);
  assert.equal(r.code, 0);
  assert.equal(r.json.acceptedRisk, true);

  // all tasks done → code-review is now a legal transition
  r = run(dir, ['set', 'phase=code-review']);
  assert.equal(r.code, 0);
});

test('build → code-review is blocked until all tasks complete', () => {
  const dir = repoAtSignoff();
  run(dir, ['begin-build']);
  const r = run(dir, ['set', 'phase=code-review']);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /all tasks complete/);
});

test('fingerprint: content edits to already-modified files change it', () => {
  const dir = repoAtSignoff();
  writeFileSync(join(dir, 'app.txt'), 'already modified\n');
  run(dir, ['begin-build']);
  run(dir, ['dispatch', '--stage', 'build', '--task', '1']);
  let r = run(dir, ['status']);
  assert.equal(r.json.pendingFingerprintMatches, true);
  // same git-status codes, different content
  writeFileSync(join(dir, 'app.txt'), 'modified differently\n');
  r = run(dir, ['status']);
  assert.equal(r.json.pendingFingerprintMatches, false);
});

test('amendment loop: build→review preserves position; relock protects completed tasks', () => {
  const dir = repoAtSignoff();
  run(dir, ['begin-build']);
  run(dir, ['dispatch', '--stage', 'build', '--task', '1']);
  run(dir, ['complete', '--task', '1']);

  let r = run(dir, ['set', 'phase=review', '--amendment']);
  assert.equal(r.code, 0);
  assert.equal(r.json.amendmentReview, true);

  // relock with completed task 1 changed → refused
  writeFileSync(join(dir, 'PLAN.md'), PLAN_BODY.replace('1. First step', '1. First step CHANGED'));
  r = run(dir, ['lock-plan', '--relock']);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /immutable/);

  // relock with only task 2 changed → ok
  writeFileSync(join(dir, 'PLAN.md'), PLAN_BODY.replace('2. Second step', '2. Second step, amended'));
  r = run(dir, ['lock-plan', '--relock']);
  assert.equal(r.code, 0);

  // amendment review round, then return to build with position preserved
  r = run(dir, ['dispatch', '--stage', 'plan']);
  const f = writeCritique(dir, r.json.critiqueFile, 'Amendment fine.\n\nVERDICT: APPROVED\n');
  run(dir, ['verdict', '--file', f, '--stage', 'plan']);
  r = run(dir, ['set', 'phase=build']);
  assert.equal(r.code, 0);
  r = run(dir, ['status']);
  assert.equal(r.json.taskIndex, 1);
  assert.equal(r.json.amendmentReview, false);
});

test('code review: coverage contract and user-extensible fix-round cap', () => {
  const dir = repoAtSignoff();
  const act2Reviewer = run(dir, ['status']).json.modelSelection.reviewer;
  run(dir, ['begin-build']);
  run(dir, ['dispatch', '--stage', 'build', '--task', '1']);
  run(dir, ['complete', '--task', '1']);
  run(dir, ['dispatch', '--stage', 'build', '--task', '2']);
  run(dir, ['complete', '--task', '2']);
  run(dir, ['set', 'phase=code-review']);

  // APPROVED without a coverage line fails closed
  let r = run(dir, ['dispatch', '--stage', 'code']);
  assert.deepEqual(
    { model: r.json.agent.model, effort: r.json.agent.effort },
    act2Reviewer,
  );
  let f = writeCritique(dir, r.json.critiqueFile, 'All good.\n\nVERDICT: APPROVED\n');
  r = run(dir, ['verdict', '--file', f, '--stage', 'code']);
  assert.equal(r.code, 2);

  // APPROVED with PARTIAL coverage fails closed
  r = run(dir, ['dispatch', '--stage', 'code', '--retry']);
  f = writeCritique(dir, r.json.critiqueFile, 'All good.\nCOVERAGE: PARTIAL — skipped big file\nVERDICT: APPROVED\n');
  r = run(dir, ['verdict', '--file', f, '--stage', 'code']);
  assert.equal(r.code, 2);

  // REVISE with PARTIAL is a valid (actionable) verdict — pending survived retries
  f = writeCritique(dir, '.forge/critiques/code-manual.md', 'Bug in app.txt.\nCOVERAGE: PARTIAL — one binary skipped\nVERDICT: REVISE\n');
  r = run(dir, ['verdict', '--file', '.forge/critiques/code-manual.md', '--stage', 'code']);
  assert.equal(r.code, 0);
  assert.equal(r.json.action, 'revise');
  assert.equal(r.json.remaining, 2);

  // fix dispatch → complete --fix
  r = run(dir, ['dispatch', '--stage', 'fix']);
  assert.equal(r.code, 0);
  assert.equal(run(dir, ['complete', '--fix']).json.action, 'fix-completed');

  // second round still has one fix round remaining
  r = run(dir, ['dispatch', '--stage', 'code']);
  f = writeCritique(dir, r.json.critiqueFile, 'A third-round bug remains.\nCOVERAGE: FULL\nVERDICT: REVISE\n');
  r = run(dir, ['verdict', '--file', f, '--stage', 'code']);
  assert.equal(r.json.action, 'revise');
  run(dir, ['dispatch', '--stage', 'fix']);
  run(dir, ['complete', '--fix']);

  // third code round hits the initial fix cap on REVISE
  r = run(dir, ['dispatch', '--stage', 'code']);
  f = writeCritique(dir, r.json.critiqueFile, 'Still a bug.\nCOVERAGE: FULL\nVERDICT: REVISE\n');
  r = run(dir, ['verdict', '--file', f, '--stage', 'code']);
  assert.equal(r.json.action, 'deadlock');

  // user can add exactly one fix round and continue
  assert.equal(run(dir, ['set', 'maxFixRounds=4']).code, 1);
  r = run(dir, [
    'set', 'maxFixRounds=4', '--user-note', 'user authorizes one additional fix round',
  ]);
  assert.equal(r.code, 0);
  r = run(dir, ['dispatch', '--stage', 'fix']);
  assert.equal(r.code, 0);
  assert.equal(run(dir, ['dispatch', '--stage', 'fix', '--retry']).code, 0);
  assert.equal(run(dir, ['dispatch', '--stage', 'fix', '--retry']).code, 0);
  assert.equal(run(dir, ['dispatch', '--stage', 'fix', '--retry']).code, 0);
  assert.equal(run(dir, ['dispatch', '--stage', 'fix', '--retry']).code, 3);
  r = run(dir, [
    'complete', '--fix', '--user-override',
    '--user-note', 'user accepts the unverified fix result',
  ]);
  assert.equal(r.code, 0);
  assert.equal(r.json.acceptedRisk, true);
  r = run(dir, ['dispatch', '--stage', 'code']);
  f = writeCritique(dir, r.json.critiqueFile, 'Now clean.\nCOVERAGE: FULL\nVERDICT: APPROVED\n');
  r = run(dir, ['verdict', '--file', f, '--stage', 'code']);
  assert.equal(r.json.action, 'approved');
});

test('lockfile: a held fresh lock blocks mutating commands', () => {
  const dir = makeRepo();
  run(dir, ['init']);
  writeFileSync(join(dir, '.forge', 'lock'), JSON.stringify({ pid: 99999, ts: Date.now() }));
  const r = run(dir, ['set', 'phase=review']);
  assert.equal(r.code, 1);
  assert.match(r.json.error, /lock/);
});

test('cleanup: keeps artifacts by default, purges only generated agents, refs and state', () => {
  const dir = repoAtSignoff();
  const agentsDir = mkdtempSync(join(tmpdir(), 'forge-agents-'));
  const env = { FORGE_AGENTS_DIR: agentsDir };
  run(dir, ['install-agents'], env);
  writeFileSync(join(dir, 'app.txt'), 'dirty\n');
  run(dir, ['begin-build']);

  // default cleanup leaves the shared global agents untouched
  let r = run(dir, ['cleanup'], env);
  assert.equal(r.code, 0);
  assert.deepEqual(r.json.removedAgents, []);
  assert.equal(r.json.removedRefs.length, 2);
  assert.ok(existsSync(join(agentsDir, 'forge_reviewer.toml')));
  assert.ok(!existsSync(join(dir, '.forge')));
  assert.ok(existsSync(join(dir, 'PLAN.md')));

  // a hand-authored global is spared even under --purge-agents; cleanup works
  // even when there is no active state.
  writeFileSync(join(agentsDir, 'forge_builder.toml'), 'name = "forge_builder"\n# mine\n');
  r = run(dir, ['cleanup', '--purge-agents'], env);
  assert.equal(r.code, 0);
  assert.deepEqual(r.json.removedAgents, ['forge_reviewer.toml']);
  assert.equal(r.json.keptAgents.length, 1);
  assert.ok(!existsSync(join(agentsDir, 'forge_reviewer.toml')));
  assert.ok(existsSync(join(agentsDir, 'forge_builder.toml')));
  assert.ok(!existsSync(join(dir, '.forge')));
  assert.ok(existsSync(join(dir, 'PLAN.md')));
  assert.doesNotMatch(readFileSync(join(dir, '.git', 'info', 'exclude'), 'utf8'), /plan-forge-flow/);
  const refCheck = spawnSync('git', ['rev-parse', '--verify', 'refs/plan-forge/head-base'], { cwd: dir, encoding: 'utf8', env: BASE_ENV });
  assert.notEqual(refCheck.status, 0);
});

test('status: reports next task and files', () => {
  const dir = repoAtSignoff();
  const r = run(dir, ['status']);
  assert.equal(r.json.exists, true);
  assert.equal(r.json.phase, 'signoff');
  assert.equal(r.json.nextTask.number, 1);
  assert.equal(r.json.files.plan, true);
  assert.equal(r.json.files.builderAgent, false);
});

function repoAtCodeReview() {
  const dir = repoAtSignoff();
  run(dir, ['begin-build']);
  run(dir, ['dispatch', '--stage', 'build', '--task', '1']);
  run(dir, ['complete', '--task', '1']);
  run(dir, ['dispatch', '--stage', 'build', '--task', '2']);
  run(dir, ['complete', '--task', '2']);
  assert.equal(run(dir, ['set', 'phase=code-review']).code, 0);
  return dir;
}

test('prepare-review: covers full tracked diff and inventories every untracked file', () => {
  const dir = repoAtSignoff();
  writeFileSync(join(dir, 'app.txt'), 'pre-existing tracked edit\n');
  writeFileSync(join(dir, 'notes.txt'), 'pre-existing notes\n');
  run(dir, ['begin-build']);
  writeFileSync(join(dir, 'app.txt'), 'forge changed tracked edit\n');
  writeFileSync(join(dir, 'safe.txt'), 'safe untracked content\n');
  writeFileSync(join(dir, 'secret.env'), 'TOKEN=do-not-inline\n');
  writeFileSync(join(dir, 'binary.bin'), Buffer.from([0, 1, 2, 3]));
  run(dir, ['dispatch', '--stage', 'build', '--task', '1']);
  run(dir, ['complete', '--task', '1']);
  run(dir, ['dispatch', '--stage', 'build', '--task', '2']);
  run(dir, ['complete', '--task', '2']);
  run(dir, ['set', 'phase=code-review']);

  let r = run(dir, ['prepare-review']);
  assert.equal(r.code, 0);
  const paths = r.json.untracked.map((item) => item.path);
  for (const expected of ['PLAN.md', 'PLAN-REVIEW-LOG.md', 'notes.txt', 'safe.txt', 'secret.env', 'binary.bin']) {
    assert.ok(paths.includes(expected), `missing untracked path ${expected}`);
  }
  assert.deepEqual(r.json.withheld.sort(), ['binary.bin', 'secret.env']);
  assert.equal(r.json.coverageReady, false);
  const patch = readFileSync(join(dir, '.forge', 'final-review.patch'), 'utf8');
  assert.match(patch, /forge changed tracked edit/);
  assert.match(patch, /safe untracked content/);
  assert.doesNotMatch(patch, /TOKEN=do-not-inline/);
  const manifest = JSON.parse(readFileSync(join(dir, '.forge', 'review-manifest.json'), 'utf8'));
  assert.ok(manifest.tracked.preExistingFiles.includes('app.txt'));
  assert.equal(manifest.untracked.find((item) => item.path === 'notes.txt').preExisting, true);

  r = run(dir, ['prepare-review', '--allow-files', '["binary.bin","secret.env"]']);
  assert.equal(r.json.coverageReady, true);
  assert.match(readFileSync(join(dir, '.forge', 'final-review.patch'), 'utf8'), /TOKEN=do-not-inline/);
});

test('pre-existing fixes and builder replacement require explicit user reasons', () => {
  const dir = repoAtCodeReview();
  let r = run(dir, ['authorize-preexisting', '--files', '["app.txt"]']);
  assert.equal(r.code, 1);
  r = run(dir, ['authorize-preexisting', '--files', '["app.txt"]', '--user-note', 'please fix this old issue too']);
  assert.equal(r.code, 0);
  assert.deepEqual(r.json.files, ['app.txt']);

  r = run(dir, ['builder-session', '--id', 'builder-a']);
  assert.equal(r.code, 0);
  r = run(dir, ['builder-session', '--id', 'builder-b']);
  assert.equal(r.code, 1);
  r = run(dir, ['builder-session', '--id', 'builder-b', '--user-note', 'original task was not recoverable']);
  assert.equal(r.code, 0);
  assert.equal(r.json.action, 'builder-session-recovered');
});

test('accept-risk override completes a capped review as done-with-findings', () => {
  const dir = repoAtCodeReview();
  for (let round = 1; round <= 3; round++) {
    const dispatched = run(dir, ['dispatch', '--stage', 'code']);
    const critique = writeCritique(
      dir,
      dispatched.json.critiqueFile,
      `Finding ${round} remains.\nCOVERAGE: FULL\nVERDICT: REVISE\n`,
    );
    const verdict = run(dir, ['verdict', '--stage', 'code', '--file', critique]);
    if (round < 3) {
      assert.equal(verdict.json.action, 'revise');
      run(dir, ['dispatch', '--stage', 'fix']);
      run(dir, ['complete', '--fix']);
    } else {
      assert.equal(verdict.json.action, 'deadlock');
    }
  }
  let r = run(dir, ['set', 'phase=done-with-findings', '--user-override']);
  assert.equal(r.code, 1);
  r = run(dir, [
    'set', 'phase=done-with-findings', '--user-override',
    '--user-note', 'user accepts the remaining documented risk',
  ]);
  assert.equal(r.code, 0);
  assert.equal(r.json.phase, 'done-with-findings');
});

test('cleanup --delete-artifacts is ownership-gated and still removes internals', () => {
  const owned = makeRepo();
  run(owned, ['init']);
  let r = run(owned, ['cleanup', '--delete-artifacts']);
  assert.equal(r.code, 0);
  assert.deepEqual(r.json.removedArtifacts.sort(), ['PLAN-REVIEW-LOG.md', 'PLAN.md']);
  assert.ok(!existsSync(join(owned, 'PLAN.md')));
  assert.ok(!existsSync(join(owned, '.forge')));

  const foreign = makeRepo();
  run(foreign, ['init']);
  writeFileSync(join(foreign, 'PLAN.md'), '# user-owned replacement\n');
  r = run(foreign, ['cleanup', '--delete-artifacts']);
  assert.equal(r.code, 0);
  assert.equal(r.json.artifactDeletionBlocked, true);
  assert.ok(existsSync(join(foreign, 'PLAN.md')));
  assert.ok(existsSync(join(foreign, 'PLAN-REVIEW-LOG.md')));
  assert.ok(!existsSync(join(foreign, '.forge')));
  assert.doesNotMatch(readFileSync(join(foreign, '.git', 'info', 'exclude'), 'utf8'), /plan-forge-flow/);
});
