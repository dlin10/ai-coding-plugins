import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import {
  stateFromApprovedEnvelope,
  validateBuilderSelectionBinding,
} from '../../plugins/plan-forge-flow/scripts/forge.mjs';
import { parseCodexDebugModels } from '../../plugins/plan-forge-flow/scripts/model-catalog.mjs';

const forge = fileURLToPath(new URL('../../plugins/plan-forge-flow/scripts/forge.mjs', import.meta.url));
const observedAt = '2026-07-27T00:00:00.000Z';
const humanPlanHash = 'a'.repeat(64);

function row(slug, priority) {
  return {
    slug,
    display_name: slug.toUpperCase(),
    description: `${slug} description`,
    default_reasoning_level: 'medium',
    supported_reasoning_levels: [
      { effort: 'low', description: 'Low' },
      { effort: 'medium', description: 'Medium' },
      { effort: 'high', description: 'High' },
      { effort: 'ultra', description: 'Ultra' },
    ],
    priority,
    visibility: 'list',
  };
}

function catalogJson() {
  return JSON.stringify({ models: [row('stronger', 1), row('weaker', 2)] });
}

function catalog() {
  return parseCodexDebugModels(catalogJson(), observedAt);
}

function approvedEnvelope(overrides = {}) {
  return {
    humanPlanHash,
    builderPlanHash: humanPlanHash,
    completedReviewRounds: 2,
    reviewer: { model: 'weaker', effort: 'low' },
    builder: { model: 'stronger', effort: 'high' },
    ...overrides,
  };
}

function implementationApproval(planHash = humanPlanHash) {
  return {
    kind: 'native-plan-implementation',
    planHash,
    approvedAt: '2026-07-27T00:01:00.000Z',
  };
}

test('approved-envelope boundary persists only reviewer/builder pins and binds builder to plan hash', () => {
  const state = stateFromApprovedEnvelope(approvedEnvelope(), catalog(), implementationApproval());
  assert.equal(state.phase, 'signoff');
  assert.equal(state.round, 2);
  assert.deepEqual(state.modelSelection, {
    reviewer: { model: 'weaker', effort: 'low' },
    builder: { model: 'stronger', effort: 'high' },
  });
  assert.equal('orchestrator' in state.modelSelection, false);
  assert.equal('sessionId' in state, false);
  assert.equal(state.builderSelectionPlanHash, humanPlanHash);
  assert.equal(state.implementationApproval.planHash, humanPlanHash);
  assert.deepEqual(validateBuilderSelectionBinding(state, humanPlanHash), {
    model: 'stronger',
    effort: 'high',
  });
  assert.throws(
    () => validateBuilderSelectionBinding(state, 'b'.repeat(64)),
    /does not match the current human plan/,
  );
});

test('approved-envelope boundary rejects builder and approval hash mismatches', () => {
  assert.throws(
    () => stateFromApprovedEnvelope(
      approvedEnvelope({ builderPlanHash: 'b'.repeat(64) }),
      catalog(),
      implementationApproval(),
    ),
    /builder selection is not bound/,
  );
  assert.throws(
    () => stateFromApprovedEnvelope(approvedEnvelope(), catalog(), implementationApproval('b'.repeat(64))),
    /implementation approval does not match/,
  );
});

test('reviewer and builder selection commands are read-only and use the live CLI catalog', () => {
  const dir = mkdtempSync(join(tmpdir(), 'forge-selection-'));
  const fakeCodex = join(dir, 'fake-codex.js');
  writeFileSync(fakeCodex, `console.log(${JSON.stringify(catalogJson())});\n`);
  const env = { ...process.env, FORGE_CODEX_PATH: fakeCodex };
  const run = (args) => {
    const result = spawnSync(process.execPath, [forge, ...args], { cwd: dir, env, encoding: 'utf8' });
    return {
      code: result.status,
      json: JSON.parse(result.stdout.trim().split('\n').at(-1)),
    };
  };

  let result = run([
    'configure-reviewer',
    '--reviewer-model', 'weaker',
    '--reviewer-effort', 'low',
  ]);
  assert.equal(result.code, 0);
  assert.equal(result.json.action, 'reviewer-selected');
  assert.deepEqual(result.json.reviewer, { model: 'weaker', effort: 'low' });

  result = run([
    'configure-reviewer',
    '--reviewer-model', 'weaker',
    '--reviewer-effort', 'low',
    '--user-override',
    '--user-note', 'legacy strength override',
  ]);
  assert.equal(result.code, 1);
  assert.match(result.json.error, /overrides are no longer supported/);

  result = run([
    'configure-builder',
    '--builder-model', 'stronger',
    '--builder-effort', 'high',
    '--plan-hash', humanPlanHash,
  ]);
  assert.equal(result.code, 0);
  assert.equal(result.json.action, 'builder-selected');
  assert.equal(result.json.humanPlanHash, humanPlanHash);

  result = run([
    'configure-reviewer',
    '--catalog', join(dir, 'stale-catalog.json'),
    '--reviewer-model', 'stronger',
    '--reviewer-effort', 'high',
  ]);
  assert.equal(result.code, 1);
  assert.match(result.json.error, /fresh codex debug models catalog/);
  assert.equal(existsSync(join(dir, '.forge')), false);
  assert.equal(existsSync(join(dir, 'PLAN.md')), false);
});
