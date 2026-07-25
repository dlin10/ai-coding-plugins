import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  realpathSync,
  symlinkSync,
} from 'node:fs';
import { spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';

const HOOK = fileURLToPath(
  new URL('../../plugins/plan-forge-flow/scripts/capture-context.mjs', import.meta.url),
);

test('UserPromptSubmit hook records model/session/cwd but leaves effort unknown', () => {
  const pluginData = mkdtempSync(join(tmpdir(), 'forge-hook-'));
  const cwd = mkdtempSync(join(tmpdir(), 'forge-hook-cwd-'));
  const result = spawnSync(process.execPath, [HOOK], {
    encoding: 'utf8',
    input: JSON.stringify({
      session_id: 'session-123',
      cwd,
      model: 'gpt-5.6-sol',
      permission_mode: 'workspace-write',
    }),
    env: { ...process.env, FORGE_PLUGIN_DATA: pluginData },
  });
  assert.equal(result.status, 0);
  assert.equal(result.stdout, '');
  const files = readdirSync(join(pluginData, 'session-context'));
  assert.equal(files.length, 1);
  const captured = JSON.parse(readFileSync(join(pluginData, 'session-context', files[0]), 'utf8'));
  assert.equal(captured.sessionId, 'session-123');
  assert.equal(captured.model, 'gpt-5.6-sol');
  assert.equal(captured.cwd, cwd);
  assert.equal(captured.effortKnown, false);
});

test('UserPromptSubmit hook fails open for malformed input', () => {
  const result = spawnSync(process.execPath, [HOOK], { encoding: 'utf8', input: '{bad' });
  assert.equal(result.status, 0);
});

test('UserPromptSubmit hook writes where the CLI looks, ignoring Codex PLUGIN_DATA', () => {
  const pluginData = mkdtempSync(join(tmpdir(), 'forge-hook-shared-'));
  const codexPluginData = mkdtempSync(join(tmpdir(), 'forge-hook-codex-'));
  const cwd = mkdtempSync(join(tmpdir(), 'forge-hook-cwd-'));
  const result = spawnSync(process.execPath, [HOOK], {
    encoding: 'utf8',
    input: JSON.stringify({ session_id: 'session-shared', cwd, model: 'gpt-5.6-sol' }),
    env: { ...process.env, FORGE_PLUGIN_DATA: pluginData, PLUGIN_DATA: codexPluginData },
  });
  assert.equal(result.status, 0);
  assert.equal(existsSync(join(codexPluginData, 'session-context')), false);
  const dir = join(pluginData, 'session-context');
  const files = readdirSync(dir);
  assert.equal(files.length, 1);
  assert.equal(JSON.parse(readFileSync(join(dir, files[0]), 'utf8')).sessionId, 'session-shared');
});

test('UserPromptSubmit hook captures effort when a future/native payload exposes it', () => {
  const pluginData = mkdtempSync(join(tmpdir(), 'forge-hook-'));
  const cwd = mkdtempSync(join(tmpdir(), 'forge-hook-cwd-'));
  const result = spawnSync(process.execPath, [HOOK], {
    encoding: 'utf8',
    input: JSON.stringify({
      session_id: 'session-effort',
      cwd,
      model: 'gpt-5.6-sol',
      reasoning_effort: 'xhigh',
    }),
    env: { ...process.env, FORGE_PLUGIN_DATA: pluginData },
  });
  assert.equal(result.status, 0);
  const file = readdirSync(join(pluginData, 'session-context'))[0];
  const captured = JSON.parse(readFileSync(join(pluginData, 'session-context', file), 'utf8'));
  assert.equal(captured.effort, 'xhigh');
  assert.equal(captured.effortKnown, true);
});

test('UserPromptSubmit hook keys symlinked workspaces by canonical real path', () => {
  const pluginData = mkdtempSync(join(tmpdir(), 'forge-hook-'));
  const cwd = mkdtempSync(join(tmpdir(), 'forge-hook-cwd-'));
  const parent = mkdtempSync(join(tmpdir(), 'forge-hook-link-'));
  const alias = join(parent, 'workspace');
  symlinkSync(cwd, alias, process.platform === 'win32' ? 'junction' : 'dir');
  for (const path of [cwd, alias]) {
    const result = spawnSync(process.execPath, [HOOK], {
      encoding: 'utf8',
      input: JSON.stringify({
        session_id: 'session-symlink',
        cwd: path,
        model: 'gpt-5.6-sol',
      }),
      env: { ...process.env, FORGE_PLUGIN_DATA: pluginData },
    });
    assert.equal(result.status, 0);
  }
  const files = readdirSync(join(pluginData, 'session-context'));
  assert.equal(files.length, 1);
  const captured = JSON.parse(readFileSync(join(pluginData, 'session-context', files[0]), 'utf8'));
  assert.equal(captured.cwd, realpathSync(cwd));
});

test('UserPromptSubmit hook keys repository subdirectories by the Git root', () => {
  const pluginData = mkdtempSync(join(tmpdir(), 'forge-hook-'));
  const cwd = mkdtempSync(join(tmpdir(), 'forge-hook-repo-'));
  const sub = join(cwd, 'nested');
  const init = spawnSync('git', ['init', '-q'], { cwd, encoding: 'utf8' });
  assert.equal(init.status, 0, init.stderr);
  mkdirSync(sub);
  for (const path of [cwd, sub]) {
    const result = spawnSync(process.execPath, [HOOK], {
      encoding: 'utf8',
      input: JSON.stringify({ session_id: 'session-root', cwd: path, model: 'gpt-5.6-sol' }),
      env: { ...process.env, FORGE_PLUGIN_DATA: pluginData },
    });
    assert.equal(result.status, 0);
  }
  const files = readdirSync(join(pluginData, 'session-context'));
  assert.equal(files.length, 1);
  const captured = JSON.parse(readFileSync(join(pluginData, 'session-context', files[0]), 'utf8'));
  assert.equal(captured.cwd, realpathSync(cwd));
});
