import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, readdirSync } from 'node:fs';
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
    env: { ...process.env, PLUGIN_DATA: pluginData },
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

test('UserPromptSubmit hook fails open for malformed input or missing PLUGIN_DATA', () => {
  let result = spawnSync(process.execPath, [HOOK], { encoding: 'utf8', input: '{bad' });
  assert.equal(result.status, 0);
  result = spawnSync(process.execPath, [HOOK], {
    encoding: 'utf8',
    input: JSON.stringify({ cwd: process.cwd(), model: 'gpt-5.6-sol' }),
    env: { ...process.env, PLUGIN_DATA: '' },
  });
  assert.equal(result.status, 0);
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
    env: { ...process.env, PLUGIN_DATA: pluginData },
  });
  assert.equal(result.status, 0);
  const file = readdirSync(join(pluginData, 'session-context'))[0];
  const captured = JSON.parse(readFileSync(join(pluginData, 'session-context', file), 'utf8'));
  assert.equal(captured.effort, 'xhigh');
  assert.equal(captured.effortKnown, true);
});
