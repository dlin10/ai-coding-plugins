#!/usr/bin/env node

import { appendFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

const MAX_INPUT_BYTES = 2 * 1024 * 1024;
const MAX_RESULT_CHARS = 256 * 1024;
const PLAN_FORGE_AGENT = /^(?:plan-forge-flow:)?forge-(reviewer|builder)-(none|low|medium|high|xhigh|max)$/;
const ANTHROPIC_ALIASES = new Set(['sonnet', 'opus', 'haiku', 'fable', 'inherit']);
const MODEL_ID = /^[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}$/;

function normalizeModelEvidence(requestedAlias, effort, response) {
  if (!ANTHROPIC_ALIASES.has(requestedAlias)) throw new Error('unsupported requested alias');
  if (requestedAlias === 'haiku' && effort !== 'none') throw new Error('haiku requires none effort');
  if (!['haiku', 'inherit'].includes(requestedAlias) && effort === 'none') throw new Error('model family requires explicit effort');

  const resolvedModel = response.resolvedModel;
  if (typeof resolvedModel !== 'string' || !MODEL_ID.test(resolvedModel)) throw new Error('resolvedModel is required');
  const modelsUsed = response.modelsUsed == null ? [resolvedModel] : response.modelsUsed;
  if (!Array.isArray(modelsUsed) || modelsUsed.length === 0 || modelsUsed.length > 64 ||
      modelsUsed.some((model) => typeof model !== 'string' || !MODEL_ID.test(model))) {
    throw new Error('modelsUsed is invalid');
  }

  if (requestedAlias === 'inherit') {
    if (modelsUsed.some((model) => model !== resolvedModel)) throw new Error('inherit model swap detected');
  } else {
    const family = new RegExp(`(^|[^a-z0-9])${requestedAlias}([^a-z0-9]|$)`, 'i');
    if (!family.test(resolvedModel) || modelsUsed.some((model) => !family.test(model))) {
      throw new Error('model family swap detected');
    }
  }

  return { resolvedModel, modelsUsed };
}

function boundedText(value) {
  if (typeof value === 'string') return value.slice(0, MAX_RESULT_CHARS);
  if (value === undefined || value === null) return null;
  try {
    return JSON.stringify(value).slice(0, MAX_RESULT_CHARS);
  } catch {
    return null;
  }
}

function safeName(value) {
  return String(value || 'unknown').replace(/[^A-Za-z0-9._-]/g, '_').slice(0, 128) || 'unknown';
}

try {
  const dataRoot = process.env.CLAUDE_PLUGIN_DATA;
  if (!dataRoot) process.exit(0);

  const chunks = [];
  let bytes = 0;
  for await (const chunk of process.stdin) {
    bytes += chunk.length;
    if (bytes > MAX_INPUT_BYTES) process.exit(0);
    chunks.push(chunk);
  }

  const input = JSON.parse(Buffer.concat(chunks).toString('utf8'));
  if (!['PostToolUse', 'SubagentStop'].includes(input.hook_event_name)) process.exit(0);

  const response = input.tool_response ?? {};
  const toolInput = input.tool_input ?? {};
  const agent = input.hook_event_name === 'SubagentStop'
    ? input.agent_type
    : toolInput.subagent_type ?? response.agentType ?? response.agent_type;
  const match = typeof agent === 'string' ? agent.match(PLAN_FORGE_AGENT) : null;
  if (!match) process.exit(0);

  const result = input.hook_event_name === 'SubagentStop'
    ? input.last_assistant_message
    : response.content ?? response.result ?? response.status;
  const requestedAlias = input.hook_event_name === 'PostToolUse' ? toolInput.model ?? 'inherit' : null;
  const effort = match[2];
  let modelEvidence = null;
  let modelEvidenceError = null;
  if (input.hook_event_name === 'PostToolUse') {
    try {
      modelEvidence = normalizeModelEvidence(requestedAlias, effort, response);
    } catch (error) {
      modelEvidenceError = error instanceof Error ? error.message : 'invalid model evidence';
    }
  }
  const evidence = {
    capturedAt: new Date().toISOString(),
    event: input.hook_event_name,
    sessionId: input.session_id ?? null,
    agentId: input.agent_id ?? response.agentId ?? null,
    agent,
    requestedAlias,
    resolvedModel: modelEvidence?.resolvedModel ?? response.resolvedModel ?? null,
    modelsUsed: modelEvidence?.modelsUsed ?? (Array.isArray(response.modelsUsed) ? response.modelsUsed : null),
    effort,
    modelEvidenceValid: modelEvidence === null ? input.hook_event_name === 'SubagentStop' ? null : false : true,
    modelEvidenceError,
    result: boundedText(result),
  };

  const evidenceRoot = join(dataRoot, 'agent-evidence');
  mkdirSync(evidenceRoot, { recursive: true });
  appendFileSync(join(evidenceRoot, `${safeName(input.session_id)}.jsonl`), `${JSON.stringify(evidence)}\n`, { encoding: 'utf8' });
} catch {
  // Evidence is advisory. Never block or perturb the Claude workflow.
}
