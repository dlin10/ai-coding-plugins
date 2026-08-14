#!/usr/bin/env node

import { createHash } from 'node:crypto';
import { existsSync, statSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const MAX_INPUT_BYTES = 1024 * 1024;
const MAX_OUTPUT_BYTES = 64 * 1024;

function hookFailure(input, reason) {
  if (input?.hook_event_name === 'UserPromptExpansion') {
    return { decision: 'block', reason };
  }
  if (input?.hook_event_name === 'PreToolUse') {
    return {
      hookSpecificOutput: {
        hookEventName: 'PreToolUse',
        permissionDecision: 'deny',
        permissionDecisionReason: reason,
      },
    };
  }
  return null;
}

function isEntry(input) {
  return input?.hook_event_name === 'UserPromptExpansion' && input.command_name === 'plan-forge-flow:forge' ||
         input?.hook_event_name === 'PreToolUse' && input.tool_name === 'Skill';
}

function isExit(input) {
  return input?.hook_event_name === 'PreToolUse' && input.tool_name === 'ExitPlanMode';
}

function activationExists(input) {
  if (typeof input?.session_id !== 'string' || input.session_id.length === 0) return true;
  const dataRoot = process.env.FORGE_PLUGIN_DATA || process.env.CLAUDE_PLUGIN_DATA ||
                   join(homedir(), '.claude', 'plugin-data', 'plan-forge-flow');
  const key = createHash('sha256').update(input.session_id).digest('hex');
  const path = join(dataRoot, 'claude-activations', `${key}.json`);
  if (!existsSync(path)) return false;
  try {
    return statSync(path).isFile() || existsSync(path);
  } catch {
    return true;
  }
}

function executablePath() {
  const platforms = { win32: 'win', linux: 'linux', darwin: 'osx' };
  const architectures = { x64: 'x64', arm64: 'arm64' };
  const platform = platforms[process.platform];
  const architecture = architectures[process.arch];
  if (!platform || !architecture) throw new Error(`unsupported host ${process.platform}-${process.arch}`);
  const pluginRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
  return join(pluginRoot, 'bin', `${platform}-${architecture}`, process.platform === 'win32' ? 'planforge.exe' : 'planforge');
}

function fallback(input, error) {
  const message = `Plan Forge enforcement is unavailable: ${error instanceof Error ? error.message : 'unknown dispatcher failure'}`;
  if (isEntry(input) || isExit(input) && activationExists(input)) return hookFailure(input, message);
  return null;
}

let input;
let raw;
try {
  const chunks = [];
  let bytes = 0;
  for await (const chunk of process.stdin) {
    bytes += chunk.length;
    if (bytes > MAX_INPUT_BYTES) throw new Error('hook input exceeds the size bound');
    chunks.push(chunk);
  }
  raw = Buffer.concat(chunks).toString('utf8');
  input = JSON.parse(raw);
  if (!isEntry(input) && !isExit(input)) process.exit(0);

  const executable = executablePath();
  if (!existsSync(executable)) throw new Error('RID-specific planforge executable is missing');
  const result = spawnSync(executable, ['hook', 'claude-workflow'], {
    input: raw,
    env: { ...process.env, CLAUDE_CODE_SESSION_ID: input.session_id ?? '' },
    encoding: 'utf8',
    maxBuffer: MAX_OUTPUT_BYTES,
    timeout: 10_000,
    windowsHide: true,
  });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`planforge hook exited with code ${result.status}`);
  const output = result.stdout ?? '';
  if (Buffer.byteLength(output) > MAX_OUTPUT_BYTES) throw new Error('planforge hook output exceeds the size bound');
  if (output.trim().length > 0) JSON.parse(output);
  else if (isEntry(input)) throw new Error('planforge did not arm the Forge run');
  process.stdout.write(output);
} catch (error) {
  const output = fallback(input, error);
  if (output) process.stdout.write(JSON.stringify(output));
}
