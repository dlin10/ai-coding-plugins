#!/usr/bin/env node

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const root = resolve(import.meta.dirname, '..', 'plugins', 'plan-forge-flow');
const markerPattern = /^ROSLYN: (?:USED — .+|FALLBACK — .+|NOT_APPLICABLE)$/gm;

function read(relative) {
  return readFileSync(join(root, relative), 'utf8').replace(/\r\n/g, '\n');
}

function assertContract(text, source) {
  for (const phrase of ['absolute', 'project/assembly', 'not applicable', 'not itself', 'build']) {
    assert.ok(text.toLowerCase().includes(phrase.toLowerCase()), `${source} includes ${phrase}`);
  }
  assert.match(text, /missing/iu, `${source} covers missing Roslyn`);
  assert.match(text, /unreachable/iu, `${source} covers unreachable Roslyn`);
  assert.match(text, /(?:another|different)\s+solution/iu, `${source} covers wrong-solution Roslyn`);
  assert.match(text, /inconclusive/iu, `${source} covers inconclusive Roslyn`);
  for (const marker of ['ROSLYN: USED', 'ROSLYN: FALLBACK', 'ROSLYN: NOT_APPLICABLE']) {
    assert.ok(text.includes(marker), `${source} documents ${marker}`);
  }
  assert.match(text, /exactly one|exactly 1/i, `${source} requires one audit marker`);
}

const shared = read('skills/forge/references/roslyn-first-review.md');
const cursor = read('cursor/skills/forge/references/roslyn-first-review.md');
const openAi = read('skills/forge/references/openai-reviewer.md');
assertContract(shared, 'shared reviewer contract');
assertContract(cursor, 'Cursor reviewer contract');
assert.match(shared, /post-doctor capability probe/i);
assert.match(shared, /never changes the doctor verdict/i);
assert.match(shared, /configured[\s\S]*never proves server capability or solution\s+identity/i);
assert.match(openAi, /every fresh OpenAI App Server reviewer prompt/);
assert.match(openAi, /exactly one/);

const codexReviewer = read('agents/forge_reviewer.toml');
const cursorReviewer = read('cursor/agents/forge-reviewer.md');
assertContract(codexReviewer, 'Codex reviewer');
assertContract(cursorReviewer, 'Cursor reviewer');

for (const effort of ['none', 'low', 'medium', 'high', 'xhigh', 'max']) {
  assertContract(read(`agents/forge-reviewer-${effort}.md`), `Claude reviewer ${effort}`);
}

for (const skill of ['skills/forge/SKILL.md', 'cursor/skills/forge/SKILL.md']) {
  const text = read(skill);
  assert.match(text, /optional.*Roslyn[\s\S]*capability\s+probe/i, `${skill} runs the post-doctor probe`);
  assert.match(text, /never changes the doctor verdict|without changing the[\s\S]*doctor verdict/i);
}

for (const sample of [
  'ROSLYN: USED — PlanForgeFlow.Cli / planforge',
  'ROSLYN: FALLBACK — server attached to another solution',
  'ROSLYN: NOT_APPLICABLE',
]) {
  assert.equal([...sample.matchAll(markerPattern)].length, 1);
}
assert.equal([...('ROSLYN: USED — A\nROSLYN: FALLBACK — B').matchAll(markerPattern)].length, 2,
  'multiple markers are detectable and invalid under the contract');

console.log('Plan Forge Roslyn reviewer contract tests passed.');
