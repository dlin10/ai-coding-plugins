#!/usr/bin/env node
import { execFileSync } from 'node:child_process';
import { mkdirSync, realpathSync, renameSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
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

let input = '';
for await (const chunk of process.stdin) input += chunk;

try {
  const event = JSON.parse(input || '{}');
  if (!event.cwd) process.exit(0);
  const cwd = canonicalWorkspace(event.cwd);
  const target = sessionContextPathFor(cwd);
  const dir = dirname(target);
  const effort = event.reasoning_effort ?? event.effort ?? null;
  const payload = {
    version: 1,
    sessionId: event.session_id ?? null,
    cwd,
    model: event.model ?? null,
    permissionMode: event.permission_mode ?? null,
    effort,
    effortKnown: typeof effort === 'string' && effort.length > 0,
    observedAt: new Date().toISOString(),
  };
  mkdirSync(dir, { recursive: true });
  const temp = `${target}.${process.pid}.tmp`;
  writeFileSync(temp, JSON.stringify(payload, null, 2) + '\n');
  renameSync(temp, target);
} catch {
  // Hooks are observational and must never block a user prompt.
}
