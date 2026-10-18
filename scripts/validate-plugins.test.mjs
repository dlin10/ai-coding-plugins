import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import test from 'node:test';
import { validateCatalogCoverage, validateCatalogSuperset } from './validate-plugins.mjs';

function writeJson(path, value) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, JSON.stringify(value));
}

function createFixture(t) {
  const root = mkdtempSync(join(tmpdir(), 'validate-plugins-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  mkdirSync(join(root, 'plugins'), { recursive: true });
  writeJson(join(root, '.agents/plugins/marketplace.json'), { plugins: [] });
  writeJson(join(root, '.claude-plugin/marketplace.json'), { plugins: [] });
  writeJson(join(root, '.cursor-plugin/marketplace.json'), { plugins: [] });
  return root;
}

test('ignores directories that do not ship a host manifest', (t) => {
  const root = createFixture(t);
  mkdirSync(join(root, 'plugins/Common'));
  const errors = [];

  validateCatalogCoverage(root, (error) => errors.push(error));

  assert.deepEqual(errors, []);
});

test('reports a shipped host manifest that is absent from its catalog', (t) => {
  const root = createFixture(t);
  writeJson(join(root, 'plugins/example/.codex-plugin/plugin.json'), {
    name: 'example',
    version: '1.0.0',
  });
  const errors = [];

  validateCatalogCoverage(root, (error) => errors.push(error));

  assert.deepEqual(errors, [
    'plugins/example: ships .codex-plugin/plugin.json but is not listed in .agents/plugins/marketplace.json',
  ]);
});

test('lets a mod be listed in the Claude catalog alone', () => {
  const claude = { name: 'm', plugins: [{ name: 'pulse', source: './mods/pulse' }] };
  const cursor = { name: 'm', plugins: [] };
  const errors = [];

  validateCatalogSuperset(claude, cursor, (error) => errors.push(error));

  assert.deepEqual(errors, []);
});

test('reports a plugin listed in the Claude catalog but not in the Cursor one', () => {
  const claude = { name: 'm', plugins: [{ name: 'example', source: './plugins/example' }] };
  const cursor = { name: 'm', plugins: [] };
  const errors = [];

  validateCatalogSuperset(claude, cursor, (error) => errors.push(error));

  assert.deepEqual(errors, [
    'plugin "example" is in .claude-plugin/marketplace.json but missing from .cursor-plugin/marketplace.json',
  ]);
});
