import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  mkdtempSync,
  writeFileSync,
  readFileSync,
  existsSync,
  mkdirSync,
  realpathSync,
  symlinkSync,
} from 'node:fs';
import { spawn, spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath, pathToFileURL } from 'node:url';
import {
  parseVerdict,
  parseCoverage,
  validateModelId,
  upsertExcludeBlock,
  removeExcludeBlock,
  parseApproachTasks,
  sha256,
  classifySensitivePath,
  classifySensitiveContent,
  chunkPathspecs,
  parseFeatureEnabled,
} from '../../plugins/plan-forge-flow/scripts/forge.mjs';

const FORGE = fileURLToPath(
  new URL('../../plugins/plan-forge-flow/scripts/forge.mjs', import.meta.url),
);
const WORKFLOW = fileURLToPath(
  new URL('../../plugins/plan-forge-flow/skills/forge/references/workflow.md', import.meta.url),
);
const SKILL = fileURLToPath(
  new URL('../../plugins/plan-forge-flow/skills/forge/SKILL.md', import.meta.url),
);
const README = fileURLToPath(
  new URL('../../plugins/plan-forge-flow/README.md', import.meta.url),
);
const WORKFLOW_SVG = fileURLToPath(
  new URL('../../plugins/plan-forge-flow/assets/plan-forge-workflow.svg', import.meta.url),
);
const MARKETPLACE = fileURLToPath(
  new URL('../../.agents/plugins/marketplace.json', import.meta.url),
);
const PLUGIN_MANIFEST = fileURLToPath(
  new URL('../../plugins/plan-forge-flow/.codex-plugin/plugin.json', import.meta.url),
);
const CAPTURE_HOOK = fileURLToPath(
  new URL('../../plugins/plan-forge-flow/scripts/capture-context.mjs', import.meta.url),
);
const HOOKS_MANIFEST = fileURLToPath(
  new URL('../../plugins/plan-forge-flow/hooks/hooks.json', import.meta.url),
);
const PACKAGE_JSON = fileURLToPath(new URL('../../package.json', import.meta.url));
const CI_WORKFLOW = fileURLToPath(new URL('../../.github/workflows/ci.yml', import.meta.url));

// ---------------------------------------------------------------------------
// Pure functions

test('parseVerdict: valid verdicts on the last non-empty line', () => {
  assert.equal(parseVerdict('stuff\nVERDICT: APPROVED'), 'APPROVED');
  assert.equal(parseVerdict('stuff\nVERDICT: REVISE\n\n  \n'), 'REVISE');
  assert.equal(parseVerdict('VERDICT:   APPROVED  '), 'APPROVED');
});

test('parseVerdict: restrained decoration is accepted, mid-text or missing verdicts are rejected', () => {
  assert.equal(parseVerdict('**VERDICT: APPROVED**'), 'APPROVED');
  assert.equal(parseVerdict('VERDICT: REVISE.'), 'REVISE');
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

test('secret classifier is filename-anchored and recognizes sensitive content signatures', () => {
  for (const safe of [
    'TokenService.cs',
    'PasswordPolicy.md',
    'SecretSanta.tsx',
    'src/auth/CredentialStore.ts',
  ]) {
    assert.equal(classifySensitivePath(safe), false, `${safe} should be safe`);
  }
  for (const sensitive of [
    'prod.env',
    'creds.env',
    'serviceAccount.json',
    'keystore.jks',
    'appsettings.Production.json',
    'secrets.json',
    'credentials',
    '.aws/credentials',
    '.git-credentials',
    'kubeconfig',
    '.kube/config',
    'terraform.tfstate',
    'terraform.tfstate.backup',
  ]) {
    assert.equal(classifySensitivePath(sensitive), true, `${sensitive} should be sensitive`);
  }
  assert.equal(
    classifySensitiveContent('key=-----BEGIN RSA PRIVATE KEY-----\nabc'),
    'private-key',
  );
  assert.equal(classifySensitiveContent('AWS=AKIA1234567890ABCDEF'), 'aws-access-key');
  assert.equal(
    classifySensitiveContent('api_key=AbCdEfGhIjKlMnOpQrStUv12+/'),
    'high-entropy-assignment',
  );
  for (const declaration of [
    'API_KEY=AbCdEfGhIjKlMnOpQrStUv12+/',
    '"db_password": "AbCdEfGhIjKlMnOpQrStUv12+/"',
    'const apiKey = "AbCdEfGhIjKlMnOpQrStUv12+/"',
    'export const dbPassword = "AbCdEfGhIjKlMnOpQrStUv12+/"',
    'private static readonly Secret = "AbCdEfGhIjKlMnOpQrStUv12+/"',
    'public const string ApiKey = "AbCdEfGhIjKlMnOpQrStUv12+/"',
    'api_token = "AbCdEfGhIjKlMnOpQrStUv12+/"',
    'self.api_token = "AbCdEfGhIjKlMnOpQrStUv12+/"',
    '- client_secret: AbCdEfGhIjKlMnOpQrStUv12+/',
  ]) {
    assert.equal(
      classifySensitiveContent(declaration),
      'high-entropy-assignment',
      `${declaration} should be detected`,
    );
  }
  assert.equal(classifySensitiveContent('// rotate the api key quarterly'), null);
  assert.equal(
    classifySensitiveContent('config = {"password":"AbCdEfGhIjKlMnOpQrStUv12+/"}'),
    null,
  );
});

test('pathspec chunking respects serialized budgets and preserves literal paths', () => {
  const paths = ['src/file with space.js', 'src/file#hash.js', ...Array.from(
    { length: 300 },
    (_, index) => `src/generated-${String(index).padStart(3, '0')}.js`,
  )];
  const chunks = chunkPathspecs(paths, 1024);
  assert.ok(chunks.length > 1);
  assert.deepEqual(
    chunks.flat(),
    paths.map((path) => `:(top,literal)${path}`),
  );
  for (const chunk of chunks) {
    assert.ok(chunk.reduce((used, spec) => used + spec.length + 1, 0) <= 1024);
  }
});

test('feature parser reads the enabled column rather than the stability column', () => {
  assert.equal(parseFeatureEnabled('multi_agent stable true'), true);
  assert.equal(parseFeatureEnabled('multi_agent stable false'), false);
  assert.equal(parseFeatureEnabled('other stable true'), null);
  assert.equal(parseFeatureEnabled('multi_agent stable unavailable'), null);
});

test('root verification harness and plugin hook declaration are wired', () => {
  const pkg = JSON.parse(readFileSync(PACKAGE_JSON, 'utf8'));
  assert.equal(pkg.type, 'module');
  for (const file of ['capture-context.test.mjs', 'model-catalog.test.mjs', 'forge.test.mjs']) {
    assert.match(pkg.scripts.test, new RegExp(file.replaceAll('.', '\\.')));
  }
  const ci = readFileSync(CI_WORKFLOW, 'utf8');
  assert.match(ci, /ubuntu-latest/);
  assert.match(ci, /windows-latest/);
  assert.match(ci, /npm test/);
  const manifest = JSON.parse(readFileSync(PLUGIN_MANIFEST, 'utf8'));
  assert.equal(manifest.hooks, './hooks/hooks.json');
});

test('workflow documents declined sign-off, operational commands, and trust boundary', () => {
  const workflow = readFileSync(WORKFLOW, 'utf8');
  const readme = readFileSync(README, 'utf8');
  assert.match(workflow, /user declines[\s\S]*set phase=review/);
  assert.match(workflow, /resolve-build --conflict/);
  assert.match(workflow, /pendingFingerprintMatches/);
  assert.match(workflow, /verifyCommands/);
  assert.match(workflow, /cleanup --purge-agents/);
  assert.match(workflow, /fork_turns=none/);
  assert.match(workflow, /session-capture age/);
  assert.match(workflow, /observed-model/);
  assert.match(workflow, /1 MiB aggregate budget/);
  assert.doesNotMatch(workflow, /When `\{performanceBlock\}` requires/);
  assert.match(readme, /### Trust boundary/);
  assert.match(readme, /cannot independently prove/);
  assert.match(readme, /read-only, but they are not access-restricted/);
});

test('Act 4 review contract requires performance analysis and uses the .NET skill when available', () => {
  const workflow = readFileSync(WORKFLOW, 'utf8');
  assert.match(workflow, /performance-sensitive path/);
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
  assert.match(readme, /!\[Plan Forge Flow workflow[^]*\]\(assets\/plan-forge-workflow\.svg\)/);
  assert.doesNotMatch(readme, /```mermaid/);
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

test('README workflow SVG is accessible and contains every workflow stage', () => {
  const svg = readFileSync(WORKFLOW_SVG, 'utf8');
  assert.match(svg, /<svg[^>]*role="img"[^>]*aria-labelledby="title description"/);
  assert.match(svg, /<title id="title">Plan Forge Flow workflow<\/title>/);
  for (const label of ['Phase 0', 'Act 1', 'Act 2', 'Act 3', 'Act 4', 'Done', 'Done with findings']) {
    assert.match(svg, new RegExp(`>${label}<`));
  }
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

function writeSessionCapture(cwd, pluginData, overrides = {}) {
  const canonical = realpathSync(cwd);
  const dir = join(pluginData, 'session-context');
  mkdirSync(dir, { recursive: true });
  const payload = {
    version: 1,
    sessionId: 'session-a',
    cwd: canonical,
    model: 'gpt-5.6-terra',
    permissionMode: 'workspace-write',
    effort: 'high',
    effortKnown: true,
    observedAt: new Date().toISOString(),
    ...overrides,
  };
  const path = join(dir, `${sha256(canonical.toLowerCase())}.json`);
  writeFileSync(path, JSON.stringify(payload, null, 2) + '\n');
  return path;
}

function rawRun(cwd, args, env = {}) {
  const res = spawnSync(process.execPath, [FORGE, ...args], {
    cwd,
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
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

test('CLI hardening: commit-less repos and malformed inputs fail with JSON', () => {
  const empty = mkdtempSync(join(tmpdir(), 'forge-empty-repo-'));
  sh(empty, 'git', ['init', '-q']);
  let r = rawRun(empty, ['init']);
  assert.equal(r.code, 1);
  assert.match(r.json.error, /no commits/);

  const dir = makeRepo();
  assert.equal(rawRun(dir, ['init']).code, 0);
  writeFileSync(join(dir, '.forge', 'state.json'), '{not-json');
  r = rawRun(dir, ['status']);
  assert.equal(r.code, 1);
  assert.equal(r.json.unexpected, true);

  const versioned = makeRepo();
  rawRun(versioned, ['init']);
  const statePath = join(versioned, '.forge', 'state.json');
  const state = JSON.parse(readFileSync(statePath, 'utf8'));
  state.version = 99;
  writeFileSync(statePath, JSON.stringify(state));
  r = rawRun(versioned, ['status']);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /unsupported forge state version 99/);

  r = rawRun(makeRepo(), ['models', '--native-models', '{"slug":"not-an-array"}']);
  assert.equal(r.code, 1);
  assert.match(r.json.error, /JSON array/);
});

test('argument parser supports --key=value and leading-dash values', () => {
  const spaceForm = makeRepo();
  const equalsForm = makeRepo();
  let r = rawRun(spaceForm, ['init', '--rounds', '2']);
  assert.equal(r.code, 0);
  assert.equal(r.json.maxRounds, 2);
  r = rawRun(equalsForm, ['init', '--rounds=2']);
  assert.equal(r.code, 0);
  assert.equal(r.json.maxRounds, 2);

  const negative = makeRepo();
  r = rawRun(negative, ['init', '--rounds', '-2']);
  assert.equal(r.code, 1);
  assert.match(r.json.error, /positive integer/);
});

test('doctor fails non-zero for a missing session capture and validates installed agents', () => {
  const dir = makeRepo();
  const agentsDir = mkdtempSync(join(tmpdir(), 'forge-doctor-agents-'));
  const pluginData = mkdtempSync(join(tmpdir(), 'forge-doctor-data-'));
  const fakeCodex = join(dir, 'fake-codex.js');
  writeFileSync(
    fakeCodex,
    `const args = process.argv.slice(2);
if (args[0] === '--version') console.log('codex-cli 0.145.0');
else if (args[0] === 'features') console.log('multi_agent stable true');
else process.exitCode = 1;
`,
  );
  const env = {
    FORGE_AGENTS_DIR: agentsDir,
    FORGE_PLUGIN_DATA: pluginData,
    FORGE_CODEX_PATH: fakeCodex,
  };
  assert.equal(rawRun(dir, ['install-agents'], env).code, 0);
  let r = rawRun(dir, ['doctor'], env);
  assert.equal(r.code, 1);
  const missing = r.json.checks.find((check) => check.name === 'session_capture');
  assert.equal(missing.ok, false);
  assert.equal(missing.reason, 'missing');
  assert.equal(r.json.checks.find((check) => check.name === 'reviewer_agent').ok, true);
  assert.equal(r.json.checks.find((check) => check.name === 'builder_agent').ok, true);

  writeSessionCapture(dir, pluginData);
  r = rawRun(dir, ['doctor'], env);
  assert.equal(r.code, 0, r.stderr);
  assert.equal(r.json.ok, true);
});

test('doctor warns for unknown capabilities, stale capture, and hidden spawn metadata', () => {
  const dir = makeRepo();
  const agentsDir = mkdtempSync(join(tmpdir(), 'forge-doctor-warning-agents-'));
  const pluginData = mkdtempSync(join(tmpdir(), 'forge-doctor-warning-data-'));
  const codexHome = mkdtempSync(join(tmpdir(), 'forge-doctor-warning-home-'));
  const fakeCodex = join(dir, 'fake-codex.js');
  writeFileSync(
    fakeCodex,
    `const args = process.argv.slice(2);
if (args[0] === '--version') console.log(process.env.FAKE_CODEX_VERSION);
else if (args[0] === 'features') console.log(process.env.FAKE_CODEX_FEATURES);
else process.exitCode = 1;
`,
  );
  writeFileSync(
    join(codexHome, 'config.toml'),
    '[multi_agent_v2]\nhide_spawn_agent_metadata = true\n',
  );
  writeSessionCapture(dir, pluginData, { observedAt: '2000-01-01T00:00:00.000Z' });
  const env = {
    CODEX_HOME: codexHome,
    FORGE_AGENTS_DIR: agentsDir,
    FORGE_PLUGIN_DATA: pluginData,
    FORGE_CODEX_PATH: fakeCodex,
    FORGE_SESSION_MAX_AGE_MS: '1000',
    FAKE_CODEX_VERSION: 'codex-cli preview',
    FAKE_CODEX_FEATURES: 'another_feature stable true',
  };
  assert.equal(rawRun(dir, ['install-agents'], env).code, 0);
  let r = rawRun(dir, ['doctor'], env);
  assert.equal(r.code, 0, r.stderr);
  assert.equal(r.json.ok, true);
  assert.equal(r.json.checks.find((check) => check.name === 'codex').ok, true);
  assert.equal(r.json.checks.find((check) => check.name === 'multi_agent').state, 'unknown');
  const capture = r.json.checks.find((check) => check.name === 'session_capture');
  assert.equal(capture.ok, true);
  assert.equal(capture.fresh, false);
  assert.equal(r.json.checks.find((check) => check.name === 'spawn_metadata').hidden, true);
  for (const warning of ['codex', 'multi_agent', 'spawn_metadata', 'session_capture']) {
    assert.ok(r.json.warnings.some((item) => item.name === warning), `missing ${warning} warning`);
  }

  r = rawRun(dir, ['doctor'], {
    ...env,
    FAKE_CODEX_VERSION: 'codex-cli 0.145.0',
    FAKE_CODEX_FEATURES: 'multi_agent stable false',
  });
  assert.equal(r.code, 1);
  assert.equal(r.json.checks.find((check) => check.name === 'multi_agent').state, 'false');

  r = rawRun(dir, ['doctor'], {
    ...env,
    FORGE_CODEX_PATH: join(dir, 'missing-codex'),
  });
  assert.equal(r.code, 1);
  assert.equal(r.json.checks.find((check) => check.name === 'codex').ok, false);
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

test('an approved plan verdict is bound to the reviewed PLAN.md revision', () => {
  const dir = makeRepo();
  run(dir, ['init']);
  writeFileSync(join(dir, 'PLAN.md'), PLAN_BODY);
  run(dir, ['set', 'phase=review']);
  const dispatched = run(dir, ['dispatch', '--stage', 'plan']);
  const critique = writeCritique(
    dir,
    dispatched.json.critiqueFile,
    'The reviewed plan is sound.\nVERDICT: APPROVED\n',
  );
  assert.equal(run(dir, ['verdict', '--stage', 'plan', '--file', critique]).code, 0);
  const approvedHash = run(dir, ['status', '--full']).json.lastVerdict.planHash;
  assert.equal(approvedHash, sha256(readFileSync(join(dir, 'PLAN.md'))));

  const statePath = join(dir, '.forge', 'state.json');
  const legacyState = JSON.parse(readFileSync(statePath, 'utf8'));
  delete legacyState.lastVerdict.planHash;
  writeFileSync(statePath, JSON.stringify(legacyState, null, 2) + '\n');
  let r = run(dir, ['set', 'phase=signoff']);
  assert.equal(r.code, 3, 'a verdict from an older build must fail closed');
  legacyState.lastVerdict.planHash = approvedHash;
  writeFileSync(statePath, JSON.stringify(legacyState, null, 2) + '\n');

  writeFileSync(join(dir, 'PLAN.md'), `${PLAN_BODY}\nunreviewed replacement\n`);
  r = run(dir, ['set', 'phase=signoff']);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /changed after the approving review/);

  r = run(dir, [
    'set', 'phase=signoff', '--user-override',
    '--user-note', 'accept this unreviewed plan edit',
  ]);
  assert.equal(r.code, 0);
});

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

test('invalid verdicts: exit 2, pending dispatch survives, retry cap is two', () => {
  const dir = makeRepo();
  run(dir, ['init']);
  writeFileSync(join(dir, 'PLAN.md'), PLAN_BODY);
  run(dir, ['set', 'phase=review']);
  let r = run(dir, ['dispatch', '--stage', 'plan']);
  const f = writeCritique(dir, r.json.critiqueFile, 'stuff\nVERDICT: MAYBE\n');
  r = run(dir, ['verdict', '--file', f, '--stage', 'plan']);
  assert.equal(r.code, 2);
  assert.equal(r.json.action, 'invalid-verdict');

  // pending dispatch survived; the initial format retry cap is two
  r = run(dir, ['dispatch', '--stage', 'plan', '--retry']);
  assert.equal(r.code, 0);
  assert.equal(r.json.retries, 1);
  const retryFile = writeCritique(dir, r.json.critiqueFile, 'still malformed\n');
  r = run(dir, ['verdict', '--file', retryFile, '--stage', 'plan']);
  assert.equal(r.code, 2);
  r = run(dir, ['dispatch', '--stage', 'plan', '--retry']);
  assert.equal(r.code, 0);
  assert.equal(r.json.retries, 2);
  const retryFile2 = writeCritique(dir, r.json.critiqueFile, 'still malformed again\n');
  assert.equal(run(dir, ['verdict', '--file', retryFile2, '--stage', 'plan']).code, 2);
  r = run(dir, ['dispatch', '--stage', 'plan', '--retry']);
  assert.equal(r.code, 3);

  // at the reached cap only an explicit one-round extension is accepted
  r = run(dir, ['set', 'maxVerdictRetries=3']);
  assert.equal(r.code, 1);
  r = run(dir, [
    'set', 'maxVerdictRetries=3', '--user-note', 'user wants one more format retry',
  ]);
  assert.equal(r.code, 0);
  r = run(dir, ['dispatch', '--stage', 'plan', '--retry']);
  assert.equal(r.code, 0);
  assert.equal(r.json.retries, 3);
  assert.equal(r.json.retryCap, 3);
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

test('plan integrity guards reject unauthorized relock and stale build hashes', () => {
  const relockDir = repoAtSignoff();
  let r = run(relockDir, ['lock-plan', '--relock']);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /only legal during an amendment/);

  const completeDir = repoAtSignoff();
  run(completeDir, ['begin-build']);
  run(completeDir, ['dispatch', '--stage', 'build', '--task', '1']);
  writeFileSync(join(completeDir, 'PLAN.md'), `${PLAN_BODY}\nchanged after dispatch\n`);
  r = run(completeDir, ['complete', '--task', '1']);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /changed since task 1 was dispatched/);

  const dispatchDir = repoAtSignoff();
  run(dispatchDir, ['begin-build']);
  writeFileSync(join(dispatchDir, 'PLAN.md'), `${PLAN_BODY}\nstale sign-off\n`);
  r = run(dispatchDir, ['dispatch', '--stage', 'build', '--task', '1']);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /PLAN.md changed after user sign-off/);

  const cancelDir = repoAtSignoff();
  run(cancelDir, ['begin-build']);
  run(cancelDir, ['dispatch', '--stage', 'build', '--task', '1']);
  writeFileSync(join(cancelDir, 'PLAN.md'), `${PLAN_BODY}\nstale after dispatch\n`);
  r = run(cancelDir, ['dispatch', '--stage', 'build', '--cancel']);
  assert.equal(r.code, 0, r.json?.error ?? r.stderr);
});

test('fingerprint: content edits to already-modified files change it', () => {
  const dir = repoAtSignoff();
  writeFileSync(join(dir, 'app.txt'), 'already modified\n');
  run(dir, ['begin-build']);
  run(dir, ['dispatch', '--stage', 'build', '--task', '1']);
  let r = run(dir, ['status', '--full']);
  assert.equal(r.json.pendingFingerprintMatches, true);
  // same git-status codes, different content
  writeFileSync(join(dir, 'app.txt'), 'modified differently\n');
  r = run(dir, ['status', '--full']);
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
  assert.equal(run(dir, ['status', '--full']).json.lastVerdict, null);
  r = run(dir, ['confirm-signoff', '--user-note', 'skip the amendment review']);
  assert.equal(r.code, 3);
  r = run(dir, ['set', 'phase=build']);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /amendment review must be approved/);

  // amendment review round, then return to build with position preserved
  r = run(dir, ['dispatch', '--stage', 'plan']);
  const f = writeCritique(dir, r.json.critiqueFile, 'Amendment fine.\n\nVERDICT: APPROVED\n');
  run(dir, ['verdict', '--file', f, '--stage', 'plan']);
  const reviewedAmendment = readFileSync(join(dir, 'PLAN.md'), 'utf8');
  writeFileSync(join(dir, 'PLAN.md'), `${reviewedAmendment}\nunreviewed amendment edit\n`);
  r = run(dir, ['confirm-signoff', '--user-note', 'yes, build this unreviewed edit']);
  assert.equal(r.code, 3);
  r = run(dir, ['set', 'phase=build']);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /approved a different PLAN.md revision/);
  writeFileSync(join(dir, 'PLAN.md'), reviewedAmendment);
  r = run(dir, ['confirm-signoff', '--user-note', 'yes, build the amended plan']);
  assert.equal(r.code, 0);
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
  assert.ok(Buffer.byteLength(JSON.stringify(r.json)) < 4096);
  assert.ok(r.json.caps);
  assert.equal(r.json.pendingDispatch, null);
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
  const patch = readFileSync(join(dir, '.forge', 'in-run.patch'), 'utf8') +
    readFileSync(join(dir, '.forge', 'untracked-review.patch'), 'utf8');
  assert.match(patch, /forge changed tracked edit/);
  assert.match(patch, /safe untracked content/);
  assert.doesNotMatch(patch, /TOKEN=do-not-inline/);
  const manifest = JSON.parse(readFileSync(join(dir, '.forge', 'review-manifest.json'), 'utf8'));
  assert.ok(manifest.tracked.preExistingFiles.includes('app.txt'));
  assert.equal(manifest.untracked.find((item) => item.path === 'notes.txt').preExisting, true);
  assert.equal(manifest.workspaceRoot, realpathSync(dir));
  assert.equal(manifest.diffScope.pathspec, '.');
  assert.ok(!existsSync(join(dir, '.forge', 'final-review.patch')));
  for (const artifact of ['PLAN.md', 'PLAN-REVIEW-LOG.md']) {
    const entry = manifest.untracked.find((item) => item.path === artifact);
    assert.equal(entry.classification, 'excluded-by-design');
    assert.equal(entry.excludedByDesign, true);
  }

  r = run(dir, [
    'prepare-review',
    '--allow-files', '["binary.bin","secret.env"]',
    '--user-note', 'review these two files',
  ]);
  assert.equal(r.json.coverageReady, true);
  assert.match(readFileSync(join(dir, '.forge', 'untracked-review.patch'), 'utf8'), /TOKEN=do-not-inline/);
  r = run(dir, ['prepare-review']);
  assert.equal(r.json.coverageReady, true, 'allowances must survive later preparation');
  const fullStatus = run(dir, ['status', '--full']);
  assert.equal('reviewManifest' in fullStatus.json, false);
});

test('prepare-review streams a multi-megabyte tracked diff completely', () => {
  const dir = repoAtCodeReview();
  const content = `start\n${'x'.repeat(3 * 1024 * 1024)}\ncomplete-tail\n`;
  writeFileSync(join(dir, 'app.txt'), content);
  const r = run(dir, ['prepare-review']);
  assert.equal(r.code, 0, r.json?.error ?? r.stderr);
  const patch = readFileSync(join(dir, '.forge', 'in-run.patch'), 'utf8');
  assert.ok(Buffer.byteLength(patch) > 3 * 1024 * 1024);
  assert.match(patch, /complete-tail/);
});

test('prepare-review batches hundreds of tracked paths including spaces and hash characters', () => {
  const dir = makeRepo();
  const bulkDir = join(dir, 'bulk');
  mkdirSync(bulkDir);
  const names = [
    'file with space.txt',
    'file#hash.txt',
    ...Array.from({ length: 298 }, (_, index) =>
      `file-${String(index).padStart(3, '0')}.txt`),
  ];
  for (const name of names) writeFileSync(join(bulkDir, name), 'before\n');
  sh(dir, 'git', ['add', '.']);
  sh(dir, 'git', ['commit', '-q', '-m', 'add bulk fixture']);

  run(dir, ['init']);
  writeFileSync(join(dir, 'PLAN.md'), PLAN_BODY);
  run(dir, ['set', 'phase=review']);
  let r = run(dir, ['dispatch', '--stage', 'plan']);
  writeCritique(dir, r.json.critiqueFile, 'Bulk plan is sound.\nVERDICT: APPROVED\n');
  run(dir, ['verdict', '--stage', 'plan', '--file', r.json.critiqueFile]);
  run(dir, ['set', 'phase=signoff']);
  run(dir, ['confirm-signoff', '--user-note', 'Yes, build the bulk fixture']);
  run(dir, ['lock-plan']);
  run(dir, ['begin-build']);

  for (const name of names) writeFileSync(join(bulkDir, name), `after ${name}\n`);
  run(dir, ['dispatch', '--stage', 'build', '--task', '1']);
  run(dir, ['complete', '--task', '1']);
  run(dir, ['dispatch', '--stage', 'build', '--task', '2']);
  run(dir, ['complete', '--task', '2']);
  run(dir, ['set', 'phase=code-review']);

  const started = performance.now();
  r = run(dir, ['prepare-review']);
  const elapsedMs = performance.now() - started;
  assert.equal(r.code, 0, r.json?.error ?? r.stderr);
  assert.ok(elapsedMs < 10_000, `prepare-review took ${Math.round(elapsedMs)} ms`);
  const patch = readFileSync(join(dir, '.forge', 'in-run.patch'), 'utf8');
  assert.match(patch, /bulk\/file with space\.txt/);
  assert.match(patch, /bulk\/file#hash\.txt/);
  assert.match(patch, /after file-297\.txt/);
});

test('prepare-review records oversized, unreadable, and aggregate-budget limitations', () => {
  const dir = repoAtCodeReview();
  writeFileSync(join(dir, 'oversized.txt'), 'o'.repeat(101 * 1024));
  for (let i = 0; i < 12; i++) {
    writeFileSync(join(dir, `budget-${i}.txt`), 'a'.repeat(95 * 1024));
  }
  symlinkSync('missing-target.txt', join(dir, 'dangling.txt'), 'file');

  const r = run(dir, ['prepare-review']);
  assert.equal(r.code, 0, r.json?.error ?? r.stderr);
  assert.equal(r.json.coverageReady, false);
  const manifest = JSON.parse(readFileSync(join(dir, '.forge', 'review-manifest.json'), 'utf8'));
  const oversized = manifest.untracked.find((entry) => entry.path === 'oversized.txt');
  assert.equal(oversized.classification, 'larger-than-100KB');
  assert.equal(oversized.hash, null);
  const dangling = manifest.untracked.find((entry) => entry.path === 'dangling.txt');
  assert.equal(dangling.classification, 'read-failed');
  assert.match(dangling.withheldReason, /stat-failed/);
  assert.ok(manifest.untracked.some((entry) => entry.truncated));
  assert.equal(manifest.coverageReady, false);
  assert.ok(readFileSync(join(dir, '.forge', 'untracked-review.patch')).length <= 1024 * 1024);
});

test('prepare-review screens tracked secrets and requires recorded consent', () => {
  const dir = repoAtCodeReview();
  const secret = 'const access = "AKIA1234567890ABCDEF";\n';
  writeFileSync(join(dir, 'app.txt'), secret);
  let r = run(dir, ['prepare-review']);
  assert.equal(r.code, 0);
  assert.equal(r.json.coverageReady, false);
  assert.doesNotMatch(readFileSync(join(dir, '.forge', 'in-run.patch'), 'utf8'), /AKIA/);
  let manifest = JSON.parse(readFileSync(join(dir, '.forge', 'review-manifest.json'), 'utf8'));
  assert.match(manifest.tracked.files.find((entry) => entry.path === 'app.txt').classification, /aws-access-key/);

  r = run(dir, ['prepare-review', '--allow-files', '["app.txt"]']);
  assert.equal(r.code, 1);
  assert.match(r.json.error, /requires --user-note/);
  r = run(dir, [
    'prepare-review',
    '--allow-files', '["app.txt"]',
    '--user-note', 'include this test credential in review',
  ]);
  assert.equal(r.code, 0);
  assert.equal(r.json.coverageReady, true);
  assert.match(readFileSync(join(dir, '.forge', 'in-run.patch'), 'utf8'), /AKIA/);
  r = run(dir, ['prepare-review']);
  assert.equal(r.json.coverageReady, true);
  manifest = JSON.parse(readFileSync(join(dir, '.forge', 'review-manifest.json'), 'utf8'));
  assert.deepEqual(manifest.allowances.files, ['app.txt']);
});

test('prepare-review handles more than one MiB of untracked path inventory', () => {
  const dir = repoAtCodeReview();
  const bulk = join(dir, 'bulk');
  mkdirSync(bulk);
  const count = 6200;
  for (let i = 0; i < count; i++) {
    const name = `p${String(i).padStart(5, '0')}-${'x'.repeat(155)}.txt`;
    writeFileSync(join(bulk, name), '');
  }
  const r = run(dir, ['prepare-review']);
  assert.equal(r.code, 0, r.json?.error ?? r.stderr);
  const paths = r.json.untracked.map((entry) => entry.path);
  assert.equal(paths.filter((path) => path.startsWith('bulk/')).length, count);
  assert.ok(paths.includes(`bulk/p06199-${'x'.repeat(155)}.txt`));
  assert.ok(readFileSync(join(dir, '.forge', 'changed-files.txt')).length > 1024 * 1024);
  const status = run(dir, ['status']);
  assert.ok(Buffer.byteLength(JSON.stringify(status.json)) < 4096);
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

test('session registration records observed spawn metadata and warns on pin drift', () => {
  const reviewerDir = makeRepo();
  run(reviewerDir, ['init']);
  writeFileSync(join(reviewerDir, 'PLAN.md'), PLAN_BODY);
  run(reviewerDir, ['set', 'phase=review']);
  const dispatched = rawRun(reviewerDir, ['dispatch', '--stage', 'plan']);
  let r = rawRun(reviewerDir, [
    'reviewer-session',
    '--id', 'observed-reviewer',
    '--dispatch-id', dispatched.json.dispatchId,
    '--observed-model', 'different-reviewer-model',
    '--observed-effort', 'low',
  ]);
  assert.equal(r.code, 0, r.json?.error ?? r.stderr);
  assert.match(r.stderr, /does not match pin/);
  let state = JSON.parse(readFileSync(join(reviewerDir, '.forge', 'state.json'), 'utf8'));
  assert.equal(state.pendingDispatch.observedModel, 'different-reviewer-model');
  assert.equal(state.pendingDispatch.observedEffort, 'low');
  let events = readFileSync(join(reviewerDir, '.forge', 'events.jsonl'), 'utf8')
    .trim().split('\n').map(JSON.parse);
  assert.ok(events.some((event) =>
    event.cmd === 'model-pin-not-applied' && event.role === 'reviewer'));

  const builderDir = repoAtSignoff();
  run(builderDir, ['begin-build']);
  r = rawRun(builderDir, [
    'builder-session',
    '--id', 'observed-builder',
    '--observed-model', 'different-builder-model',
    '--observed-effort', 'high',
  ]);
  assert.equal(r.code, 0, r.json?.error ?? r.stderr);
  assert.match(r.stderr, /does not match pin/);
  state = JSON.parse(readFileSync(join(builderDir, '.forge', 'state.json'), 'utf8'));
  assert.equal(state.builderSessionObservation.model, 'different-builder-model');
  assert.equal(state.builderSessionObservation.effort, 'high');
  assert.equal(run(builderDir, ['dispatch', '--stage', 'build', '--task', '1']).code, 0);
  state = JSON.parse(readFileSync(join(builderDir, '.forge', 'state.json'), 'utf8'));
  assert.equal(state.pendingDispatch.observedModel, 'different-builder-model');
  assert.equal(state.pendingDispatch.observedEffort, 'high');
  events = readFileSync(join(builderDir, '.forge', 'events.jsonl'), 'utf8')
    .trim().split('\n').map(JSON.parse);
  assert.ok(events.some((event) =>
    event.cmd === 'model-pin-not-applied' && event.role === 'builder'));
});

test('workspace commands anchor to the repository root when invoked from a subdirectory', () => {
  const dir = makeRepo();
  const sub = join(dir, 'sub');
  mkdirSync(sub);
  let r = run(sub, ['init']);
  assert.equal(r.code, 0);
  assert.ok(existsSync(join(dir, '.forge', 'state.json')));
  assert.equal(existsSync(join(sub, '.forge')), false);

  writeFileSync(join(dir, 'PLAN.md'), PLAN_BODY);
  run(dir, ['set', 'phase=review']);
  r = run(dir, ['dispatch', '--stage', 'plan']);
  const critique = writeCritique(dir, r.json.critiqueFile, 'Looks good.\nVERDICT: APPROVED\n');
  run(dir, ['verdict', '--stage', 'plan', '--file', critique]);
  run(dir, ['set', 'phase=signoff']);
  run(dir, ['confirm-signoff', '--user-note', 'yes']);
  run(dir, ['lock-plan']);
  run(dir, ['begin-build']);
  writeFileSync(join(dir, 'app.txt'), 'root change visible from subdirectory\n');
  for (const task of [1, 2]) {
    run(dir, ['dispatch', '--stage', 'build', '--task', String(task)]);
    run(dir, ['complete', '--task', String(task)]);
  }
  run(dir, ['set', 'phase=code-review']);
  r = run(sub, ['prepare-review']);
  assert.equal(r.code, 0);
  assert.match(readFileSync(join(dir, '.forge', 'in-run.patch'), 'utf8'), /root change visible/);
});

test('verdict refuses to append to an unowned review log', () => {
  const dir = makeRepo();
  run(dir, ['init']);
  writeFileSync(join(dir, 'PLAN.md'), PLAN_BODY);
  run(dir, ['set', 'phase=review']);
  const dispatched = run(dir, ['dispatch', '--stage', 'plan']);
  const critique = writeCritique(dir, dispatched.json.critiqueFile, 'Fine.\nVERDICT: APPROVED\n');
  writeFileSync(join(dir, 'PLAN-REVIEW-LOG.md'), '# user log\n');
  const r = run(dir, ['verdict', '--stage', 'plan', '--file', critique]);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /ownership marker/);
  assert.equal(readFileSync(join(dir, 'PLAN-REVIEW-LOG.md'), 'utf8'), '# user log\n');
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

test('reviewer dispatch rejects session drift that makes the pinned reviewer weaker', () => {
  const dir = makeRepo();
  const pluginData = mkdtempSync(join(tmpdir(), 'forge-session-drift-'));
  const env = { FORGE_PLUGIN_DATA: pluginData };
  const nativeModels = JSON.stringify([
    {
      slug: 'gpt-5.6-sol',
      priority: 1,
      supportedEfforts: ['low', 'medium', 'high', 'xhigh', 'max'],
      spawnAvailable: true,
    },
    {
      slug: 'gpt-5.6-terra',
      priority: 2,
      supportedEfforts: ['low', 'medium', 'high', 'xhigh', 'max'],
      spawnAvailable: true,
    },
  ]);
  writeSessionCapture(dir, pluginData, {
    sessionId: 'session-a',
    model: 'gpt-5.6-terra',
  });
  assert.equal(rawRun(dir, ['init'], env).code, 0);
  writeFileSync(join(dir, 'PLAN.md'), PLAN_BODY);
  assert.equal(rawRun(dir, ['set', 'phase=review'], env).code, 0);
  let r = rawRun(dir, [
    'configure-reviewer',
    '--native-models', nativeModels,
    '--reviewer-model', 'gpt-5.6-terra',
    '--reviewer-effort', 'high',
  ], env);
  assert.equal(r.code, 0, r.json?.error ?? r.stderr);

  writeSessionCapture(dir, pluginData, {
    sessionId: 'session-b',
    model: 'gpt-5.6-sol',
  });
  r = rawRun(dir, ['dispatch', '--stage', 'plan', '--native-models', nativeModels], env);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /strength invariant/);
});

test('expired session captures are rejected with a JSON error', () => {
  const dir = makeRepo();
  const pluginData = mkdtempSync(join(tmpdir(), 'forge-expired-session-'));
  writeSessionCapture(dir, pluginData, { observedAt: '2000-01-01T00:00:00.000Z' });
  const r = rawRun(dir, ['models', '--native-models', '[]'], {
    FORGE_PLUGIN_DATA: pluginData,
    FORGE_SESSION_MAX_AGE_MS: '1000',
  });
  assert.equal(r.code, 3);
  assert.match(r.json.error, /session capture.*older/);
});

test('stale session captures warn but do not block reviewer dispatch, cancellation, or doctor', () => {
  const dir = makeRepo();
  const pluginData = mkdtempSync(join(tmpdir(), 'forge-stale-session-'));
  const agentsDir = mkdtempSync(join(tmpdir(), 'forge-stale-agents-'));
  const fakeCodex = join(dir, 'fake-codex.js');
  writeFileSync(
    fakeCodex,
    `const args = process.argv.slice(2);
if (args[0] === '--version') console.log('codex-cli 0.145.0');
else if (args[0] === 'features') console.log('multi_agent stable true');
else process.exitCode = 1;
`,
  );
  const env = {
    FORGE_PLUGIN_DATA: pluginData,
    FORGE_AGENTS_DIR: agentsDir,
    FORGE_CODEX_PATH: fakeCodex,
    FORGE_SESSION_MAX_AGE_MS: '1000',
  };
  const nativeModels = JSON.stringify([
    {
      slug: 'gpt-5.6-sol',
      priority: 1,
      supportedEfforts: ['low', 'medium', 'high', 'xhigh', 'max'],
      spawnAvailable: true,
    },
    {
      slug: 'gpt-5.6-terra',
      priority: 2,
      supportedEfforts: ['low', 'medium', 'high', 'xhigh', 'max'],
      spawnAvailable: true,
    },
  ]);
  writeSessionCapture(dir, pluginData);
  assert.equal(rawRun(dir, ['install-agents'], env).code, 0);
  assert.equal(rawRun(dir, ['init'], env).code, 0);
  writeFileSync(join(dir, 'PLAN.md'), PLAN_BODY);
  assert.equal(rawRun(dir, ['set', 'phase=review'], env).code, 0);
  let r = rawRun(dir, [
    'configure-reviewer',
    '--native-models', nativeModels,
    '--reviewer-model', 'gpt-5.6-sol',
    '--reviewer-effort', 'high',
  ], env);
  assert.equal(r.code, 0, r.json?.error ?? r.stderr);

  writeSessionCapture(dir, pluginData, { observedAt: '2000-01-01T00:00:00.000Z' });
  r = rawRun(dir, ['dispatch', '--stage', 'plan'], env);
  assert.equal(r.code, 0, r.json?.error ?? r.stderr);
  assert.match(r.stderr, /session capture is .* ms old/);
  const events = readFileSync(join(dir, '.forge', 'events.jsonl'), 'utf8')
    .trim().split('\n').map(JSON.parse);
  assert.ok(events.some((event) => event.cmd === 'session-capture-stale'));

  r = rawRun(dir, ['dispatch', '--stage', 'plan', '--cancel'], env);
  assert.equal(r.code, 0, r.json?.error ?? r.stderr);
  r = rawRun(dir, ['doctor'], env);
  assert.equal(r.code, 0, r.json?.error ?? r.stderr);
  const capture = r.json.checks.find((check) => check.name === 'session_capture');
  assert.equal(capture.ok, true);
  assert.equal(capture.fresh, false);
});

test('repository-scoped marker refuses a second worktree at init time', () => {
  const primary = makeRepo();
  const parent = mkdtempSync(join(tmpdir(), 'forge-worktrees-'));
  const secondary = join(parent, 'secondary');
  sh(primary, 'git', ['worktree', 'add', '-q', '-b', `forge-test-${Date.now()}`, secondary]);
  assert.equal(run(primary, ['init']).code, 0);
  const r = rawRun(secondary, ['init']);
  assert.equal(r.code, 3);
  assert.match(r.json.error, /another worktree has an active forge run/);
});

test('stale-lock recovery has one winner and a CliError loser', async () => {
  const dir = makeRepo();
  assert.equal(run(dir, ['init']).code, 0);
  writeFileSync(join(dir, '.forge', 'lock'), JSON.stringify({ pid: -1, ts: 0, token: 'stale' }));
  const helper = join(dir, 'lock-helper.mjs');
  writeFileSync(
    helper,
    `import { Workspace } from ${JSON.stringify(pathToFileURL(FORGE).href)};
const ws = new Workspace(process.argv[2]);
try {
  ws.acquireLock();
  console.log(JSON.stringify({ winner: true }));
  await new Promise((resolve) => setTimeout(resolve, 500));
  ws.releaseLock();
} catch (error) {
  console.log(JSON.stringify({ error: error.message, code: error.code ?? null }));
  process.exitCode = 1;
}
`,
  );
  const launch = () => new Promise((resolveResult) => {
    const child = spawn(process.execPath, [helper, dir], {
      cwd: dir,
      env: BASE_ENV,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (chunk) => { stdout += chunk; });
    child.stderr.on('data', (chunk) => { stderr += chunk; });
    child.on('close', (code) => resolveResult({ code, stdout, stderr }));
  });
  const results = await Promise.all([launch(), launch()]);
  assert.deepEqual(results.map((result) => result.code).sort(), [0, 1]);
  const loser = results.find((result) => result.code === 1);
  const body = JSON.parse(loser.stdout.trim());
  assert.equal(body.code, 1);
  assert.match(body.error, /lock|forge run/i);
});

test('the prompt hook writes a capture that doctor finds in the same run', () => {
  const dir = makeRepo();
  const pluginData = mkdtempSync(join(tmpdir(), 'forge-hook-handoff-'));
  // Codex points PLUGIN_DATA at a marketplace-qualified directory that only the
  // hook process sees, so honouring it would strand the capture.
  const env = { FORGE_PLUGIN_DATA: pluginData, PLUGIN_DATA: mkdtempSync(join(tmpdir(), 'forge-codex-data-')) };
  const hook = spawnSync(process.execPath, [CAPTURE_HOOK], {
    cwd: dir,
    encoding: 'utf8',
    input: JSON.stringify({
      session_id: 'session-handoff',
      cwd: dir,
      model: 'gpt-5.6-terra',
      permission_mode: 'workspace-write',
    }),
    env: { ...BASE_ENV, ...env },
  });
  assert.equal(hook.status, 0, hook.stderr);
  const r = rawRun(dir, ['doctor'], env);
  const capture = r.json.checks.find((check) => check.name === 'session_capture');
  assert.equal(capture.ok, true, JSON.stringify(capture));
  assert.equal(capture.sessionId, 'session-handoff');
});

test('doctor explains a missing capture instead of only naming the path', () => {
  const dir = makeRepo();
  const env = { FORGE_PLUGIN_DATA: mkdtempSync(join(tmpdir(), 'forge-no-capture-')) };
  const capture = rawRun(dir, ['doctor'], env).json.checks
    .find((check) => check.name === 'session_capture');
  assert.equal(capture.ok, false);
  assert.equal(capture.everCaptured, false);
  assert.match(capture.hint, /hook is not running/i);
});

test('the hook manifest runs on Windows shells as well as POSIX ones', () => {
  const raw = readFileSync(HOOKS_MANIFEST, 'utf8');
  assert.equal(raw.charCodeAt(0), '{'.charCodeAt(0), 'a BOM makes Codex reject the manifest');
  const handler = JSON.parse(raw).hooks.UserPromptSubmit[0].hooks[0];
  // Codex runs hooks through PowerShell on Windows, where "$PLUGIN_ROOT"
  // expands to the empty string instead of the plugin directory.
  assert.match(handler.command, /\$PLUGIN_ROOT\//);
  assert.match(handler.commandWindows, /\$env:PLUGIN_ROOT\\/);
});
