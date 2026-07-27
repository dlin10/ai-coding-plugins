import { existsSync, readFileSync, realpathSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import process from 'node:process';
import { sessionContextPathFor } from './plugin-paths.mjs';

function canonicalWorkspace(cwd) {
  const canonical = realpathSync(cwd);
  try {
    return realpathSync(execFileSync(
      'git',
      ['-C', canonical, 'rev-parse', '--show-toplevel'],
      { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] },
    ).trim());
  } catch {
    return canonical;
  }
}

export function capturedMode(cwd) {
  const workspaceRoot = canonicalWorkspace(cwd);
  const path = sessionContextPathFor(workspaceRoot);
  if (!existsSync(path)) return { mode: 'unknown', reason: 'missing-capture', workspaceRoot };
  let capture;
  try {
    capture = JSON.parse(readFileSync(path, 'utf8'));
  } catch {
    return { mode: 'unknown', reason: 'malformed-capture', workspaceRoot };
  }
  if (capture.version !== 2 || capture.cwd !== workspaceRoot ||
      typeof capture.observedAt !== 'string') {
    return { mode: 'unknown', reason: 'schema-incompatible-capture', workspaceRoot };
  }
  const age = Date.now() - Date.parse(capture.observedAt);
  const configured = Number(process.env.FORGE_SESSION_MAX_AGE_MS);
  const maxAge = Number.isFinite(configured) && configured > 0 ? configured : 30 * 60 * 1000;
  if (!Number.isFinite(age) || age < 0 || age > maxAge) {
    return { mode: 'unknown', reason: 'stale-capture', workspaceRoot };
  }
  if (capture.collaborationMode !== 'plan' && capture.collaborationMode !== 'default') {
    return {
      mode: 'unknown',
      reason: capture.collaborationModeReason ?? 'unsupported-collaboration-mode',
      workspaceRoot,
    };
  }
  return { mode: capture.collaborationMode, reason: null, workspaceRoot };
}

export function requireDefaultMode(cwd, action) {
  const observed = capturedMode(cwd);
  if (observed.mode !== 'default') {
    throw new Error(
      `${action} requires a fresh Default-mode UserPromptSubmit capture ` +
      `(observed ${observed.mode}: ${observed.reason ?? 'no reason'})`,
    );
  }
  return observed;
}
