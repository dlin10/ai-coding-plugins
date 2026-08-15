#!/usr/bin/env node

import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

const repoRoot = resolve(import.meta.dirname, '..');
const pluginRoot = join(repoRoot, 'plugins', 'plan-forge-flow');
const agentRoot = join(pluginRoot, 'agents');
const efforts = ['none', 'low', 'medium', 'high', 'xhigh', 'max'];
const modelEvidenceCases = json(join(pluginRoot, 'tests', 'anthropic-model-evidence-cases.json'));
const reviewerTools = [
  'Read',
  'Grep',
  'Glob',
  'ToolSearch',
  'mcp__roslyn-mcp__roslyn_validate_file',
  'mcp__roslyn-mcp__roslyn_search_symbols',
  'mcp__roslyn-mcp__roslyn_get_symbol_info',
  'mcp__roslyn-mcp__roslyn_find_references',
  'mcp__roslyn-mcp__roslyn_find_implementations',
  'mcp__roslyn-mcp__roslyn_find_callers',
  'mcp__roslyn-mcp__roslyn_go_to_definition',
  'mcp__roslyn-mcp__roslyn_get_document_symbols',
  'mcp__roslyn-mcp__roslyn_find_dead_code',
];

function json(path) {
  return JSON.parse(readFileSync(path, 'utf8'));
}

function frontmatter(path) {
  const text = readFileSync(path, 'utf8').replace(/\r\n/g, '\n');
  const match = text.match(/^---\n([\s\S]*?)\n---\n/);
  assert.ok(match, `${path} has YAML frontmatter`);
  return Object.fromEntries(match[1].split('\n').map((line) => {
    const separator = line.indexOf(':');
    assert.ok(separator > 0, `${path} has simple key/value frontmatter`);
    return [line.slice(0, separator).trim(), line.slice(separator + 1).trim()];
  }));
}

const manifests = ['.codex-plugin', '.claude-plugin', '.cursor-plugin']
  .map((directory) => json(join(pluginRoot, directory, 'plugin.json')));
assert.deepEqual(new Set(manifests.map(({ version }) => version)), new Set(['0.7.0']));
assert.equal(manifests[1].agents, undefined, 'Claude discovers the conventional agents/ directory');
assert.equal(manifests[1].hooks, './hooks/hooks.claude.json');

const markdownAgents = readdirSync(agentRoot).filter((name) => name.endsWith('.md')).sort();
assert.equal(markdownAgents.length, 12, 'exactly 12 Claude agent definitions are shipped');

for (const role of ['reviewer', 'builder']) {
  for (const effort of efforts) {
    const path = join(agentRoot, `forge-${role}-${effort}.md`);
    const data = frontmatter(path);
    assert.equal(data.name, `forge-${role}-${effort}`);
    assert.equal(data.model, undefined, `${data.name} leaves model selection to the invocation`);
    assert.equal(data.effort, effort === 'none' ? undefined : effort);

    if (role === 'reviewer') {
      const actualTools = data.tools.split(',').map((tool) => tool.trim());
      assert.deepEqual(actualTools, reviewerTools, `${data.name} has the exact least-privilege allowlist`);
      assert.ok(actualTools.every((tool) => !tool.includes('*')), `${data.name} has no wildcard tools`);
      assert.ok(!actualTools.some((tool) => ['Agent', 'Bash', 'PowerShell', 'Edit', 'Write'].includes(tool)));
      assert.ok(actualTools.filter((tool) => tool.startsWith('mcp__')).every((tool) => tool.startsWith('mcp__roslyn-mcp__')));
    } else {
      assert.equal(data.tools, undefined, `${data.name} inherits normal tools and user permissions`);
    }
  }
}

const hooks = json(join(pluginRoot, 'hooks', 'hooks.claude.json')).hooks;
assert.equal(hooks.UserPromptExpansion[0].matcher, '^plan-forge-flow:forge$');
assert.equal(hooks.PreToolUse[0].matcher, 'Skill');
assert.equal(hooks.PreToolUse[0].hooks[0].if, 'Skill(plan-forge-flow:forge)');
assert.equal(hooks.PreToolUse[1].matcher, 'ExitPlanMode');
assert.equal(hooks.Stop.length, 1);
for (const hook of [hooks.UserPromptExpansion[0].hooks[0], ...hooks.PreToolUse.flatMap(({ hooks: handlers }) => handlers), hooks.Stop[0].hooks[0]]) {
  assert.equal(hook.command, 'node');
  assert.equal(hook.async, undefined, 'workflow gate hooks are synchronous');
  assert.equal(hook.timeout, 10);
  assert.deepEqual(hook.args, ['${CLAUDE_PLUGIN_ROOT}/scripts/claude-workflow-hook.mjs']);
}
assert.equal(hooks.PostToolUse[0].matcher, 'Agent');
assert.equal(hooks.SubagentStop[0].matcher, '^plan-forge-flow:forge-(reviewer|builder)-(none|low|medium|high|xhigh|max)$');
for (const event of ['PostToolUse', 'SubagentStop']) {
  const hook = hooks[event][0].hooks[0];
  assert.equal(hook.command, 'node');
  assert.equal(hook.async, true, `${event} evidence hook is non-blocking`);
  assert.deepEqual(hook.args, ['${CLAUDE_PLUGIN_ROOT}/scripts/capture-claude-agent-evidence.mjs']);
}

const hookScript = join(pluginRoot, 'scripts', 'capture-claude-agent-evidence.mjs');
const hookSource = readFileSync(hookScript, 'utf8');
assert.match(hookSource, /CLAUDE_PLUGIN_DATA/);
assert.doesNotMatch(hookSource, /CLAUDE_PROJECT_DIR/);
const workflowHookScript = join(pluginRoot, 'scripts', 'claude-workflow-hook.mjs');
const workflowHookSource = readFileSync(workflowHookScript, 'utf8');
assert.match(workflowHookSource, /claude-activations/);
assert.match(workflowHookSource, /hook', 'claude-workflow/);
assert.match(workflowHookSource, /ExitPlanMode/);
assert.match(workflowHookSource, /Stop/);
assert.match(workflowHookSource, /process\.platform !== 'win32'/);
assert.match(workflowHookSource, /process\.arch !== 'x64'/);
assert.match(workflowHookSource, /bin', 'win-x64', 'planforge\.exe'/);

const binEntries = readdirSync(join(pluginRoot, 'bin'), { withFileTypes: true });
assert.deepEqual(binEntries.filter((entry) => entry.isDirectory()).map(({ name }) => name).sort(), ['win-x64']);
assert.match(readFileSync(join(pluginRoot, 'bin', 'planforge-launcher.sh'), 'utf8'), /supports only Windows x64/);
assert.match(readFileSync(join(pluginRoot, 'bin', 'planforge-launcher.ps1'), 'utf8'), /Architecture\]::X64/);

const packageSource = readFileSync(join(pluginRoot, 'build', 'package.ps1'), 'utf8');
assert.match(packageSource, /\.claude-plugin\/plugin\.json/);
assert.match(packageSource, /\.claude-plugin\/marketplace\.json/);
assert.match(packageSource, /hooks\/hooks\.claude\.json/);
assert.match(packageSource, /foreach \(\$directory in @\('skills', 'agents', 'cursor', 'assets', 'hooks', 'scripts'\)\)/);
assert.match(packageSource, /scripts\/capture-claude-agent-evidence\.mjs/);
assert.match(packageSource, /scripts\/claude-workflow-hook\.mjs/);
assert.match(packageSource, /must contain exactly 12 Claude agent descriptors/);
assert.doesNotMatch(packageSource, /\$Rids/);
assert.match(packageSource, /\$rid = 'win-x64'/);
assert.match(packageSource, /archive must contain only the win-x64 executable/);

const skillSource = readFileSync(join(pluginRoot, 'skills', 'forge', 'SKILL.md'), 'utf8');
const claudeWorkflowPath = join(pluginRoot, 'skills', 'forge', 'references', 'claude-workflow.md');
const claudeWorkflow = readFileSync(claudeWorkflowPath, 'utf8');
assert.match(skillSource, /\[claude-workflow\.md\]\(references\/claude-workflow\.md\)/);
assert.match(skillSource, /Claude workflow is\s+authoritative/);
assert.match(skillSource, /Start only when the user explicitly invokes `\$forge`/);
assert.match(skillSource, /staged or pending plan are\s+not consent/);
assert.match(skillSource, /Never call\s+`ExitPlanMode` until Act 2/);
assert.match(claudeWorkflow, /overrides the Codex-only\s+pending-plan hook/);

const lifecycle = [
  'run begin --host claude',
  'run doctor --host claude',
  'plan stage --host claude',
  'review record-response --host claude',
  'plan invalidate --host claude',
  'plan finalize --host claude',
  'native `ExitPlanMode`',
  'plan materialize --host claude',
  'build dispatch --host claude',
  'session builder --host claude',
  'build complete --host claude',
  'review prepare --host claude',
  'session reviewer --host claude',
  'review verdict --host claude',
  'run cleanup --host claude',
];
let lifecycleIndex = -1;
for (const command of lifecycle) {
  const nextIndex = claudeWorkflow.indexOf(command, lifecycleIndex + 1);
  assert.ok(nextIndex > lifecycleIndex, `Claude workflow contains ordered lifecycle step: ${command}`);
  lifecycleIndex = nextIndex;
}
assert.match(claudeWorkflow, /plan-forge-flow:forge-reviewer-<effort>/);
assert.match(claudeWorkflow, /plan-forge-flow:forge-builder-<effort>/);
assert.match(claudeWorkflow, /builder-resume/);
assert.match(claudeWorkflow, /Version 2\.1\.232 or newer is required/);
assert.match(claudeWorkflow, /PreToolUse:ExitPlanMode/);
assert.doesNotMatch(claudeWorkflow, /approval\/materialization order is advisory/);
assert.match(claudeWorkflow, /\{"to":"<held-agent-id>","message":"<complete locked task and dispatch evidence>"\}/);
assert.match(claudeWorkflow, /\{"to":"<held-agent-id>","message":"<bounded accepted fix findings and dispatch evidence>"\}/);
assert.match(claudeWorkflow, /SendMessage` does not\s+require Agent Teams/);
assert.match(claudeWorkflow, /https:\/\/code\.claude\.com\/docs\/en\/sub-agents#resume-subagents/);
assert.match(claudeWorkflow, /https:\/\/code\.claude\.com\/docs\/en\/tools-reference/);
assert.doesNotMatch(claudeWorkflow, /CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS/);
assert.match(claudeWorkflow, /--stage fix-build/);
assert.match(claudeWorkflow, /--stage fix-review/);
assert.match(claudeWorkflow, /zero-content lock\s+files live in the external plugin-data session directory/);

const dataRoot = mkdtempSync(join(tmpdir(), 'plan-forge-claude-evidence-'));
try {
  const env = { ...process.env, CLAUDE_PLUGIN_DATA: dataRoot };
  for (const [index, testCase] of modelEvidenceCases.entries()) {
    const input = {
      session_id: `model-case-${index}`,
      hook_event_name: 'PostToolUse',
      tool_name: 'Agent',
      tool_input: { subagent_type: 'plan-forge-flow:forge-reviewer-high', model: testCase.alias },
      tool_response: { status: 'completed', resolvedModel: testCase.model, modelsUsed: [testCase.model] },
    };
    const result = spawnSync(process.execPath, [hookScript], { input: JSON.stringify(input), env, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    const evidence = JSON.parse(readFileSync(join(dataRoot, 'agent-evidence', `model-case-${index}.jsonl`), 'utf8'));
    assert.equal(evidence.modelEvidenceValid, testCase.valid, `${testCase.alias}/${testCase.model} matches the shared family-boundary contract`);
  }

  const inputs = [
    {
      session_id: 'session:one',
      hook_event_name: 'PostToolUse',
      tool_name: 'Agent',
      tool_input: { subagent_type: 'plan-forge-flow:forge-reviewer-high', model: 'opus' },
      tool_response: { status: 'completed', agentId: 'agent-1', resolvedModel: 'claude-opus-test', content: [{ type: 'text', text: 'approved' }] },
    },
    {
      session_id: 'session:one',
      hook_event_name: 'SubagentStop',
      agent_id: 'agent-1',
      agent_type: 'plan-forge-flow:forge-reviewer-high',
      last_assistant_message: 'approved',
    },
  ];

  for (const input of inputs) {
    const result = spawnSync(process.execPath, [hookScript], { input: JSON.stringify(input), env, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stdout, '');
  }

  const evidencePath = join(dataRoot, 'agent-evidence', 'session_one.jsonl');
  const evidence = readFileSync(evidencePath, 'utf8').trim().split('\n').map(JSON.parse);
  assert.equal(evidence.length, 2);
  assert.deepEqual(evidence.map(({ event }) => event), ['PostToolUse', 'SubagentStop']);
  assert.equal(evidence[0].requestedAlias, 'opus');
  assert.equal(evidence[0].resolvedModel, 'claude-opus-test');
  assert.deepEqual(evidence[0].modelsUsed, ['claude-opus-test']);
  assert.equal(evidence[0].effort, 'high');
  assert.equal(evidence[0].modelEvidenceValid, true);
  assert.equal(evidence[0].modelEvidenceError, null);
  assert.equal(evidence[1].result, 'approved');

  const swapped = {
    session_id: 'session:swap',
    hook_event_name: 'PostToolUse',
    tool_name: 'Agent',
    tool_input: { subagent_type: 'plan-forge-flow:forge-builder-max', model: 'sonnet' },
    tool_response: {
      status: 'completed',
      resolvedModel: 'claude-sonnet-4-6',
      modelsUsed: ['claude-sonnet-4-6', 'claude-opus-4-6'],
    },
  };
  const swappedResult = spawnSync(process.execPath, [hookScript], { input: JSON.stringify(swapped), env, encoding: 'utf8' });
  assert.equal(swappedResult.status, 0, swappedResult.stderr);
  const swappedEvidence = JSON.parse(readFileSync(join(dataRoot, 'agent-evidence', 'session_swap.jsonl'), 'utf8'));
  assert.equal(swappedEvidence.modelEvidenceValid, false);
  assert.equal(swappedEvidence.modelEvidenceError, 'model family swap detected');

  const inherited = {
    session_id: 'session:inherit',
    hook_event_name: 'PostToolUse',
    tool_name: 'Agent',
    tool_input: { subagent_type: 'plan-forge-flow:forge-reviewer-none' },
    tool_response: { status: 'completed', resolvedModel: 'claude-opus-4-6' },
  };
  const inheritedResult = spawnSync(process.execPath, [hookScript], { input: JSON.stringify(inherited), env, encoding: 'utf8' });
  assert.equal(inheritedResult.status, 0, inheritedResult.stderr);
  const inheritedEvidence = JSON.parse(readFileSync(join(dataRoot, 'agent-evidence', 'session_inherit.jsonl'), 'utf8'));
  assert.equal(inheritedEvidence.requestedAlias, 'inherit');
  assert.deepEqual(inheritedEvidence.modelsUsed, ['claude-opus-4-6']);
  assert.equal(inheritedEvidence.effort, 'none');
  assert.equal(inheritedEvidence.modelEvidenceValid, true);

  const unresolved = {
    session_id: 'session:unresolved',
    hook_event_name: 'PostToolUse',
    tool_name: 'Agent',
    tool_input: { subagent_type: 'plan-forge-flow:forge-reviewer-high', model: 'opus' },
    tool_response: { status: 'completed' },
  };
  const unresolvedResult = spawnSync(process.execPath, [hookScript], { input: JSON.stringify(unresolved), env, encoding: 'utf8' });
  assert.equal(unresolvedResult.status, 0, unresolvedResult.stderr);
  const unresolvedEvidence = JSON.parse(readFileSync(join(dataRoot, 'agent-evidence', 'session_unresolved.jsonl'), 'utf8'));
  assert.equal(unresolvedEvidence.resolvedModel, null);
  assert.equal(unresolvedEvidence.modelsUsed, null);
  assert.equal(unresolvedEvidence.modelEvidenceValid, false);
  assert.equal(unresolvedEvidence.modelEvidenceError, 'resolvedModel is required');

  const malformed = spawnSync(process.execPath, [hookScript], { input: '{', env, encoding: 'utf8' });
  assert.equal(malformed.status, 0, 'malformed advisory evidence never blocks Claude');
} finally {
  rmSync(dataRoot, { recursive: true, force: true });
}

const dispatcherRoot = mkdtempSync(join(tmpdir(), 'plan-forge-claude-dispatcher-'));
try {
  const scripts = join(dispatcherRoot, 'scripts');
  const dispatcherData = join(dispatcherRoot, 'data');
  mkdirSync(scripts, { recursive: true });
  copyFileSync(workflowHookScript, join(scripts, 'claude-workflow-hook.mjs'));
  const env = { ...process.env, CLAUDE_PLUGIN_DATA: dispatcherData, FORGE_PLUGIN_DATA: '' };
  const entry = {
    session_id: 'missing-binary-entry',
    cwd: pluginRoot,
    hook_event_name: 'UserPromptExpansion',
    command_name: 'plan-forge-flow:forge',
  };
  const entryResult = spawnSync(process.execPath, [join(scripts, 'claude-workflow-hook.mjs')], {
    input: JSON.stringify(entry), env, encoding: 'utf8',
  });
  assert.equal(entryResult.status, 0, entryResult.stderr);
  assert.equal(JSON.parse(entryResult.stdout).decision, 'block', 'entry fails closed when the RID binary is unavailable');

  const dispatcherUrl = pathToFileURL(join(scripts, 'claude-workflow-hook.mjs')).href;
  for (const [platform, architecture] of [['win32', 'arm64'], ['linux', 'x64'], ['linux', 'arm64'], ['darwin', 'x64'], ['darwin', 'arm64']]) {
    const program = `Object.defineProperty(process, 'platform', { value: '${platform}' });` +
                    `Object.defineProperty(process, 'arch', { value: '${architecture}' });` +
                    `await import(${JSON.stringify(dispatcherUrl)});`;
    const unsupported = spawnSync(process.execPath, ['--input-type=module', '--eval', program], {
      input: JSON.stringify(entry), env, encoding: 'utf8',
    });
    assert.equal(unsupported.status, 0, unsupported.stderr);
    const decision = JSON.parse(unsupported.stdout);
    assert.equal(decision.decision, 'block');
    assert.match(decision.reason, /supports only Windows x64/, `${platform}/${architecture} is rejected`);
  }

  const exit = {
    session_id: 'missing-binary-exit',
    cwd: pluginRoot,
    hook_event_name: 'PreToolUse',
    tool_name: 'ExitPlanMode',
    tool_input: { plan: '# Ordinary plan' },
  };
  const ordinaryExit = spawnSync(process.execPath, [join(scripts, 'claude-workflow-hook.mjs')], {
    input: JSON.stringify(exit), env, encoding: 'utf8',
  });
  assert.equal(ordinaryExit.status, 0, ordinaryExit.stderr);
  assert.equal(ordinaryExit.stdout, '', 'ordinary Plan Mode stays available without an activation');

  const activations = join(dispatcherData, 'claude-activations');
  mkdirSync(activations, { recursive: true });
  const key = createHash('sha256').update(exit.session_id).digest('hex');
  writeFileSync(join(activations, `${key}.json`), '{');
  const activeExit = spawnSync(process.execPath, [join(scripts, 'claude-workflow-hook.mjs')], {
    input: JSON.stringify(exit), env, encoding: 'utf8',
  });
  assert.equal(activeExit.status, 0, activeExit.stderr);
  assert.equal(JSON.parse(activeExit.stdout).hookSpecificOutput.permissionDecision, 'deny',
               'active Forge Plan Mode fails closed when the RID binary is unavailable');

  const stop = {
    session_id: 'missing-binary-stop',
    cwd: pluginRoot,
    hook_event_name: 'Stop',
    stop_hook_active: false,
  };
  const ordinaryStop = spawnSync(process.execPath, [join(scripts, 'claude-workflow-hook.mjs')], {
    input: JSON.stringify(stop), env, encoding: 'utf8',
  });
  assert.equal(ordinaryStop.status, 0, ordinaryStop.stderr);
  assert.equal(ordinaryStop.stdout, '', 'ordinary Stop stays available without an activation');

  const stopKey = createHash('sha256').update(stop.session_id).digest('hex');
  writeFileSync(join(activations, `${stopKey}.json`), '{');
  const activeStop = spawnSync(process.execPath, [join(scripts, 'claude-workflow-hook.mjs')], {
    input: JSON.stringify(stop), env, encoding: 'utf8',
  });
  assert.equal(activeStop.status, 0, activeStop.stderr);
  assert.equal(JSON.parse(activeStop.stdout).continue, false,
               'active Forge Stop fails visibly when the win-x64 binary is unavailable');
} finally {
  rmSync(dispatcherRoot, { recursive: true, force: true });
}

console.log('Plan Forge Claude asset tests passed.');
