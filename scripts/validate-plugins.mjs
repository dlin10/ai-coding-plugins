#!/usr/bin/env node
/**
 * Validates the Codex, Claude Code, and Cursor marketplace surfaces plus
 * cross-host and Roslyn extension version consistency.
 * Run: npm run validate:plugins
 */
import { readFileSync, existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve, relative, isAbsolute } from 'node:path';
import { fileURLToPath } from 'node:url';
import Ajv from 'ajv';
import addFormats from 'ajv-formats';

const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(__dirname, '..');

const PLUGIN_SCHEMA_URL =
  'https://raw.githubusercontent.com/cursor/plugins/main/schemas/plugin.schema.json';
const MARKETPLACE_SCHEMA_URL =
  'https://raw.githubusercontent.com/cursor/plugins/main/schemas/marketplace.schema.json';

const errors = [];

function fail(message) {
  errors.push(message);
}

function readJson(path) {
  try {
    return JSON.parse(readFileSync(path, 'utf8'));
  } catch (e) {
    fail(`${path}: invalid JSON — ${e.message}`);
    return null;
  }
}

async function fetchSchema(url) {
  const res = await fetch(url);
  if (!res.ok) {
    throw new Error(`Failed to fetch schema ${url}: ${res.status}`);
  }
  return res.json();
}

function hasFrontmatter(content, filePath, { requireName = true } = {}) {
  const normalized = content.replace(/\r\n/g, '\n');
  if (!normalized.startsWith('---\n')) {
    fail(`${filePath}: missing YAML frontmatter`);
    return false;
  }
  const end = normalized.indexOf('\n---\n', 4);
  if (end === -1) {
    fail(`${filePath}: unclosed YAML frontmatter`);
    return false;
  }
  const fm = normalized.slice(4, end);
  if (!/^description:\s*.+/m.test(fm)) {
    fail(`${filePath}: frontmatter must include description`);
    return false;
  }
  if (requireName && !/^name:\s*.+/m.test(fm)) {
    fail(`${filePath}: frontmatter must include name`);
    return false;
  }
  return true;
}

function assertRelativePath(path, context) {
  if (isAbsolute(path)) {
    fail(`${context}: absolute path not allowed: ${path}`);
    return false;
  }
  if (path.includes('..')) {
    fail(`${context}: path traversal not allowed: ${path}`);
    return false;
  }
  return true;
}

function validatePluginComponents(pluginDir) {
  const skillsDir = join(pluginDir, 'skills');
  if (existsSync(skillsDir)) {
    for (const entry of readdirSync(skillsDir, { withFileTypes: true })) {
      if (!entry.isDirectory()) continue;
      const skillFile = join(skillsDir, entry.name, 'SKILL.md');
      if (existsSync(skillFile)) {
        hasFrontmatter(readFileSync(skillFile, 'utf8'), relative(repoRoot, skillFile));
      }
    }
  }

  const commandsDir = join(pluginDir, 'commands');
  if (existsSync(commandsDir)) {
    for (const file of readdirSync(commandsDir)) {
      if (/\.(md|mdc|markdown|txt)$/i.test(file)) {
        const cmdFile = join(commandsDir, file);
        hasFrontmatter(readFileSync(cmdFile, 'utf8'), relative(repoRoot, cmdFile), {
          requireName: false,
        });
      }
    }
  }

  const hooksDir = join(pluginDir, 'hooks');
  if (existsSync(hooksDir)) {
    for (const file of readdirSync(hooksDir)) {
      if (!/^hooks(?:\.[a-z]+)?\.json$/i.test(file)) continue;
      const hooksPath = join(hooksDir, file);
      const hooks = readJson(hooksPath);
      if (file === 'hooks.cursor.json' && hooks && hooks.version !== 1) {
        fail(`${relative(repoRoot, hooksPath)}: Cursor hooks should include "version": 1`);
      }
    }
  }
}

function componentPaths(value) {
  if (typeof value === 'string') return [value];
  return Array.isArray(value) ? value : [];
}

function walkFiles(path) {
  if (!existsSync(path)) return [];
  const entries = readdirSync(path, { withFileTypes: true });
  return entries.flatMap((entry) => {
    const child = join(path, entry.name);
    return entry.isDirectory() ? walkFiles(child) : [child];
  });
}

function validateDeclaredComponents(pluginDir, manifest, manifestPath) {
  for (const [kind, value] of Object.entries({
    skills: manifest.skills,
    commands: manifest.commands,
    agents: manifest.agents,
  })) {
    for (const declaredPath of componentPaths(value)) {
      const normalized = declaredPath.replace(/^\.\//, '');
      if (!assertRelativePath(normalized, `${relative(repoRoot, manifestPath)} ${kind}`)) continue;
      const absolute = join(pluginDir, normalized);
      if (!existsSync(absolute)) {
        fail(`${relative(repoRoot, manifestPath)}: ${kind} path not found: ${declaredPath}`);
        continue;
      }
      const candidates = walkFiles(absolute);
      for (const file of candidates) {
        const name = file.split(/[\\/]/).at(-1);
        if (kind === 'skills' && name === 'SKILL.md') {
          hasFrontmatter(readFileSync(file, 'utf8'), relative(repoRoot, file));
        } else if (kind === 'commands' && /\.(md|mdc|markdown|txt)$/i.test(name)) {
          hasFrontmatter(readFileSync(file, 'utf8'), relative(repoRoot, file), { requireName: false });
        } else if (kind === 'agents' && /\.md$/i.test(name)) {
          hasFrontmatter(readFileSync(file, 'utf8'), relative(repoRoot, file));
        }
      }
    }
  }
}

function validatePluginManifest(pluginDir, manifestPath, hostKind) {
  const manifest = readJson(manifestPath);
  if (!manifest) return;

  if (
    hostKind !== 'claude' &&
    existsSync(join(pluginDir, 'skills')) &&
    componentPaths(manifest.skills).length === 0
  ) {
    fail(
      `${relative(repoRoot, manifestPath)}: plugins with a root skills/ directory must declare skills`,
    );
  }

  if (!manifest.name || !KEBAB_CASE.test(manifest.name)) {
    fail(`${relative(repoRoot, manifestPath)}: invalid plugin name "${manifest.name}"`);
  }

  if (manifest.hooks && typeof manifest.hooks === 'string') {
    assertRelativePath(manifest.hooks.replace(/^\.\//, ''), relative(repoRoot, manifestPath));
    const hooksFile = join(pluginDir, manifest.hooks);
    if (!existsSync(hooksFile)) {
      fail(`${relative(repoRoot, manifestPath)}: hooks file not found: ${manifest.hooks}`);
    }
  }

  if (manifest.skills && typeof manifest.skills === 'string') {
    assertRelativePath(manifest.skills.replace(/^\.\//, ''), relative(repoRoot, manifestPath));
    const skillsPath = join(pluginDir, manifest.skills);
    if (!existsSync(skillsPath)) {
      fail(`${relative(repoRoot, manifestPath)}: skills path not found: ${manifest.skills}`);
    }
  }

  validatePluginComponents(pluginDir);
  validateDeclaredComponents(pluginDir, manifest, manifestPath);

  if (hostKind === 'cursor' && manifest.name === 'plan-forge-flow') {
    if (manifest.minClientVersions?.cursor !== '3.15.6') {
      fail(`${relative(repoRoot, manifestPath)}: Plan Forge Flow requires minClientVersions.cursor 3.15.6`);
    }
    if (manifest.hooks) {
      fail(`${relative(repoRoot, manifestPath)}: Cursor Plan Forge Flow must not use hooks as an enforcement boundary`);
    }
    const reviewer = join(pluginDir, 'cursor', 'agents', 'forge-reviewer.md');
    const reviewerText = existsSync(reviewer) ? readFileSync(reviewer, 'utf8') : '';
    if (!/^model:\s*inherit\s*$/m.test(reviewerText) || !/^readonly:\s*true\s*$/m.test(reviewerText)) {
      fail(`${relative(repoRoot, reviewer)}: reviewer must declare model: inherit and readonly: true`);
    }
    if (!/ABSOLUTELY DO NOT mutate anything\./.test(reviewerText) || !/only an intent flag in Cursor/.test(reviewerText)) {
      fail(`${relative(repoRoot, reviewer)}: reviewer must state the advisory no-mutation contract`);
    }
    const builder = join(pluginDir, 'cursor', 'agents', 'forge-builder.md');
    const builderText = existsSync(builder) ? readFileSync(builder, 'utf8') : '';
    if (!/^model:\s*inherit\s*$/m.test(builderText) || !/^readonly:\s*false\s*$/m.test(builderText) || !/exactly the one locked plan step or one bounded fix-list/.test(builderText)) {
      fail(`${relative(repoRoot, builder)}: builder must be foreground, writable, inherited-model, and single-dispatch`);
    }
    const command = join(pluginDir, 'cursor', 'commands', 'forge.md');
    const commandText = existsSync(command) ? readFileSync(command, 'utf8') : '';
    if (!/\$\{CURSOR_PLUGIN_ROOT\}\/cursor\/skills\/forge\/SKILL\.md/.test(commandText) || !/--host cursor/.test(commandText) || !/`\/forge resume` recovery branch/.test(commandText)) {
      fail(`${relative(repoRoot, command)}: /forge must route to the Cursor skill, explicit Cursor host, and recovery-only resume branch`);
    }
    const skill = join(pluginDir, 'cursor', 'skills', 'forge', 'SKILL.md');
    const skillText = existsSync(skill) ? readFileSync(skill, 'utf8') : '';
    if (!/native plan creation is the terminal action/i.test(skillText) || !/normal flow does not require `\/forge resume`/i.test(skillText) || !/does not bind the later native file by path or hash/i.test(skillText)) {
      fail(`${relative(repoRoot, skill)}: Cursor Forge must declare chat-first review, terminal native creation, and recovery-only resume`);
    }
    const workflow = join(pluginDir, 'cursor', 'skills', 'forge', 'references', 'workflow.md');
    const workflowText = existsSync(workflow) ? readFileSync(workflow, 'utf8') : '';
    const planningStart = workflowText.indexOf('## 1. Chat planning and automatic review');
    const recoveryStart = workflowText.indexOf('## 3. `/forge resume` recovery');
    const buildStart = workflowText.indexOf('## 4. Local Build');
    if (!(planningStart >= 0 && planningStart < recoveryStart && recoveryStart < buildStart)) {
      fail(`${relative(repoRoot, workflow)}: Cursor workflow must put normal chat-first flow before recovery and Build`);
    } else {
      const normalFlow = workflowText.slice(planningStart, recoveryStart);
      const chatPlan = normalFlow.indexOf('Show the complete chat plan');
      const stage = normalFlow.indexOf('planforge plan stage');
      const reviewerDispatch = normalFlow.indexOf('fresh `forge-reviewer`');
      const record = normalFlow.indexOf('review record-response');
      const builderQuestion = normalFlow.indexOf('Ask separately for the implementation builder model and effort');
      const finalize = normalFlow.indexOf('planforge plan finalize');
      const ready = normalFlow.indexOf('successful `ready` result', finalize);
      const nativeCreation = normalFlow.indexOf('Create the registered editable native `.plan.md`', ready);
      if (!(chatPlan >= 0 && chatPlan < stage && stage < reviewerDispatch && reviewerDispatch < record && record < builderQuestion && builderQuestion < finalize && finalize < ready && ready < nativeCreation)) {
        fail(`${relative(repoRoot, workflow)}: normal flow must order chat display, stage, fresh review, response recording, builder question, finalize, ready, and native creation`);
      }
      if (!/Native creation is the terminal action/.test(normalFlow.slice(nativeCreation)) || /planforge (?:plan stage|review record-response|plan finalize)/.test(normalFlow.slice(nativeCreation))) {
        fail(`${relative(repoRoot, workflow)}: native plan creation must be the terminal normal-flow action`);
      }
    }
    const nativeContract = join(pluginDir, 'cursor', 'skills', 'forge', 'references', 'native-plan-contract.md');
    const nativeContractText = existsSync(nativeContract) ? readFileSync(nativeContract, 'utf8') : '';
    if (!/Before any repository write, run Plan Forge `plan materialize/.test(nativeContractText) || !/using the installed launcher/.test(nativeContractText) || /not build-ready until `\/forge resume`/.test(nativeContractText) || /using the installed launcher at/i.test(nativeContractText)) {
      fail(`${relative(repoRoot, nativeContract)}: native preamble must contain the materialize gate without a resume gate or launcher-path substitution`);
    }
  }

  if (hostKind === 'claude' && manifest.name === 'plan-forge-flow') {
    const efforts = ['none', 'low', 'medium', 'high', 'xhigh', 'max'];
    const expectedReviewerTools = [
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
    const markdownAgents = readdirSync(join(pluginDir, 'agents')).filter((file) => file.endsWith('.md'));
    if (markdownAgents.length !== 12) {
      fail(`${relative(repoRoot, pluginDir)}: Claude Plan Forge Flow must ship exactly 12 Markdown agents`);
    }
    for (const role of ['reviewer', 'builder']) {
      for (const effort of efforts) {
        const agentPath = join(pluginDir, 'agents', `forge-${role}-${effort}.md`);
        const text = existsSync(agentPath) ? readFileSync(agentPath, 'utf8').replace(/\r\n/g, '\n') : '';
        const frontmatter = text.match(/^---\n([\s\S]*?)\n---\n/)?.[1] ?? '';
        const model = frontmatter.match(/^model:\s*(.+)$/m)?.[1];
        const actualEffort = frontmatter.match(/^effort:\s*(.+)$/m)?.[1];
        const tools = frontmatter.match(/^tools:\s*(.+)$/m)?.[1]?.split(',').map((tool) => tool.trim());
        if (!text || model || actualEffort !== (effort === 'none' ? undefined : effort)) {
          fail(`${relative(repoRoot, agentPath)}: Claude agent must omit model and encode its effort suffix exactly`);
        }
        if (role === 'reviewer' && JSON.stringify(tools) !== JSON.stringify(expectedReviewerTools)) {
          fail(`${relative(repoRoot, agentPath)}: reviewer must use the exact built-in and Roslyn least-privilege allowlist`);
        }
        if (role === 'builder' && tools) {
          fail(`${relative(repoRoot, agentPath)}: builder must inherit normal tools and user permissions`);
        }
      }
    }

    const claudeHooksPath = join(pluginDir, 'hooks', 'hooks.claude.json');
    const claudeHooks = readJson(claudeHooksPath)?.hooks;
    const postHook = claudeHooks?.PostToolUse?.[0];
    const stopHook = claudeHooks?.SubagentStop?.[0];
    if (postHook?.matcher !== 'Agent' || stopHook?.matcher !== '^plan-forge-flow:forge-(reviewer|builder)-(none|low|medium|high|xhigh|max)$' ||
        !postHook?.hooks?.every((hook) => hook.async === true) || !stopHook?.hooks?.every((hook) => hook.async === true)) {
      fail(`${relative(repoRoot, claudeHooksPath)}: Claude evidence hooks must be scoped and non-blocking`);
    }
  }
}

const KEBAB_CASE = /^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$/;

/** Extract a version with a regex from a file, failing when absent. */
function extractVersion(path, regex, what) {
  if (!existsSync(path)) {
    fail(`${relative(repoRoot, path)}: file not found (needed for ${what})`);
    return null;
  }
  const match = readFileSync(path, 'utf8').match(regex);
  if (!match) {
    fail(`${relative(repoRoot, path)}: could not find ${what}`);
    return null;
  }
  return match[1];
}

/** Deep equality for marketplace plugin entries (order-insensitive object keys). */
function deepEqual(a, b) {
  if (a === b) return true;
  if (typeof a !== typeof b) return false;
  if (a === null || b === null) return a === b;
  if (Array.isArray(a)) {
    if (!Array.isArray(b) || a.length !== b.length) return false;
    return a.every((item, i) => deepEqual(item, b[i]));
  }
  if (typeof a === 'object') {
    const aKeys = Object.keys(a).sort();
    const bKeys = Object.keys(b).sort();
    if (aKeys.length !== bKeys.length) return false;
    if (!aKeys.every((k, i) => k === bKeys[i])) return false;
    return aKeys.every((k) => deepEqual(a[k], b[k]));
  }
  return false;
}

/**
 * Cursor catalog may be a strict superset of Claude catalog (Cursor-only plugins).
 * Shared entries must be deep-equal; Claude-only entries are an error.
 */
function validateCatalogSuperset(claude, cursor) {
  if (claude.name !== cursor.name) {
    fail(
      `marketplace name mismatch: Claude "${claude.name}" vs Cursor "${cursor.name}"`,
    );
  }
  const claudeOwner = JSON.stringify(claude.owner ?? null);
  const cursorOwner = JSON.stringify(cursor.owner ?? null);
  if (claudeOwner !== cursorOwner) {
    fail('marketplace owner mismatch between Claude and Cursor catalogs');
  }

  const cursorByName = new Map(
    (cursor.plugins ?? []).map((p) => [p.name, p]),
  );
  for (const entry of claude.plugins ?? []) {
    const cursorEntry = cursorByName.get(entry.name);
    if (!cursorEntry) {
      fail(
        `plugin "${entry.name}" is in .claude-plugin/marketplace.json but missing from .cursor-plugin/marketplace.json`,
      );
      continue;
    }
    if (!deepEqual(entry, cursorEntry)) {
      fail(
        `plugin "${entry.name}" differs between Claude and Cursor marketplace catalogs`,
      );
    }
  }
}

/** Cross-host sync checks: catalog superset, plugin versions equal, roslyn-mcp extension version consistent. */
function validateSync() {
  // 1. Cursor marketplace may be a strict superset of Claude marketplace
  //    (Cursor-only plugins). Shared entries must be deep-equal.
  const claudeCatalog = join(repoRoot, '.claude-plugin', 'marketplace.json');
  const cursorCatalog = join(repoRoot, '.cursor-plugin', 'marketplace.json');
  const codexCatalog = join(repoRoot, '.agents', 'plugins', 'marketplace.json');
  if (!existsSync(claudeCatalog)) {
    fail('Missing .claude-plugin/marketplace.json');
  } else if (existsSync(cursorCatalog)) {
    const claude = readJson(claudeCatalog);
    const cursor = readJson(cursorCatalog);
    if (claude && cursor) {
      validateCatalogSuperset(claude, cursor);
      const codex = readJson(codexCatalog);
      if (codex && codex.name !== claude.name) {
        fail(`marketplace name mismatch: Codex "${codex.name}" vs Claude "${claude.name}"`);
      }
    }
  }

  // 2. Where a plugin ships multiple manifests, all versions must match.
  const pluginsDir = join(repoRoot, 'plugins');
  for (const entry of readdirSync(pluginsDir, { withFileTypes: true })) {
    if (!entry.isDirectory()) continue;
    const claudeManifest = join(pluginsDir, entry.name, '.claude-plugin', 'plugin.json');
    const cursorManifest = join(pluginsDir, entry.name, '.cursor-plugin', 'plugin.json');
    const codexManifest = join(pluginsDir, entry.name, '.codex-plugin', 'plugin.json');
    const manifests = [codexManifest, claudeManifest, cursorManifest]
      .filter(existsSync)
      .map(path => ({ path, manifest: readJson(path) }))
      .filter(item => item.manifest);
    const versions = new Set(manifests.map(item => item.manifest.version));
    if (versions.size > 1) {
      fail(
        `plugins/${entry.name}: host manifest versions differ (${manifests.map(item => `${relative(repoRoot, item.path)}=${item.manifest.version}`).join(', ')})`,
      );
    }
  }

  // 3 + 4. roslyn-mcp: the vsixmanifest Identity Version is the single source of
  // truth for the extension version; README and the two C# strings must match it.
  const roslynDir = join(pluginsDir, 'roslyn-mcp');
  const vsixVersion = extractVersion(
    join(roslynDir, 'extension', 'src', 'RoslynMcpExtension', 'source.extension.vsixmanifest'),
    /<Identity[^>]*\bVersion="(\d+\.\d+\.\d+)"/,
    'Identity Version',
  );
  if (vsixVersion) {
    const checks = [
      {
        path: join(roslynDir, 'README.md'),
        regex: /\*\*v(\d+\.\d+\.\d+)\*\*/,
        what: 'bundled VSIX version (**vX.Y.Z**)',
      },
      {
        path: join(roslynDir, 'extension', 'src', 'RoslynMcpExtension', 'RoslynMcpPackage.cs'),
        regex: /InstalledProductRegistration\([^)]*"(\d+\.\d+\.\d+)"\)/,
        what: 'InstalledProductRegistration version',
      },
      {
        path: join(roslynDir, 'extension', 'src', 'RoslynMcpExtension.Server', 'Program.cs'),
        regex: /Version = "(\d+\.\d+\.\d+)"/,
        what: 'ServerInfo.Version',
      },
    ];
    for (const { path, regex, what } of checks) {
      const version = extractVersion(path, regex, what);
      if (version && version !== vsixVersion) {
        fail(
          `${relative(repoRoot, path)}: ${what} is ${version} but source.extension.vsixmanifest says ${vsixVersion}`,
        );
      }
    }
  }
}

function validateClaudeMarketplace() {
  const marketplacePath = join(repoRoot, '.claude-plugin', 'marketplace.json');
  const marketplace = readJson(marketplacePath);
  if (!marketplace) return;

  for (const entry of marketplace.plugins ?? []) {
    if (!entry.name || !KEBAB_CASE.test(entry.name)) {
      fail(`Claude marketplace entry has invalid name: ${entry.name}`);
    }
    if (typeof entry.source !== 'string' || !assertRelativePath(entry.source, `Claude plugin "${entry.name}"`)) continue;
    const pluginDir = resolve(repoRoot, entry.source);
    const manifestPath = join(pluginDir, '.claude-plugin', 'plugin.json');
    if (!existsSync(manifestPath)) {
      fail(`Claude plugin "${entry.name}": missing .claude-plugin/plugin.json`);
      continue;
    }
    const manifest = readJson(manifestPath);
    if (manifest && manifest.name !== entry.name) {
      fail(`Claude plugin "${entry.name}": manifest name is "${manifest.name}"`);
    }
    validatePluginManifest(pluginDir, manifestPath, 'claude');
  }
}

function validateCodexMarketplace() {
  const marketplacePath = join(repoRoot, '.agents', 'plugins', 'marketplace.json');
  const marketplace = readJson(marketplacePath);
  if (!marketplace) return;
  if (!marketplace.name || !Array.isArray(marketplace.plugins)) {
    fail('Codex marketplace requires name and plugins[]');
    return;
  }

  for (const entry of marketplace.plugins) {
    const sourcePath = entry.source?.path;
    if (!entry.name || !KEBAB_CASE.test(entry.name)) {
      fail(`Codex marketplace entry has invalid name: ${entry.name}`);
    }
    if (entry.source?.source !== 'local' || typeof sourcePath !== 'string' || !assertRelativePath(sourcePath, `Codex plugin "${entry.name}"`)) {
      fail(`Codex plugin "${entry.name}": source must be a relative local path`);
      continue;
    }
    if (!['NOT_AVAILABLE', 'AVAILABLE', 'INSTALLED_BY_DEFAULT'].includes(entry.policy?.installation)) {
      fail(`Codex plugin "${entry.name}": invalid installation policy`);
    }
    if (!['ON_INSTALL', 'ON_USE'].includes(entry.policy?.authentication)) {
      fail(`Codex plugin "${entry.name}": invalid authentication policy`);
    }
    if (!entry.category) fail(`Codex plugin "${entry.name}": category is required`);

    const pluginDir = resolve(repoRoot, sourcePath);
    const manifestPath = join(pluginDir, '.codex-plugin', 'plugin.json');
    if (!existsSync(manifestPath)) {
      fail(`Codex plugin "${entry.name}": missing .codex-plugin/plugin.json`);
      continue;
    }
    const manifest = readJson(manifestPath);
    if (!manifest) continue;
    if (manifest.name !== entry.name) {
      fail(`Codex plugin "${entry.name}": manifest name is "${manifest.name}"`);
    }
    if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/.test(manifest.version ?? '')) {
      fail(`Codex plugin "${entry.name}": version must be semver`);
    }
    for (const field of ['displayName', 'shortDescription', 'longDescription', 'developerName', 'category', 'capabilities', 'defaultPrompt']) {
      if (!manifest.interface?.[field]) fail(`Codex plugin "${entry.name}": interface.${field} is required`);
    }
    validatePluginManifest(pluginDir, manifestPath, 'codex');
  }
}

async function main() {
  const ajv = new Ajv({ allErrors: true, strict: false });
  addFormats(ajv);

  const [pluginSchema, marketplaceSchema] = await Promise.all([
    fetchSchema(PLUGIN_SCHEMA_URL),
    fetchSchema(MARKETPLACE_SCHEMA_URL),
  ]);

  const validatePlugin = ajv.compile(pluginSchema);
  const validateMarketplace = ajv.compile(marketplaceSchema);

  const marketplacePath = join(repoRoot, '.cursor-plugin', 'marketplace.json');
  if (!existsSync(marketplacePath)) {
    fail('Missing .cursor-plugin/marketplace.json');
  } else {
    const marketplace = readJson(marketplacePath);
    if (marketplace) {
      if (!validateMarketplace(marketplace)) {
        for (const err of validateMarketplace.errors ?? []) {
          fail(`marketplace.json: ${err.instancePath || '/'} ${err.message}`);
        }
      }

      for (const entry of marketplace.plugins ?? []) {
        if (!entry.name || !KEBAB_CASE.test(entry.name)) {
          fail(`marketplace entry has invalid name: ${entry.name}`);
        }
        if (!assertRelativePath(entry.source, `marketplace plugin "${entry.name}"`)) continue;

        const pluginDir = resolve(repoRoot, entry.source);
        const cursorManifestPath = join(pluginDir, '.cursor-plugin', 'plugin.json');
        const claudeManifestPath = join(pluginDir, '.claude-plugin', 'plugin.json');

        // Cursor reads .cursor-plugin/plugin.json when present, and otherwise
        // falls back to .claude-plugin/plugin.json (as it does for Claude-only plugins).
        const isCursorManifest = existsSync(cursorManifestPath);
        const manifestPath = isCursorManifest ? cursorManifestPath : claudeManifestPath;
        if (!existsSync(manifestPath)) {
          fail(
            `Plugin "${entry.name}": missing .cursor-plugin/plugin.json or .claude-plugin/plugin.json`,
          );
          continue;
        }

        const manifest = readJson(manifestPath);

        // Only the Cursor manifest is validated against the Cursor plugin schema;
        // the Claude manifest uses a different schema.
        if (isCursorManifest && manifest && !validatePlugin(manifest)) {
          for (const err of validatePlugin.errors ?? []) {
            fail(`${entry.name}/plugin.json: ${err.instancePath || '/'} ${err.message}`);
          }
        }

        if (manifest && entry.name !== manifest.name) {
          fail(
            `Plugin "${entry.name}": marketplace name does not match plugin.json name "${manifest.name}"`,
          );
        }

        validatePluginManifest(pluginDir, manifestPath, isCursorManifest ? 'cursor' : 'claude');
      }
    }
  }

  validateClaudeMarketplace();
  validateCodexMarketplace();
  validateSync();

  if (errors.length > 0) {
    console.error('Plugin validation failed:\n');
    for (const e of errors) console.error(`  - ${e}`);
    process.exit(1);
  }

  console.log('Plugin validation passed.');
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
