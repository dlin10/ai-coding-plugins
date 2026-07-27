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
  writeFileSync,
} from 'node:fs';
import { spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import {
  canonicalWorkspaceRoot,
} from '../../plugins/plan-forge-flow/scripts/plugin-paths.mjs';
import {
  buildApprovalWrapper,
  issuanceItemId,
  newNonce,
  sha256,
} from '../../plugins/plan-forge-flow/scripts/resume-envelope.mjs';

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
  assert.equal(captured.cwd, canonicalWorkspaceRoot(cwd));
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
  assert.equal(captured.cwd, canonicalWorkspaceRoot(cwd));
});

test('UserPromptSubmit derives Default mode from the matching turn and emits bounded resume context', () => {
  const pluginData = mkdtempSync(join(tmpdir(), 'forge-hook-'));
  const cwd = mkdtempSync(join(tmpdir(), 'forge-hook-cwd-'));
  const transcriptPath = join(cwd, 'transcript.jsonl');
  const plan = '<!-- plan-forge-flow:owned -->\n# Plan\n\n## Approach\n1. Implement.\n';
  const nonce = newNonce();
  const origin = {
    sessionId: 'session-native',
    transcriptPath,
    turnId: 'turn-plan',
  };
  const envelope = {
    version: 1,
    humanPlanHash: sha256(plan),
    repository: { workspaceRoot: realpathSync(cwd), gitCommonDir: realpathSync(cwd) },
    origin: { ...origin, itemId: issuanceItemId(origin, nonce, sha256(plan)) },
    planRevision: 1,
    nonce,
    completedReviewRounds: 1,
    maxRounds: 5,
    reviewer: { model: 'gpt-5.6-sol', effort: 'high' },
    builder: { model: 'gpt-5.6-terra', effort: 'high' },
    builderPlanHash: sha256(plan),
    catalogObservation: {
      generatedAt: '2026-07-27T00:00:00.000Z',
      models: [
        {
          slug: 'gpt-5.6-sol',
          displayName: 'Sol',
          description: 'Reviewer',
          priority: 1,
          defaultEffort: 'high',
          reasoningLevels: [{ effort: 'high', description: 'High' }],
        },
        {
          slug: 'gpt-5.6-terra',
          displayName: 'Terra',
          description: 'Builder',
          priority: 2,
          defaultEffort: 'high',
          reasoningLevels: [{ effort: 'high', description: 'High' }],
        },
      ],
    },
    reviewLog: '<!-- plan-forge-flow:owned -->\n# Log\n',
  };
  const wrapper = buildApprovalWrapper(plan, envelope);
  writeFileSync(transcriptPath, [
    JSON.stringify({ type: 'session_meta', payload: { id: 'session-native' } }),
    JSON.stringify({
      type: 'turn_context',
      payload: { turn_id: 'turn-plan', collaboration_mode: { mode: 'plan' } },
    }),
    JSON.stringify({
      type: 'response_item',
      payload: {
        type: 'message',
        id: envelope.origin.itemId,
        role: 'assistant',
        phase: 'final_answer',
        content: [{ type: 'output_text', text: `<proposed_plan>\n${wrapper}</proposed_plan>` }],
      },
    }),
    JSON.stringify({
      type: 'turn_context',
      payload: { turn_id: 'turn-native', collaboration_mode: { mode: 'default' } },
    }),
  ].join('\n') + '\n');
  const result = spawnSync(process.execPath, [HOOK], {
    encoding: 'utf8',
    input: JSON.stringify({
      session_id: 'session-native',
      turn_id: 'turn-native',
      transcript_path: transcriptPath,
      cwd,
      prompt: 'Implement the plan.',
      permission_mode: 'read-only',
    }),
    env: { ...process.env, FORGE_PLUGIN_DATA: pluginData },
  });
  assert.equal(result.status, 0);
  const hookOutput = JSON.parse(result.stdout);
  assert.equal(hookOutput.hookSpecificOutput.hookEventName, 'UserPromptSubmit');
  assert.match(hookOutput.hookSpecificOutput.additionalContext, /authenticated resume\/materialization/);
  const file = readdirSync(join(pluginData, 'session-context'))[0];
  const captured = JSON.parse(readFileSync(join(pluginData, 'session-context', file), 'utf8'));
  assert.equal(captured.collaborationMode, 'default');
  assert.equal(captured.promptKind, 'same-context');
  assert.equal(captured.implementationCandidate, true);
  assert.equal(captured.permissionMode, 'read-only');
  assert.equal(Object.hasOwn(captured, 'prompt'), false);
});

test('UserPromptSubmit does not inject context for an ordinary preceding plan', () => {
  const pluginData = mkdtempSync(join(tmpdir(), 'forge-hook-'));
  const cwd = mkdtempSync(join(tmpdir(), 'forge-hook-cwd-'));
  const transcriptPath = join(cwd, 'transcript.jsonl');
  writeFileSync(transcriptPath, [
    JSON.stringify({ type: 'session_meta', payload: { id: 'session-ordinary' } }),
    JSON.stringify({
      type: 'turn_context',
      payload: { turn_id: 'turn-plan', collaboration_mode: { mode: 'plan' } },
    }),
    JSON.stringify({
      type: 'response_item',
      payload: {
        type: 'message',
        role: 'assistant',
        phase: 'final_answer',
        content: [{ type: 'output_text', text: '<proposed_plan>\n# Ordinary plan\n</proposed_plan>' }],
      },
    }),
    JSON.stringify({
      type: 'turn_context',
      payload: { turn_id: 'turn-implement', collaboration_mode: { mode: 'default' } },
    }),
  ].join('\n') + '\n');
  const result = spawnSync(process.execPath, [HOOK], {
    encoding: 'utf8',
    input: JSON.stringify({
      session_id: 'session-ordinary',
      turn_id: 'turn-implement',
      transcript_path: transcriptPath,
      cwd,
      prompt: 'Implement the plan.',
    }),
    env: { ...process.env, FORGE_PLUGIN_DATA: pluginData },
  });
  assert.equal(result.status, 0);
  assert.equal(result.stdout, '');
  const file = readdirSync(join(pluginData, 'session-context'))[0];
  const capture = JSON.parse(readFileSync(join(pluginData, 'session-context', file), 'utf8'));
  assert.equal(capture.promptKind, 'same-context');
  assert.equal(capture.implementationCandidate, false);
});
