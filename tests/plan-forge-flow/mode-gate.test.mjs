import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  writeFileSync,
} from 'node:fs';
import { execFileSync, spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { sessionContextPathFor } from '../../plugins/plan-forge-flow/scripts/plugin-paths.mjs';

const FORGE = fileURLToPath(
  new URL('../../plugins/plan-forge-flow/scripts/forge.mjs', import.meta.url),
);

function git(cwd, args) {
  return execFileSync('git', ['-C', cwd, ...args], { encoding: 'utf8' }).trim();
}

function fixture(mode) {
  const root = mkdtempSync(join(tmpdir(), 'forge-mode-gate-'));
  const repo = join(root, 'repo');
  const pluginData = join(root, 'plugin-data');
  mkdirSync(repo);
  git(repo, ['init', '-q']);
  git(repo, ['-c', 'user.name=Forge Test', '-c', 'user.email=forge@example.test',
    'commit', '--allow-empty', '-q', '-m', 'initial']);
  const cwd = realpathSync(repo);
  if (mode) {
    const previous = process.env.FORGE_PLUGIN_DATA;
    process.env.FORGE_PLUGIN_DATA = pluginData;
    const capturePath = sessionContextPathFor(cwd);
    if (previous === undefined) delete process.env.FORGE_PLUGIN_DATA;
    else process.env.FORGE_PLUGIN_DATA = previous;
    mkdirSync(join(pluginData, 'session-context'), { recursive: true });
    writeFileSync(capturePath, JSON.stringify({
      version: 2,
      cwd,
      collaborationMode: mode,
      observedAt: new Date().toISOString(),
    }));
  }
  return { cwd, pluginData };
}

function run(item, args) {
  return spawnSync(process.execPath, [FORGE, ...args, '--cwd', item.cwd], {
    encoding: 'utf8',
    env: { ...process.env, FORGE_PLUGIN_DATA: item.pluginData },
  });
}

test('removed pre-materialization state command cannot mutate in Plan or unknown mode', () => {
  for (const mode of ['plan', null]) {
    const item = fixture(mode);
    const excludePath = join(item.cwd, '.git', 'info', 'exclude');
    const beforeExclude = readFileSync(excludePath, 'utf8');
    const result = run(item, ['init']);
    assert.notEqual(result.status, 0);
    assert.match(result.stdout, /unknown command/);
    assert.equal(existsSync(join(item.cwd, '.forge')), false);
    assert.equal(existsSync(join(item.cwd, 'PLAN.md')), false);
    assert.equal(existsSync(join(item.cwd, 'PLAN-REVIEW-LOG.md')), false);
    assert.equal(existsSync(join(item.cwd, '.git', 'plan-forge-active.json')), false);
    assert.equal(readFileSync(excludePath, 'utf8'), beforeExclude);
  }
});

test('removed pre-materialization state command is unavailable in Default mode too', () => {
  const item = fixture('default');
  const result = run(item, ['init']);
  assert.equal(result.status, 1);
  assert.match(result.stdout, /unknown command/);
  assert.equal(existsSync(join(item.cwd, '.forge')), false);
});
