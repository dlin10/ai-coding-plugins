#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { mkdirSync, renameSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import process from 'node:process';

function sha256(text) {
  return createHash('sha256').update(text).digest('hex');
}

let input = '';
for await (const chunk of process.stdin) input += chunk;

try {
  const event = JSON.parse(input || '{}');
  const pluginData = process.env.PLUGIN_DATA;
  if (!pluginData || !event.cwd) process.exit(0);
  const cwd = resolve(event.cwd);
  const dir = join(pluginData, 'session-context');
  const target = join(dir, `${sha256(cwd.toLowerCase())}.json`);
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
