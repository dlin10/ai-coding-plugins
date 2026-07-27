/**
 * Storage locations shared by the UserPromptSubmit hook and the forge CLI.
 *
 * Both processes must derive the same session-capture path or the handoff is
 * silently lost, so the resolution deliberately ignores Codex's PLUGIN_DATA:
 * Codex sets it only for hook subprocesses (and points it at a marketplace
 * qualified directory such as `plugins/data/plan-forge-flow-<marketplace>`),
 * which the CLI cannot reconstruct.
 */
import { createHash } from 'node:crypto';
import { join, resolve } from 'node:path';
import { homedir } from 'node:os';
import process from 'node:process';

export function codexHomeDir() {
  const override = process.env.CODEX_HOME?.trim();
  return override ? resolve(override) : join(homedir(), '.codex');
}

export function pluginDataDir() {
  const override = process.env.FORGE_PLUGIN_DATA?.trim();
  return override ? resolve(override) : join(codexHomeDir(), 'plugin-data', 'plan-forge-flow');
}

export function sessionContextDir() {
  return join(pluginDataDir(), 'session-context');
}

export function materializationJournalDir() {
  return join(pluginDataDir(), 'materialization-journal');
}

export function nonceTombstoneDir() {
  return join(pluginDataDir(), 'nonce-tombstones');
}

/** @param canonicalCwd Real path of the workspace (Git root when available). */
export function sessionContextPathFor(canonicalCwd) {
  const digest = createHash('sha256').update(canonicalCwd.toLowerCase()).digest('hex');
  return join(sessionContextDir(), `${digest}.json`);
}
