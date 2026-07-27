#!/usr/bin/env node
/**
 * plan-forge-flow deterministic CLI.
 *
 * Owns every mechanical state transition of the /forge workflow (round caps,
 * verdict parsing, task identity, dispatch/consume lifecycle, build baseline,
 * git-exclude block) so the orchestrating agent supplies judgment only.
 *
 * Contract: machine-readable JSON on stdout (one object, last line), human
 * progress on stderr. Exit codes: 0 ok, 1 usage/IO/environment or unexpected
 * failure, 2 invalid verdict, 3 state precondition violated.
 */
import {
  existsSync,
  closeSync,
  mkdirSync,
  openSync,
  readFileSync,
  writeFileSync,
  renameSync,
  readdirSync,
  rmSync,
  unlinkSync,
  realpathSync,
  statSync,
} from 'node:fs';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { basename, dirname, join, resolve, isAbsolute, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { homedir } from 'node:os';
import process from 'node:process';
import {
  CodexDebugModelsProvider,
  ModelCatalogResolver,
  runCodexCli,
  validateModelSelection,
} from './model-catalog.mjs';
import {
  createPlanUxState,
  effortPicker,
  finalProposedPlanOutput,
  issueApprovalEnvelope,
  modelPicker,
  recordBuilderSelection,
  recordFullPlanPreview,
} from './native-plan-ux.mjs';
import { collaborationModeForTurn, readTranscript } from './transcript-auth.mjs';
import {
  materializationJournalDir,
  nonceTombstoneDir,
  sessionContextPathFor,
} from './plugin-paths.mjs';
import { requireDefaultMode } from './mode-gate.mjs';
import {
  extractApprovalWrapper,
  repositoryIdentityEquals,
} from './resume-envelope.mjs';

// ---------------------------------------------------------------------------
// Constants

const STATE_VERSION = 2;
const OWNED_MARKER = '<!-- plan-forge-flow:owned -->';
const GENERATED_MARKER = '# plan-forge-flow:generated';
const EXCLUDE_BEGIN = '# >>> plan-forge-flow (managed) >>>';
const EXCLUDE_END = '# <<< plan-forge-flow (managed) <<<';
// Only `.forge/` needs excluding now — the subagents live in the global
// ~/.codex/agents dir, never in the target repo's working tree.
const EXCLUDE_ENTRIES = ['.forge/'];
const DEFAULT_MAX_PLAN_ROUNDS = 5;
const MODEL_RE = /^[A-Za-z0-9][A-Za-z0-9._\[\]=,-]*$/;
const AGENT_ROLES = ['reviewer', 'builder'];
const VERDICT_RE = /^VERDICT:\s*(APPROVED|REVISE)\s*$/;
const COVERAGE_RE = /^COVERAGE:\s*(FULL|PARTIAL\s*[—-].+)\s*$/;
const PHASES = ['grill', 'review', 'signoff', 'build', 'code-review', 'done', 'done-with-findings'];
const HEAD_BASE_REF_NAME = 'refs/plan-forge/head-base';
const WORKTREE_BASE_REF_NAME = 'refs/plan-forge/worktree-base';
const REVIEW_TEXT_LIMIT = 100 * 1024;
const REVIEW_AGGREGATE_LIMIT = 1024 * 1024;
const GIT_MAX_BUFFER = 64 * 1024 * 1024;
// The only inactivity constant Codex documents: app-server unloads an
// unsubscribed thread after 30 min. This is a hook-health heuristic, not a
// session TTL; Codex does not expire sessions because the user is idle.
const DEFAULT_SESSION_MAX_AGE_MS = 30 * 60 * 1000;
const MAX_REPLAY_KEYS = 100;
const BOOLEAN_FLAGS = new Set([
  'amendment',
  'cancel',
  'conflict',
  'delete-artifacts',
  'fix',
  'force',
  'full',
  'purge-agents',
  'purge-ledger',
  'relock',
  'retry',
  'user-override',
]);
const SENSITIVE_BASENAME_RE =
  /^(?:\.env(?:\..*)?|[^/]+\.env|\.npmrc|\.pypirc|\.netrc|id_[^/]+|service[-_.]?account(?:[^/]*)\.json|keystore\.jks|appsettings\.[^/]+\.json|credentials|\.git-credentials|secrets?\.(?:json|ya?ml|toml)|kubeconfig|terraform\.tfstate(?:\.backup)?)$/i;
const SENSITIVE_EXTENSION_RE = /\.(?:pem|key|p12|pfx|jks)$/i;
const PRIVATE_KEY_RE = /-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----/;
const AWS_ACCESS_KEY_RE = /\bAKIA[0-9A-Z]{16}\b/;
const SECRET_KEYWORD_RE =
  /(?:api[_-]?key|secret|token|password|passwd|credential|private[_-]?key)/i;
const ASSIGNMENT_RE =
  /^[\s\-*]*["']?([A-Za-z0-9_.$-]+)["']?\s*[:=]\s*["']?([^"'\s,;]{20,})["']?/;
const DECLARATION_PREFIX_RE =
  /^\s*(?:(?:export|public|private|protected|internal|static|readonly|final|const|let|var|val|def|string|self\.|this\.)\s+)+/i;
const STATE_LOCK_CRASH_EXIT_CODE = 87;

// ---------------------------------------------------------------------------
// Small helpers (exported for unit tests)

export function sha256(data) {
  return createHash('sha256').update(data).digest('hex');
}

export function classifySensitivePath(path) {
  const normalized = path.replaceAll('\\', '/');
  const name = basename(normalized);
  return SENSITIVE_BASENAME_RE.test(name) ||
    SENSITIVE_EXTENSION_RE.test(name) ||
    /(?:^|\/)\.docker\/config\.json$/i.test(normalized) ||
    /(?:^|\/)\.kube\/config$/i.test(normalized);
}

function hasSecretEntropy(value) {
  const classes = [
    /[a-z]/.test(value),
    /[A-Z]/.test(value),
    /\d/.test(value),
    /[+/=_-]/.test(value),
  ].filter(Boolean).length;
  return classes >= 3 || /^[a-f0-9]{32,}$/i.test(value);
}

const stripDeclarationPrefix = (line) => line.replace(DECLARATION_PREFIX_RE, '');

export function classifySensitiveContent(data) {
  const text = Buffer.isBuffer(data) ? data.toString('utf8') : String(data);
  if (PRIVATE_KEY_RE.test(text)) return 'private-key';
  if (AWS_ACCESS_KEY_RE.test(text)) return 'aws-access-key';
  for (const line of text.split('\n')) {
    if (line.length > 4096 || !SECRET_KEYWORD_RE.test(line)) continue;
    const match = ASSIGNMENT_RE.exec(stripDeclarationPrefix(line));
    if (!match || !SECRET_KEYWORD_RE.test(match[1])) continue;
    if (hasSecretEntropy(match[2])) return 'high-entropy-assignment';
  }
  return null;
}

/** Last non-empty line decides; restrained Markdown decoration is tolerated. */
export function parseVerdict(text) {
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  for (let i = lines.length - 1; i >= 0; i--) {
    const line = lines[i].trim();
    if (line === '') continue;
    const m = VERDICT_RE.exec(line.replace(/^\*\*(.*)\*\*$/, '$1').replace(/[.!]$/, ''));
    return m ? m[1] : null;
  }
  return null;
}

/**
 * Code-stage coverage contract: exactly one COVERAGE line, positioned before
 * the final verdict line. Returns 'FULL' | 'PARTIAL' | null (contract broken).
 */
export function parseCoverage(text) {
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  let verdictIdx = -1;
  for (let i = lines.length - 1; i >= 0; i--) {
    if (lines[i].trim() === '') continue;
    verdictIdx = i;
    break;
  }
  const hits = [];
  for (let i = 0; i < lines.length; i++) {
    const m = COVERAGE_RE.exec(lines[i].trim());
    if (m) hits.push({ index: i, kind: m[1].startsWith('FULL') ? 'FULL' : 'PARTIAL' });
  }
  if (hits.length !== 1) return null;
  if (verdictIdx !== -1 && hits[0].index >= verdictIdx) return null;
  return hits[0].kind;
}

export function validateModelId(model) {
  if (model === 'inherit') return true;
  return MODEL_RE.test(model);
}

/**
 * Where the shared forge subagents are installed so Codex registers them at
 * task start (global user scope, not the target repo). Overridable via
 * FORGE_AGENTS_DIR for tests.
 */
export function globalAgentsDir() {
  const override = process.env.FORGE_AGENTS_DIR;
  if (override && override.trim()) return resolve(override.trim());
  return join(homedir(), '.codex', 'agents');
}

/** Rewrite (never duplicate) the managed block; preserves surrounding lines. */
export function upsertExcludeBlock(excludePath) {
  mkdirSync(dirname(excludePath), { recursive: true });
  const existing = existsSync(excludePath) ? readFileSync(excludePath, 'utf8') : '';
  const block = [EXCLUDE_BEGIN, ...EXCLUDE_ENTRIES, EXCLUDE_END].join('\n');
  const b = existing.indexOf(EXCLUDE_BEGIN);
  const e = existing.indexOf(EXCLUDE_END);
  const next =
    b !== -1 && e !== -1 && e > b
      ? existing.slice(0, b) + block + existing.slice(e + EXCLUDE_END.length)
      : existing + (existing && !existing.endsWith('\n') ? '\n' : '') + block + '\n';
  writeFileSync(excludePath, next);
}

export function removeExcludeBlock(excludePath) {
  if (!existsSync(excludePath)) return;
  const existing = readFileSync(excludePath, 'utf8');
  const b = existing.indexOf(EXCLUDE_BEGIN);
  const e = existing.indexOf(EXCLUDE_END);
  if (b === -1 || e === -1 || e <= b) return;
  let head = existing.slice(0, b);
  let tail = existing.slice(e + EXCLUDE_END.length);
  if (tail.startsWith('\n')) tail = tail.slice(1);
  writeFileSync(excludePath, head + tail);
}

/**
 * Parse the numbered items under "## Approach". Numbers must run 1..N in
 * order — ambiguity here is a plan-authoring error, not something to guess at.
 */
export function parseApproachTasks(planText) {
  const text = planText.replace(/\r\n/g, '\n');
  const sectionMatch = /^## Approach\s*$/m.exec(text);
  if (!sectionMatch) throw new Error('PLAN.md has no "## Approach" section');
  const sectionStart = sectionMatch.index + sectionMatch[0].length;
  const nextSection = /^## /m.exec(text.slice(sectionStart + 1));
  const section = text.slice(
    sectionStart,
    nextSection ? sectionStart + 1 + nextSection.index : text.length,
  );
  const lines = section.split('\n');
  const tasks = [];
  let current = null;
  for (const line of lines) {
    const m = /^(\d+)\.\s+(.*)$/.exec(line);
    if (m) {
      if (current) tasks.push(current);
      current = { number: Number(m[1]), title: m[2].trim(), body: [line] };
    } else if (current) {
      current.body.push(line);
    }
  }
  if (current) tasks.push(current);
  if (tasks.length === 0) throw new Error('"## Approach" contains no numbered steps');
  tasks.forEach((t, i) => {
    if (t.number !== i + 1) {
      throw new Error(`Approach steps must be numbered 1..N in order (found ${t.number} at position ${i + 1})`);
    }
  });
  return tasks.map((t) => ({
    number: t.number,
    title: t.title,
    hash: sha256(t.body.join('\n').trimEnd()),
  }));
}

// ---------------------------------------------------------------------------
// CLI plumbing

function out(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n');
}

function note(msg) {
  process.stderr.write(msg + '\n');
}

class CliError extends Error {
  constructor(code, message, extra = {}) {
    super(message);
    this.code = code;
    this.extra = extra;
  }
}

function parseArgs(argv) {
  const flags = {};
  const positional = [];
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const equals = a.indexOf('=');
      if (equals !== -1) {
        flags[a.slice(2, equals)] = a.slice(equals + 1);
        continue;
      }
      const key = a.slice(2);
      const next = argv[i + 1];
      if (!BOOLEAN_FLAGS.has(key) && next !== undefined) {
        flags[key] = next;
        i++;
      } else {
        flags[key] = true;
      }
    } else {
      positional.push(a);
    }
  }
  return { flags, positional };
}

function git(cwd, args, opts = {}) {
  return execFileSync('git', args, {
    cwd,
    encoding: 'utf8',
    maxBuffer: GIT_MAX_BUFFER,
    ...opts,
  }).trimEnd();
}

function tryGit(cwd, args) {
  try {
    return git(cwd, args, { stdio: ['ignore', 'pipe', 'ignore'] });
  } catch (error) {
    if (error?.code === 'ENOBUFS' || /maxBuffer|ENOBUFS/i.test(error?.message ?? '')) {
      throw new CliError(1, `git output exceeded the ${GIT_MAX_BUFFER}-byte safety buffer; refusing a partial result`);
    }
    return null;
  }
}

function gitToFile(cwd, args, target, options = {}) {
  const fd = openSync(target, options.append ? 'a' : 'w');
  try {
    execFileSync('git', args, {
      cwd,
      encoding: 'utf8',
      maxBuffer: GIT_MAX_BUFFER,
      stdio: ['ignore', fd, 'pipe'],
    });
  } catch (error) {
    if (error?.code === 'ENOBUFS' || /maxBuffer|ENOBUFS/i.test(error?.message ?? '')) {
      throw new CliError(1, 'git diff output was truncated; review preparation failed closed');
    }
    const stderr = error?.stderr?.toString().trim().slice(0, 500);
    throw new CliError(
      1,
      `git ${args[0]} failed while preparing review evidence${stderr ? `: ${stderr}` : ''}`,
    );
  } finally {
    closeSync(fd);
  }
}

function gitRoot(cwd) {
  try {
    const root = git(cwd, ['rev-parse', '--show-toplevel'], { stdio: ['ignore', 'pipe', 'ignore'] });
    return realpathSync(root);
  } catch {
    return realpathSync(cwd);
  }
}

function processIsAlive(pid) {
  if (!Number.isInteger(pid) || pid < 1) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error?.code === 'EPERM';
  }
}

function parseStateLock(path, label) {
  let value;
  try {
    value = JSON.parse(readFileSync(path, 'utf8'));
  } catch {
    throw new CliError(1, `${label} is unreadable: ${path}`);
  }
  if (!value || typeof value !== 'object' || Array.isArray(value) ||
      !Number.isInteger(value.pid) || value.pid < 1 ||
      !Number.isFinite(value.ts) ||
      typeof value.token !== 'string' || !value.token) {
    throw new CliError(1, `${label} has an invalid schema: ${path}`);
  }
  return value;
}

function sameLock(left, right) {
  return Boolean(left && right && left.pid === right.pid && left.token === right.token);
}

function stateLockCrash(label) {
  if (process.env.FORGE_STATE_LOCK_CRASH_AT === label) {
    process.exit(STATE_LOCK_CRASH_EXIT_CODE);
  }
}

export class Workspace {
  constructor(cwd) {
    if (!existsSync(cwd)) throw new CliError(1, `cwd does not exist: ${cwd}`);
    this.requestedCwd = realpathSync(cwd);
    this.cwd = gitRoot(this.requestedCwd);
    this.forgeDir = join(this.cwd, '.forge');
    this.statePath = join(this.forgeDir, 'state.json');
    this.critiquesDir = join(this.forgeDir, 'critiques');
    this.planPath = join(this.cwd, 'PLAN.md');
    this.logPath = join(this.cwd, 'PLAN-REVIEW-LOG.md');
    this.lockPath = join(this.forgeDir, 'lock');
  }

  excludePath() {
    const rel = tryGit(this.cwd, ['rev-parse', '--git-path', 'info/exclude']);
    if (!rel) return null;
    return isAbsolute(rel) ? rel : join(this.cwd, rel);
  }

  hasState() {
    return existsSync(this.statePath);
  }

  loadState() {
    if (!this.hasState()) {
      throw new CliError(3, 'no forge state — run resume from an authenticated native implementation prompt first');
    }
    const state = JSON.parse(readFileSync(this.statePath, 'utf8'));
    if (state.version !== STATE_VERSION) {
      throw new CliError(3, `unsupported forge state version ${JSON.stringify(state.version)}; expected ${STATE_VERSION}`);
    }
    state.maxFixRounds ??= 3;
    state.maxBuildRetries ??= 3;
    state.maxVerdictRetries ??= 2;
    state.implementationApproval ??= null;
    state.builderSelectionPlanHash ??= null;
    if (state.modelSelection?.orchestrator) delete state.modelSelection.orchestrator;
    state.replayKeys = (state.replayKeys ?? []).slice(-MAX_REPLAY_KEYS);
    delete state.baseRef;
    delete state.planHash;
    return state;
  }

  saveState(state) {
    state.updatedAt = new Date().toISOString();
    mkdirSync(this.forgeDir, { recursive: true });
    const tmp = this.statePath + '.tmp';
    writeFileSync(tmp, JSON.stringify(state, null, 2) + '\n');
    renameSync(tmp, this.statePath);
  }

  appendEvent(cmd, summary = {}) {
    if (!existsSync(this.forgeDir)) return;
    const line = JSON.stringify({ ts: new Date().toISOString(), cmd, ...summary });
    writeFileSync(join(this.forgeDir, 'events.jsonl'), line + '\n', { flag: 'a' });
  }

  lastEvents(n = 5) {
    const p = join(this.forgeDir, 'events.jsonl');
    if (!existsSync(p)) return [];
    const lines = readFileSync(p, 'utf8').trimEnd().split('\n').filter(Boolean);
    return lines.slice(-n).map((l) => {
      try {
        return JSON.parse(l);
      } catch {
        return { raw: l };
      }
    });
  }

  acquireLock() {
    mkdirSync(this.forgeDir, { recursive: true });
    const token = `${process.pid}:${Date.now()}:${Math.random()}`;
    const recoveryPath = `${this.lockPath}.recovery`;
    const replacement = { pid: process.pid, ts: Date.now(), token };
    let guard = null;
    if (!existsSync(recoveryPath)) {
      try {
        writeFileSync(this.lockPath, JSON.stringify(replacement), { flag: 'wx' });
        this.lockToken = token;
        return;
      } catch (error) {
        if (error?.code !== 'EEXIST') throw error;
      }
    }

    const held = existsSync(this.lockPath)
      ? parseStateLock(this.lockPath, 'Forge state lock')
      : null;
    if (!existsSync(recoveryPath) && held && processIsAlive(held.pid)) {
      throw new CliError(1, 'another forge run holds .forge/lock');
    }

    const freshGuard = {
      version: 1,
      pid: process.pid,
      ts: Date.now(),
      token: `${token}:recovery`,
      phase: 'observed',
      observed: held ? { pid: held.pid, token: held.token } : null,
      replacement: { pid: process.pid, token },
      abandoned: [],
    };
    try {
      writeFileSync(recoveryPath, JSON.stringify(freshGuard), { flag: 'wx' });
      guard = freshGuard;
    } catch (error) {
      if (error?.code !== 'EEXIST') throw error;
      const staleGuard = parseStateLock(recoveryPath, 'Forge lock recovery guard');
      if (staleGuard.version !== 1 ||
          (staleGuard.phase !== 'observed' && staleGuard.phase !== 'reclaiming') ||
          !Object.hasOwn(staleGuard, 'observed') ||
          !staleGuard.replacement ||
          !Number.isInteger(staleGuard.replacement.pid) ||
          typeof staleGuard.replacement.token !== 'string' ||
          !Array.isArray(staleGuard.abandoned) ||
          staleGuard.abandoned.some((lock) =>
            !Number.isInteger(lock?.pid) || typeof lock?.token !== 'string')) {
        throw new CliError(1, `Forge lock recovery guard has an invalid schema: ${recoveryPath}`);
      }
      if (processIsAlive(staleGuard.pid)) {
        throw new CliError(1, 'another forge run won the stale-lock recovery race; retry the command');
      }
      const claimPath = `${recoveryPath}.stale-${sha256(token).slice(0, 16)}`;
      try {
        renameSync(recoveryPath, claimPath);
      } catch (claimError) {
        if (claimError?.code === 'ENOENT' || claimError?.code === 'EEXIST') {
          throw new CliError(1, 'another forge run reclaimed the crashed recovery guard; retry the command');
        }
        throw claimError;
      }
      let installedReplacementGuard = false;
      try {
        const claimed = parseStateLock(claimPath, 'claimed Forge lock recovery guard');
        if (claimed.pid !== staleGuard.pid || claimed.token !== staleGuard.token) {
          throw new CliError(1, 'Forge lock recovery guard changed before atomic reclamation');
        }
        const abandoned = [...staleGuard.abandoned, staleGuard.replacement].slice(-16);
        guard = {
          ...staleGuard,
          pid: process.pid,
          ts: Date.now(),
          token: freshGuard.token,
          replacement: freshGuard.replacement,
          abandoned,
        };
        try {
          writeFileSync(recoveryPath, JSON.stringify(guard), { flag: 'wx' });
          installedReplacementGuard = true;
        } catch (replacementError) {
          if (replacementError?.code === 'EEXIST') {
            throw new CliError(1, 'another forge run replaced the crashed recovery guard first');
          }
          throw replacementError;
        }
      } finally {
        try {
          if (!installedReplacementGuard && !existsSync(recoveryPath)) {
            renameSync(claimPath, recoveryPath);
          } else {
            unlinkSync(claimPath);
          }
        } catch {
          // A crash or competing inspection may retain the claimed evidence.
        }
      }
    }
    stateLockCrash('after-recovery-guard');

    try {
      const current = existsSync(this.lockPath)
        ? parseStateLock(this.lockPath, 'Forge state lock')
        : null;
      if (guard.phase === 'observed') {
        if (!sameLock(current, guard.observed)) {
          throw new CliError(1, 'Forge state lock token or PID changed during recovery');
        }
        if (current && processIsAlive(current.pid)) {
          throw new CliError(1, 'Forge state lock owner is still alive');
        }
        const ownedGuard = parseStateLock(recoveryPath, 'Forge lock recovery guard');
        if (ownedGuard.token !== guard.token || ownedGuard.pid !== process.pid) {
          throw new CliError(1, 'Forge lock recovery guard ownership changed before unlink');
        }
        guard.phase = 'reclaiming';
        const temp = `${recoveryPath}.${process.pid}.tmp`;
        writeFileSync(temp, JSON.stringify(guard), { flag: 'wx' });
        const revalidated = parseStateLock(recoveryPath, 'Forge lock recovery guard');
        if (revalidated.token !== guard.token || revalidated.pid !== process.pid) {
          unlinkSync(temp);
          throw new CliError(1, 'Forge lock recovery guard changed before phase transition');
        }
        renameSync(temp, recoveryPath);
      }

      const beforeUnlink = existsSync(this.lockPath)
        ? parseStateLock(this.lockPath, 'Forge state lock')
        : null;
      const knownLocks = [guard.observed, guard.replacement, ...guard.abandoned].filter(Boolean);
      const matchedLock = knownLocks.find((candidate) => sameLock(beforeUnlink, candidate));
      if (beforeUnlink && (!matchedLock || processIsAlive(matchedLock.pid))) {
        throw new CliError(1, 'Forge state lock token or PID changed before unlink');
      }
      if (beforeUnlink) {
        const ownedGuard = parseStateLock(recoveryPath, 'Forge lock recovery guard');
        const finalLock = parseStateLock(this.lockPath, 'Forge state lock');
        if (ownedGuard.token !== guard.token || ownedGuard.pid !== process.pid ||
            !knownLocks.some((candidate) => sameLock(finalLock, candidate))) {
          throw new CliError(1, 'Forge lock or recovery token changed before unlink');
        }
        unlinkSync(this.lockPath);
      }
      stateLockCrash('after-stale-lock-unlink');
      try {
        writeFileSync(this.lockPath, JSON.stringify(replacement), { flag: 'wx' });
      } catch (error) {
        if (error?.code === 'EEXIST') {
          throw new CliError(1, 'another forge run acquired the lock during stale-lock recovery');
        }
        throw error;
      }
      this.lockToken = token;
      stateLockCrash('after-recovery-lock-acquired');
    } finally {
      try {
        const ownedGuard = parseStateLock(recoveryPath, 'Forge lock recovery guard');
        if (this.lockToken === token && guard &&
            ownedGuard.token === guard.token && ownedGuard.pid === process.pid) {
          unlinkSync(recoveryPath);
        }
      } catch {
        // A crash or competing recovery may already have retained or removed it.
      }
    }
  }

  releaseLock() {
    try {
      const held = JSON.parse(readFileSync(this.lockPath, 'utf8'));
      if (this.lockToken && held.token === this.lockToken) unlinkSync(this.lockPath);
    } catch {
      /* already gone */
    }
  }

  planHash() {
    if (!existsSync(this.planPath)) throw new CliError(1, 'PLAN.md not found');
    return sha256(readFileSync(this.planPath, 'utf8'));
  }

  /** Content-sensitive: tracked diff bytes + per-untracked-file content hashes. */
  treeFingerprint() {
    const diff = tryGit(this.cwd, ['diff', 'HEAD']) ?? '';
    const untracked = (tryGit(this.cwd, ['ls-files', '--others', '--exclude-standard']) ?? '')
      .split('\n')
      .filter(Boolean)
      .sort();
    const parts = [sha256(diff)];
    for (const rel of untracked) {
      const p = join(this.cwd, rel);
      parts.push(`${rel}:${existsSync(p) ? sha256(readFileSync(p)) : 'missing'}`);
    }
    return sha256(parts.join('\n'));
  }

  repositoryMarkerPath() {
    const common = tryGit(this.cwd, ['rev-parse', '--git-common-dir']);
    if (!common) return null;
    const commonDir = realpathSync(isAbsolute(common) ? common : join(this.cwd, common));
    return join(commonDir, 'plan-forge-active.json');
  }
}

function freshState(maxRounds) {
  const now = new Date().toISOString();
  return {
    version: STATE_VERSION,
    phase: 'grill',
    amendmentReview: false,
    amendmentReviewConsumed: false,
    round: 0,
    maxRounds,
    fixRound: 0,
    maxFixRounds: 3,
    maxBuildRetries: 3,
    maxVerdictRetries: 2,
    deadlock: false,
    modelSelection: null,
    builderSelectionPlanHash: null,
    modelCatalog: null,
    modelResolutionTrace: [],
    builderAgentId: null,
    reviewerAgentIds: [],
    tasks: [],
    taskIndex: 0,
    taskCount: null,
    pendingDispatch: null,
    headBaseRef: null,
    headBaseSha: null,
    worktreeBaseRef: null,
    worktreeBaseSha: null,
    untrackedBaseline: [],
    implementationApproval: null,
    replayKeys: [],
    lastVerdict: null,
    verifyCommands: [],
    createdAt: now,
    updatedAt: now,
  };
}

function requireCurrentImplementationApproval(ws, state, action) {
  const approval = state.implementationApproval;
  if (!approval) {
    throw new CliError(
      3,
      `${action} requires an approved native implementation action`,
    );
  }
  if (approval.planHash !== ws.planHash()) {
    throw new CliError(
      3,
      `PLAN.md changed after implementation approval — present the revised plan for native approval before ${action}`,
    );
  }
}

export function validateBuilderSelectionBinding(state, humanPlanHash) {
  if (!state.modelSelection?.builder) {
    throw new Error('approved builder selection is missing');
  }
  if (state.builderSelectionPlanHash !== humanPlanHash) {
    throw new Error('builder selection does not match the current human plan');
  }
  return state.modelSelection.builder;
}

function requireCurrentBuilderSelection(ws, state, action) {
  try {
    validateBuilderSelectionBinding(state, ws.planHash());
  } catch (error) {
    throw new CliError(
      3,
      `${error.message}; select a builder again before ${action}`,
    );
  }
}

// ---------------------------------------------------------------------------
// Subcommands

function sessionMaxAgeMs() {
  const configured = Number(process.env.FORGE_SESSION_MAX_AGE_MS);
  return Number.isFinite(configured) && configured > 0 ? configured : DEFAULT_SESSION_MAX_AGE_MS;
}

function sessionContextPath(cwd) {
  return sessionContextPathFor(realpathSync(cwd));
}

function readSessionContext(cwd, flags, options = {}) {
  const requireFresh = options.requireFresh !== false;
  let captured;
  if (typeof flags.context === 'string') {
    captured = JSON.parse(readFileSync(resolve(flags.context), 'utf8'));
  } else {
    const path = sessionContextPath(cwd);
    if (!existsSync(path)) return { cwd: realpathSync(cwd) };
    captured = JSON.parse(readFileSync(path, 'utf8'));
  }
  const observed = Date.parse(captured.observedAt);
  const ageMs = Number.isFinite(observed) ? Date.now() - observed : null;
  const stale = ageMs === null || ageMs > sessionMaxAgeMs();
  if (stale && requireFresh) {
    throw new CliError(
      3,
      `session capture is missing a fresh timestamp or older than ${sessionMaxAgeMs()} ms; ` +
      'submit a new prompt, or raise FORGE_SESSION_MAX_AGE_MS if this session is intentionally long-running',
    );
  }
  return {
    cwd: realpathSync(captured.cwd ?? cwd),
    sessionId: captured.sessionId,
    activeModel: captured.model,
    activeModelSource: 'prompt-hook',
    activeEffort: captured.effortKnown ? captured.effort : null,
    activeEffortSource: captured.effortKnown ? 'prompt-hook' : null,
    observedAt: captured.observedAt,
    stale,
    ageMs,
  };
}

async function resolveCatalog(_cwd, flags) {
  const obsoleteFlags = ['native-models', 'manual-model', 'manual-models', 'active-model', 'active-effort'];
  const obsolete = obsoleteFlags.find((name) => Object.hasOwn(flags, name));
  if (obsolete) {
    throw new CliError(
      1,
      `--${obsolete} is no longer supported; model discovery uses codex debug models exclusively`,
    );
  }
  const resolver = new ModelCatalogResolver({
    debug: new CodexDebugModelsProvider(),
  });
  return resolver.getCatalog();
}

async function cmdModels(cwd, flags) {
  const catalog = await resolveCatalog(cwd, flags);
  out({ available: catalog.models.length > 0, catalog });
}

async function cmdPicker(cwd, flags, positional) {
  if (positional.length > 0) throw new CliError(1, 'picker accepts flags only');
  if (Object.keys(flags).some(
    (name) => !['cwd', 'role', 'cursor', 'model'].includes(name),
  )) {
    throw new CliError(1, 'picker accepts only --role, --cursor, --model, and --cwd');
  }
  const role = flags.role;
  if (role !== 'reviewer' && role !== 'builder') {
    throw new CliError(1, 'picker requires --role reviewer|builder');
  }
  const cursor = flags.cursor === undefined ? 0 : Number(flags.cursor);
  const catalog = await resolveCatalog(cwd, flags);
  const picker = typeof flags.model === 'string'
    ? effortPicker(catalog, { role, model: flags.model, cursor })
    : modelPicker(catalog, { role, cursor });
  out({ action: 'picker-metadata', role, picker });
}

function canonicalRepositoryIdentity(cwd) {
  const workspaceRoot = realpathSync(git(cwd, ['rev-parse', '--show-toplevel']));
  const common = git(workspaceRoot, ['rev-parse', '--git-common-dir']);
  return {
    workspaceRoot,
    gitCommonDir: realpathSync(isAbsolute(common) ? common : join(workspaceRoot, common)),
  };
}

function issuanceCapture(cwd) {
  const repository = canonicalRepositoryIdentity(cwd);
  const path = sessionContextPathFor(repository.workspaceRoot);
  if (!existsSync(path)) throw new CliError(3, 'fresh Plan-mode UserPromptSubmit capture is missing');
  let capture;
  try {
    capture = JSON.parse(readFileSync(path, 'utf8'));
  } catch {
    throw new CliError(3, 'Plan-mode UserPromptSubmit capture is malformed');
  }
  const age = Date.now() - Date.parse(capture.observedAt);
  if (capture.version !== 2 || capture.cwd !== repository.workspaceRoot ||
      typeof capture.sessionId !== 'string' || typeof capture.turnId !== 'string' ||
      typeof capture.transcriptPath !== 'string' ||
      !Number.isFinite(age) || age < 0 || age > sessionMaxAgeMs()) {
    throw new CliError(3, 'Plan-mode UserPromptSubmit capture is stale or schema-incompatible');
  }
  const transcript = readTranscript(capture.transcriptPath);
  if (transcript.sessionId !== capture.sessionId) throw new CliError(3, 'Plan-mode capture session mismatch');
  const mode = collaborationModeForTurn(transcript, capture.turnId);
  if (mode.mode !== 'plan') throw new CliError(3, `approval issuance requires Plan mode (observed ${mode.mode})`);
  return {
    repository,
    transcript,
    origin: {
      sessionId: capture.sessionId,
      transcriptPath: transcript.path,
      turnId: capture.turnId,
    },
  };
}

function highestEligibleTranscriptRevision(repository, transcript) {
  let highest = 0;
  for (const record of transcript.records) {
    if (record.kind !== 'message' || record.role !== 'assistant' ||
        record.phase !== 'final_answer' || record.mode !== 'plan') {
      continue;
    }
    const match = /^<proposed_plan>\n([\s\S]*)<\/proposed_plan>\s*$/.exec(record.text);
    if (!match) continue;
    let wrapper;
    try {
      wrapper = extractApprovalWrapper(match[1]);
    } catch {
      continue;
    }
    const envelope = wrapper.envelope;
    try {
      if (repositoryIdentityEquals(envelope.repository, repository) &&
          envelope.origin.sessionId === transcript.sessionId &&
          realpathSync(envelope.origin.transcriptPath) === transcript.path &&
          envelope.origin.turnId === record.turnId) {
        highest = Math.max(highest, envelope.planRevision);
      }
    } catch {
      // Only wrappers with a still-resolvable matching origin are eligible.
    }
  }
  return highest;
}

function highestRepositoryRevision(repository, transcript) {
  const repositoryKey = sha256(
    `${process.platform === 'win32' ? repository.workspaceRoot.toLowerCase() : repository.workspaceRoot}\n` +
    `${process.platform === 'win32' ? repository.gitCommonDir.toLowerCase() : repository.gitCommonDir}`,
  );
  let highest = 0;
  const dir = nonceTombstoneDir();
  if (existsSync(dir)) {
    for (const entry of readdirSync(dir)) {
      if (!entry.endsWith('.json')) continue;
      const tombstone = JSON.parse(readFileSync(join(dir, entry), 'utf8'));
      if (tombstone.repositoryKey === repositoryKey) {
        highest = Math.max(highest, Number(tombstone.planRevision) || 0);
      }
    }
  }
  const statePath = join(repository.workspaceRoot, '.forge', 'state.json');
  if (existsSync(statePath)) {
    const state = JSON.parse(readFileSync(statePath, 'utf8'));
    highest = Math.max(highest, Number(state.materialization?.planRevision) || 0);
  }
  highest = Math.max(highest, highestEligibleTranscriptRevision(repository, transcript));
  return highest;
}

async function cmdIssueApproval(cwd, flags, positional) {
  if (positional.length > 0 || Object.keys(flags).some((name) => name !== 'cwd')) {
    throw new CliError(1, 'issue-approval accepts bounded JSON on stdin and optional --cwd');
  }
  const input = readFileSync(0);
  if (input.length <= 0 || input.length > 2 * 1024 * 1024) {
    throw new CliError(1, 'approval stdin size is invalid');
  }
  const request = JSON.parse(input.toString('utf8'));
  const keys = Object.keys(request).sort();
  const expectedKeys = [
    'builder',
    'completedReviewRounds',
    'humanPlan',
    'maxRounds',
    'reviewLog',
    'reviewer',
  ];
  if (JSON.stringify(keys) !== JSON.stringify(expectedKeys)) {
    throw new CliError(1, `approval input keys must be exactly: ${expectedKeys.join(', ')}`);
  }
  const { repository, transcript, origin } = issuanceCapture(cwd);
  const catalog = await resolveCatalog(cwd, {});
  let state = createPlanUxState(
    request.humanPlan,
    highestRepositoryRevision(repository, transcript) + 1,
  );
  state = recordFullPlanPreview(state, state.humanPlan);
  state = recordBuilderSelection(state, request.builder, catalog);
  const issued = issueApprovalEnvelope(state, {
    reviewer: request.reviewer,
    catalog,
    reviewLog: request.reviewLog,
    completedReviewRounds: request.completedReviewRounds,
    maxRounds: request.maxRounds,
    repository,
    origin,
  });
  out({
    action: 'approval-issued',
    planRevision: issued.envelope.planRevision,
    humanPlanHash: issued.envelope.humanPlanHash,
    envelope: issued.envelope,
    wrapper: issued.wrapper,
    proposedPlanOutput: finalProposedPlanOutput(issued.state),
  });
}

function shippedAgentPath(role) {
  return fileURLToPath(new URL(`../agents/forge_${role}.toml`, import.meta.url));
}

/**
 * Idempotently install the two static subagents into the global agents dir so
 * Codex registers them at task start. No model is baked in — the orchestrator
 * passes the model when it spawns the subagent. Hand-authored files (those
 * lacking the generated marker) are never overwritten.
 */
function cmdInstallAgents() {
  const dir = globalAgentsDir();
  mkdirSync(dir, { recursive: true });
  const results = {};
  const foreign = [];
  let reloadRequired = false;
  for (const role of AGENT_ROLES) {
    const src = shippedAgentPath(role);
    if (!existsSync(src)) throw new CliError(1, `shipped agent not found: ${src}`);
    const shipped = readFileSync(src, 'utf8');
    const target = join(dir, `forge_${role}.toml`);
    if (!existsSync(target)) {
      writeFileSync(target, shipped);
      results[role] = 'installed';
      reloadRequired = true;
      continue;
    }
    const current = readFileSync(target, 'utf8');
    if (!current.includes(GENERATED_MARKER)) {
      results[role] = 'foreign';
      foreign.push(role);
      continue;
    }
    if (current === shipped) {
      results[role] = 'unchanged';
    } else {
      writeFileSync(target, shipped);
      results[role] = 'updated';
      reloadRequired = true;
    }
  }
  if (foreign.length) {
    note(
      `warning: hand-authored ${foreign
        .map((r) => `forge_${r}.toml`)
        .join(', ')} already in ${dir} — not overwritten; move them aside to use the managed version`,
    );
  }
  if (reloadRequired) {
    note('a subagent file was created or updated — start a new Codex task so the native tool schema registers it, then rerun $forge');
  }
  out({ action: 'agents-installed', dir, results, reloadRequired, foreign });
}

export function parseFeatureEnabled(features, name = 'multi_agent') {
  if (typeof features !== 'string') return null;
  const line = features.split(/\r?\n/).find((candidate) =>
    candidate.trim().split(/\s+/)[0] === name);
  if (!line) return null;
  const columns = line.trim().split(/\s+/);
  const enabled = columns.at(-1)?.toLowerCase();
  if (enabled === 'true' || enabled === 'enabled') return true;
  if (enabled === 'false' || enabled === 'disabled') return false;
  return null;
}

function sessionCaptureCheck(ws) {
  const path = sessionContextPath(ws.cwd);
  if (!existsSync(path)) {
    // Distinguish "the hook never ran anywhere" from "it ran, but not for this
    // workspace"; the two need different fixes and the bare path does not say
    // which one happened.
    const everCaptured = existsSync(dirname(path));
    return {
      name: 'session_capture',
      ok: false,
      path,
      reason: 'missing',
      everCaptured,
      hint: everCaptured
        ? 'The prompt hook works but has not captured this workspace yet. Ask the user to submit a prompt from this repository.'
        : 'No capture has ever been written, so the UserPromptSubmit hook is not running. Ask the user to confirm the plugin is enabled and its hook is trusted, then submit a new prompt.',
    };
  }
  try {
    const capture = JSON.parse(readFileSync(path, 'utf8'));
    const ageMs = Date.now() - Date.parse(capture.observedAt);
    if (!Number.isFinite(ageMs)) {
      return { name: 'session_capture', ok: false, path, reason: 'invalid-timestamp' };
    }
    const fresh = ageMs <= sessionMaxAgeMs();
    return {
      name: 'session_capture',
      ok: true,
      fresh,
      path,
      sessionId: capture.sessionId ?? null,
      ageMs,
      maxAgeMs: sessionMaxAgeMs(),
      warning: fresh ? null : 'capture is stale',
    };
  } catch (error) {
    return { name: 'session_capture', ok: false, path, reason: `unreadable: ${error.message}` };
  }
}

function spawnMetadataCheck() {
  const codexHome = process.env.CODEX_HOME?.trim();
  const path = join(codexHome ? resolve(codexHome) : join(homedir(), '.codex'), 'config.toml');
  if (!existsSync(path)) {
    return { name: 'spawn_metadata', ok: true, hidden: false, path };
  }
  try {
    const text = readFileSync(path, 'utf8');
    let section = '';
    let hidden = null;
    for (const line of text.split(/\r?\n/)) {
      const sectionMatch = /^\s*\[([^\]]+)\]\s*(?:#.*)?$/.exec(line);
      if (sectionMatch) {
        section = sectionMatch[1].trim();
        continue;
      }
      const valueMatch = /^\s*([A-Za-z0-9_.-]+)\s*=\s*(true|false)\s*(?:#.*)?$/i.exec(line);
      if (!valueMatch) continue;
      const key = valueMatch[1].includes('.') || !section
        ? valueMatch[1]
        : `${section}.${valueMatch[1]}`;
      if (key === 'multi_agent_v2.hide_spawn_agent_metadata') {
        hidden = valueMatch[2].toLowerCase() === 'true';
      }
    }
    return {
      name: 'spawn_metadata',
      ok: true,
      hidden: hidden === true,
      path,
      warning: hidden === true
        ? 'spawn metadata is hidden; forge cannot pin reviewer/builder models'
        : null,
    };
  } catch (error) {
    return {
      name: 'spawn_metadata',
      ok: true,
      hidden: null,
      path,
      warning: `could not inspect Codex config for hidden spawn metadata: ${error.message}`,
    };
  }
}

function cmdDoctor(ws) {
  const checks = [];
  checks.push({ name: 'node', ok: Number(process.versions.node.split('.')[0]) >= 18, version: process.versions.node });
  const gitVersion = tryGit(ws.cwd, ['--version']);
  checks.push({ name: 'git', ok: Boolean(gitVersion), version: gitVersion });
  let codexVersion = null;
  let features = null;
  try {
    codexVersion = runCodexCli(['--version']).trim();
  } catch {
    // Report below.
  }
  try {
    features = runCodexCli(['features', 'list']);
  } catch {
    // Older builds may not expose the feature command.
  }
  const versionMatch = /(\d+)\.(\d+)\.(\d+)/.exec(codexVersion ?? '');
  const codexOk = codexVersion === null
    ? false
    : !versionMatch || Number(versionMatch[1]) > 0 || Number(versionMatch[2]) >= 145;
  checks.push({
    name: 'codex',
    ok: codexOk,
    version: codexVersion,
    warning: codexVersion !== null && !versionMatch
      ? 'could not parse the Codex version; the >= 0.145 requirement is unverified'
      : null,
  });
  const multiAgent = parseFeatureEnabled(features);
  checks.push({
    name: 'multi_agent',
    ok: multiAgent !== false,
    state: multiAgent === null ? 'unknown' : String(multiAgent),
    warning: multiAgent === null
      ? 'codex features list did not report multi_agent; forge cannot confirm subagent spawning is enabled'
      : null,
    observed: features ? features.split('\n').find((line) => /\bmulti_agent\b/.test(line))?.trim() ?? null : null,
  });
  checks.push(spawnMetadataCheck());
  checks.push(sessionCaptureCheck(ws));
  const agentsDir = globalAgentsDir();
  for (const role of AGENT_ROLES) {
    const path = join(agentsDir, `forge_${role}.toml`);
    let readable = false;
    try {
      readFileSync(path, 'utf8');
      readable = true;
    } catch {
      // Report below.
    }
    checks.push({ name: `${role}_agent`, ok: readable, path });
  }
  const ok = checks.every((check) => check.ok);
  const warnings = checks
    .filter((check) => check.warning)
    .map((check) => ({ name: check.name, warning: check.warning }));
  out({ action: 'doctor', ok, warnings, checks });
  if (!ok) process.exitCode = 1;
}

async function selectionCatalog(cwd, flags) {
  if (typeof flags.catalog === 'string') {
    throw new CliError(1, '--catalog is no longer supported; selections require a fresh codex debug models catalog');
  }
  return resolveCatalog(cwd, flags);
}

function rejectSelectionOverrides(flags) {
  if (flags['user-override'] || flags['user-note']) {
    throw new CliError(1, 'model selection overrides are no longer supported');
  }
  if (flags['orchestrator-model'] || flags['orchestrator-effort']) {
    throw new CliError(1, 'orchestrator model and effort are not part of reviewer or builder selection');
  }
}

function selectedPair(flags, role) {
  const pair = {};
  for (const name of ['model', 'effort']) {
    const flag = `${role}-${name}`;
    if (typeof flags[flag] !== 'string' || !flags[flag].trim()) {
      throw new CliError(1, `configure-${role} requires --${flag} <value>`);
    }
    pair[name] = flags[flag].trim();
  }
  if (!validateModelId(pair.model)) throw new CliError(1, `${role} model fails the slug grammar`);
  pair.effort = pair.effort.toLowerCase();
  return pair;
}

async function cmdConfigureReviewer(cwd, flags) {
  if (flags['builder-model'] || flags['builder-effort'] || flags['plan-hash']) {
    throw new CliError(1, 'configure-reviewer accepts only a reviewer model and effort');
  }
  rejectSelectionOverrides(flags);
  const reviewer = selectedPair(flags, 'reviewer');
  const catalog = await selectionCatalog(cwd, flags);
  let validation;
  try {
    validation = validateModelSelection({ reviewer }, catalog);
  } catch (error) {
    throw new CliError(3, error.message);
  }
  out({
    action: 'reviewer-selected',
    reviewer,
    catalogObservation: {
      generatedAt: catalog.generatedAt,
      source: 'codex-debug-models',
    },
    validation,
  });
}

async function cmdConfigureBuilder(cwd, flags) {
  if (flags['reviewer-model'] || flags['reviewer-effort']) {
    throw new CliError(1, 'configure-builder accepts only a builder model, effort, and human-plan hash');
  }
  rejectSelectionOverrides(flags);
  const builder = selectedPair(flags, 'builder');
  if (typeof flags['plan-hash'] !== 'string' || !/^[a-f0-9]{64}$/i.test(flags['plan-hash'])) {
    throw new CliError(1, 'configure-builder requires --plan-hash <sha256 of the immutable human plan>');
  }
  const humanPlanHash = flags['plan-hash'].toLowerCase();
  const catalog = await selectionCatalog(cwd, flags);
  let validation;
  try {
    validation = validateModelSelection({ builder }, catalog);
  } catch (error) {
    throw new CliError(3, error.message);
  }
  out({
    action: 'builder-selected',
    builder,
    humanPlanHash,
    catalogObservation: {
      generatedAt: catalog.generatedAt,
      source: 'codex-debug-models',
    },
    validation,
  });
}

/*
 * Reviewer and builder choices are intentionally not persisted by the
 * read-only commands above. Task 3's approval validator calls this factory
 * only after it has authenticated the native implementation action and
 * approved envelope.
 */
export function stateFromApprovedEnvelope(envelope, catalog, implementationApproval) {
  if (!envelope || typeof envelope !== 'object' || Array.isArray(envelope)) {
    throw new Error('approved envelope is required');
  }
  const humanPlanHash = envelope.humanPlanHash;
  if (typeof humanPlanHash !== 'string' || !/^[a-f0-9]{64}$/i.test(humanPlanHash)) {
    throw new Error('approved envelope has an invalid humanPlanHash');
  }
  const reviewer = envelope.reviewer;
  const builder = envelope.builder;
  const validation = validateModelSelection({ reviewer, builder }, catalog);
  if (envelope.builderPlanHash !== humanPlanHash) {
    throw new Error('builder selection is not bound to the approved human plan hash');
  }
  if (!implementationApproval || implementationApproval.planHash !== humanPlanHash) {
    throw new Error('implementation approval does not match the approved human plan hash');
  }
  const reviewRounds = envelope.completedReviewRounds;
  if (!Number.isInteger(reviewRounds) || reviewRounds < 1) {
    throw new Error('approved envelope has an invalid completedReviewRounds');
  }
  const state = freshState(
    Number.isInteger(envelope.maxRounds) && envelope.maxRounds >= reviewRounds
      ? envelope.maxRounds
      : Math.max(DEFAULT_MAX_PLAN_ROUNDS, reviewRounds),
  );
  state.phase = 'signoff';
  state.round = reviewRounds;
  state.modelSelection = { reviewer: validation.reviewer, builder: validation.builder };
  state.builderSelectionPlanHash = humanPlanHash.toLowerCase();
  state.modelCatalog = structuredClone(catalog);
  state.modelResolutionTrace = structuredClone(catalog.trace ?? []);
  state.implementationApproval = {
    ...structuredClone(implementationApproval),
    planHash: humanPlanHash.toLowerCase(),
  };
  return state;
}

export function stateFromApprovedAmendment(
  existing,
  envelope,
  catalog,
  implementationApproval,
  humanPlan,
) {
  if (!existing?.amendmentReview || (existing.phase !== 'build' && existing.phase !== 'review')) {
    throw new Error('active Forge state is not awaiting an amendment approval');
  }
  const verdict = existing.lastVerdict;
  if (!existing.amendmentReviewConsumed || verdict?.stage !== 'plan' ||
      verdict.action !== 'approved' || verdict.verdict !== 'APPROVED' ||
      verdict.planHash !== envelope.humanPlanHash ||
      !Number.isInteger(verdict.round) || verdict.round !== existing.round ||
      typeof verdict.critiqueHash !== 'string' ||
      !existing.replayKeys?.includes(`plan:${verdict.planHash}:${verdict.critiqueHash}`) ||
      verdict.reviewLogHash !== sha256(envelope.reviewLog) ||
      envelope.completedReviewRounds !== existing.round) {
    throw new Error('amendment requires a consumed APPROVED review bound to the amended plan and durable log');
  }
  const validation = validateModelSelection({
    reviewer: envelope.reviewer,
    builder: envelope.builder,
  }, catalog);
  if (JSON.stringify(validation.reviewer) !== JSON.stringify(existing.modelSelection?.reviewer)) {
    throw new Error('amendment must retain the pinned reviewer selection');
  }
  if (envelope.builderPlanHash !== envelope.humanPlanHash ||
      implementationApproval?.planHash !== envelope.humanPlanHash) {
    throw new Error('amendment approval is not bound to the revised human plan');
  }
  const tasks = parseApproachTasks(humanPlan);
  for (const old of existing.tasks ?? []) {
    if (old.number > existing.taskIndex) continue;
    const replacement = tasks.find((task) => task.number === old.number);
    if (!replacement || replacement.hash !== old.hash) {
      throw new Error(`amendment would change completed task ${old.number} ("${old.title}")`);
    }
  }
  const state = structuredClone(existing);
  const builderChanged =
    validation.builder.model !== existing.modelSelection?.builder?.model ||
    validation.builder.effort !== existing.modelSelection?.builder?.effort;
  state.phase = 'build';
  state.amendmentReview = false;
  state.amendmentReviewConsumed = false;
  state.pendingDispatch = null;
  state.tasks = tasks;
  state.taskCount = tasks.length;
  state.modelSelection = {
    reviewer: validation.reviewer,
    builder: validation.builder,
  };
  state.builderSelectionPlanHash = envelope.humanPlanHash;
  state.implementationApproval = {
    ...structuredClone(implementationApproval),
    planHash: envelope.humanPlanHash,
  };
  if (builderChanged) {
    state.builderAgentId = null;
    state.builderSessionObservation = null;
  }
  return state;
}

function cmdLockPlan(ws, flags) {
  const state = ws.loadState();
  const relock = Boolean(flags.relock);
  if (!relock && state.phase !== 'signoff') {
    throw new CliError(3, `lock-plan runs at the sign-off transition (phase is "${state.phase}")`);
  }
  if (!relock) requireCurrentImplementationApproval(ws, state, 'locking the plan');
  if (relock && !state.amendmentReview) {
    throw new CliError(3, 'lock-plan --relock is only legal during an amendment (phase build/review with --amendment)');
  }
  const planText = readFileSync(ws.planPath, 'utf8');
  let tasks;
  try {
    tasks = parseApproachTasks(planText);
  } catch (e) {
    throw new CliError(1, e.message);
  }
  if (relock) {
    for (const old of state.tasks) {
      if (old.number > state.taskIndex) continue;
      const now = tasks.find((t) => t.number === old.number);
      if (!now || now.hash !== old.hash) {
        throw new CliError(3, `relock would change completed task ${old.number} ("${old.title}") — completed work is immutable`);
      }
    }
  }
  state.tasks = tasks;
  state.taskCount = tasks.length;
  ws.saveState(state);
  ws.appendEvent('lock-plan', { relock, taskCount: tasks.length });
  out({ action: relock ? 'plan-relocked' : 'plan-locked', taskCount: tasks.length, tasks });
}

const DISPATCH_STAGES = {
  plan: { phase: 'review' },
  code: { phase: 'code-review' },
  build: { phase: 'build' },
  fix: { phase: 'code-review' },
};

function retryCapForStage(state, stage) {
  return stage === 'plan' || stage === 'code'
    ? state.maxVerdictRetries ?? 2
    : state.maxBuildRetries ?? 3;
}

async function cmdDispatch(ws, flags) {
  const state = ws.loadState();
  const stage = flags.stage;
  const spec = DISPATCH_STAGES[stage];
  if (!spec) throw new CliError(1, 'dispatch requires --stage plan|code|build|fix');
  if ((stage === 'plan' || stage === 'code') && !state.modelSelection?.reviewer) {
    throw new CliError(3, 'reviewer is not pinned for this run — run configure-reviewer first');
  }
  if ((stage === 'build' || stage === 'fix') && !state.modelSelection?.builder) {
    throw new CliError(3, 'builder is not pinned for this run — run configure-builder first');
  }
  if (state.phase !== spec.phase) {
    throw new CliError(3, `dispatch --stage ${stage} requires phase "${spec.phase}" (current: "${state.phase}")`);
  }
  if (flags.cancel) {
    const pending = state.pendingDispatch;
    if (!pending || pending.stage !== stage) {
      throw new CliError(3, `--cancel needs a pending ${stage} dispatch`);
    }
    state.pendingDispatch = null;
    ws.saveState(state);
    ws.appendEvent('dispatch', { stage, cancelled: true });
    out({ action: 'dispatch-cancelled', stage });
    return;
  }
  if (stage === 'build') requireCurrentImplementationApproval(ws, state, 'build dispatch');
  if (stage === 'build' || stage === 'fix') {
    requireCurrentBuilderSelection(ws, state, `${stage} dispatch`);
  }
  if (stage === 'plan' && state.amendmentReview && state.amendmentReviewConsumed) {
    throw new CliError(3, 'a material amendment gets exactly one fresh review round; return the decision to the user');
  }

  if (flags.retry) {
    const pending = state.pendingDispatch;
    if (!pending || pending.stage !== stage) {
      throw new CliError(3, `--retry needs a pending ${stage} dispatch`);
    }
    const retryCap = retryCapForStage(state, stage);
    if (pending.retries >= retryCap) {
      throw new CliError(
        3,
        `retry cap (${retryCap}) reached for this ${stage} dispatch — ask the user to authorize one additional round, accept risk and advance, or stop`,
      );
    }
    pending.retries += 1;
    pending.dispatchId = sha256(
      `${stage}:retry:${pending.retries}:${new Date().toISOString()}:${process.pid}`,
    ).slice(0, 16);
    delete pending.reviewerAgentId;
    delete pending.lastInvalidVerdict;
    if (stage === 'plan' || stage === 'code') {
      pending.critiqueFile = critiqueFileName(state, stage, pending.retries);
    }
    ws.saveState(state);
    ws.appendEvent('dispatch', { stage, retry: pending.retries });
    out({
      action: 'dispatch-retry',
      stage,
      dispatchId: pending.dispatchId,
      retries: pending.retries,
      retryCap,
      critiqueFile: pending.critiqueFile ?? null,
      agent: stage === 'plan' || stage === 'code'
        ? { role: 'forge_reviewer', ...state.modelSelection.reviewer, fresh: true }
        : { role: 'forge_builder', ...state.modelSelection.builder, persistentId: state.builderAgentId ?? null },
    });
    return;
  }

  if (state.pendingDispatch) {
    throw new CliError(3, `a ${state.pendingDispatch.stage} dispatch is already pending — consume it (verdict/complete/resolve-build) or --retry`);
  }

  const pending = {
    stage,
    planHash: ws.planHash(),
    retries: 0,
    createdAt: new Date().toISOString(),
  };
  pending.dispatchId = sha256(`${stage}:${pending.createdAt}:${process.pid}`).slice(0, 16);
  if (stage === 'plan') pending.round = state.round + 1;
  if (stage === 'code') pending.round = state.fixRound + 1;
  if (stage === 'plan' || stage === 'code') {
    pending.critiqueFile = critiqueFileName(state, stage, 0);
  }
  if (stage === 'build') {
    const task = flags.task ? parsePositiveInt(flags.task, 'task') : null;
    if (!task) throw new CliError(1, 'dispatch --stage build requires --task K');
    if (task !== state.taskIndex + 1) {
      throw new CliError(3, `next task is ${state.taskIndex + 1}, not ${task}`);
    }
    if (!state.taskCount || task > state.taskCount) {
      throw new CliError(3, `task ${task} is out of range (taskCount=${state.taskCount})`);
    }
    pending.task = task;
  }
  if (stage === 'build' || stage === 'fix') {
    pending.fingerprint = ws.treeFingerprint();
    if (state.builderSessionObservation) {
      pending.observedModel = state.builderSessionObservation.model;
      pending.observedEffort = state.builderSessionObservation.effort;
    }
  }
  state.pendingDispatch = pending;
  ws.saveState(state);
  ws.appendEvent('dispatch', { stage, task: pending.task ?? null, round: pending.round ?? null });
  out({
    action: 'dispatched',
    stage,
    dispatchId: pending.dispatchId,
    round: pending.round ?? null,
    task: pending.task ?? null,
    critiqueFile: pending.critiqueFile ?? null,
    agent: stage === 'plan' || stage === 'code'
      ? { role: 'forge_reviewer', ...state.modelSelection.reviewer, fresh: true }
      : { role: 'forge_builder', ...state.modelSelection.builder, persistentId: state.builderAgentId ?? null },
  });
}

function critiqueFileName(state, stage, retry) {
  const suffix = retry > 0 ? `-r${retry}` : '';
  return stage === 'plan'
    ? `.forge/critiques/round-${state.round + 1}${suffix}.md`
    : `.forge/critiques/code-review-${state.fixRound + 1}${suffix}.md`;
}

function cmdComplete(ws, flags) {
  const state = ws.loadState();
  const pending = state.pendingDispatch;
  const userOverride = Boolean(flags['user-override']);
  const userNote = typeof flags['user-note'] === 'string' ? flags['user-note'].trim() : '';
  if (userOverride) {
    if (!userNote) {
      throw new CliError(1, 'accepting verification risk requires --user-note "<the user\'s exact words>"');
    }
    const cap = state.maxBuildRetries ?? 3;
    if (!pending || (pending.stage !== 'build' && pending.stage !== 'fix') || pending.retries < cap) {
      throw new CliError(3, `verification risk may be accepted only after reaching the retry cap (${cap})`);
    }
  }
  if (flags.fix) {
    if (!pending || pending.stage !== 'fix') throw new CliError(3, 'complete --fix needs a pending fix dispatch');
    state.pendingDispatch = null;
    ws.saveState(state);
    ws.appendEvent('complete', {
      fix: true,
      verificationReportedPassed: !userOverride,
      acceptedRisk: userOverride,
      userNote: userOverride ? userNote : null,
    });
    out({ action: 'fix-completed', fixRound: state.fixRound, acceptedRisk: userOverride });
    return;
  }
  const task = flags.task ? parsePositiveInt(flags.task, 'task') : null;
  if (!task) throw new CliError(1, 'complete requires --task K or --fix');
  if (!pending || pending.stage !== 'build' || pending.task !== task) {
    throw new CliError(3, `complete --task ${task} needs a pending build dispatch for task ${task}`);
  }
  if (ws.planHash() !== pending.planHash) {
    throw new CliError(3, `PLAN.md changed since task ${task} was dispatched; cancel or resolve the stale build before completing it`);
  }
  state.pendingDispatch = null;
  state.taskIndex = task;
  ws.saveState(state);
  ws.appendEvent('complete', {
    task,
    verificationReportedPassed: !userOverride,
    acceptedRisk: userOverride,
    userNote: userOverride ? userNote : null,
  });
  out({
    action: 'task-completed',
    taskIndex: state.taskIndex,
    taskCount: state.taskCount,
    nextTask: state.taskIndex < state.taskCount ? state.taskIndex + 1 : null,
    acceptedRisk: userOverride,
  });
}

function cmdResolveBuild(ws, flags) {
  if (!flags.conflict) throw new CliError(1, 'resolve-build requires --conflict');
  const state = ws.loadState();
  const pending = state.pendingDispatch;
  if (!pending || pending.stage !== 'build') {
    throw new CliError(3, 'resolve-build --conflict needs a pending build dispatch');
  }
  const task = pending.task;
  state.pendingDispatch = null;
  ws.saveState(state);
  ws.appendEvent('resolve-build', { task, conflict: true });
  out({ action: 'build-conflict-resolved', task, taskIndex: state.taskIndex });
}

function cmdVerdict(ws, flags) {
  const state = ws.loadState();
  const stage = flags.stage;
  if (stage !== 'plan' && stage !== 'code') throw new CliError(1, 'verdict requires --stage plan|code');
  const requiredPhase = stage === 'plan' ? 'review' : 'code-review';
  if (state.phase !== requiredPhase) {
    throw new CliError(3, `verdict --stage ${stage} requires phase "${requiredPhase}" (current: "${state.phase}")`);
  }
  const pending = state.pendingDispatch;
  if (!pending || pending.stage !== stage) {
    throw new CliError(3, `no pending ${stage} dispatch — run dispatch --stage ${stage} first`);
  }
  if (!pending.reviewerAgentId) {
    throw new CliError(3, 'register the freshly spawned reviewer with reviewer-session before consuming its verdict');
  }
  if (ws.planHash() !== pending.planHash) {
    throw new CliError(3, `PLAN.md changed since this reviewer was dispatched — the critique is stale; run dispatch --stage ${stage} --cancel, then dispatch fresh`);
  }
  if (typeof flags.file !== 'string') throw new CliError(1, 'verdict requires --file <critique>');
  const requested = resolve(ws.cwd, flags.file);
  if (!existsSync(requested)) throw new CliError(1, `critique file not found: ${flags.file}`);
  const critiquePath = realpathSync(requested);
  const critiquesReal = realpathSync(ws.critiquesDir);
  if (!critiquePath.startsWith(critiquesReal + sep)) {
    throw new CliError(1, 'critique file must live inside .forge/critiques/');
  }
  const content = readFileSync(critiquePath, 'utf8');
  if (!content.trim()) throw new CliError(1, 'critique file is empty');

  const verdict = parseVerdict(content);
  if (!verdict) {
    const lines = content.replace(/\r\n/g, '\n').split('\n').filter((l) => l.trim() !== '');
    pending.lastInvalidVerdict = true;
    ws.saveState(state);
    out({
      action: 'invalid-verdict',
      lastLine: lines[lines.length - 1] ?? '',
      hint: 'retry up to the current cap, then ask the user to authorize one more attempt, accept risk and advance, or stop',
    });
    process.exitCode = 2;
    return;
  }
  let coverage = null;
  if (stage === 'code') {
    coverage = parseCoverage(content);
    if (coverage === null || (verdict === 'APPROVED' && coverage !== 'FULL')) {
      pending.lastInvalidVerdict = true;
      ws.saveState(state);
      out({
        action: 'invalid-verdict',
        reason:
          coverage === null
            ? 'code review must contain exactly one COVERAGE: FULL|PARTIAL line before the verdict line'
            : 'APPROVED with COVERAGE: PARTIAL — approval on partial evidence fails closed',
        hint: 'retry up to the current cap, then ask the user to authorize one more attempt, accept risk and advance, or stop',
      });
      process.exitCode = 2;
      return;
    }
  }

  const replayKey = `${stage}:${pending.planHash}:${sha256(content)}`;
  if (state.replayKeys.includes(replayKey)) {
    throw new CliError(3, 'this exact critique was already processed for this plan revision (replay rejected)');
  }

  const round = pending.round;
  const heading = stage === 'plan' ? `## Round ${round} — Reviewer` : `## Code review ${round} — Reviewer`;
  if (!existsSync(ws.logPath) || !readFileSync(ws.logPath, 'utf8').includes(OWNED_MARKER)) {
    throw new CliError(3, 'PLAN-REVIEW-LOG.md is missing the plan-forge ownership marker; refusing to append');
  }
  writeFileSync(ws.logPath, `\n${heading}\n\n${content.trimEnd()}\n`, { flag: 'a' });

  state.replayKeys.push(replayKey);
  state.replayKeys = state.replayKeys.slice(-MAX_REPLAY_KEYS);
  state.pendingDispatch = null;
  if (stage === 'plan') state.round = round;
  else state.fixRound = round;
  const cap = stage === 'plan' ? state.maxRounds : state.maxFixRounds;
  const count = stage === 'plan' ? state.round : state.fixRound;
  let action;
  if (stage === 'plan' && state.amendmentReview) {
    state.amendmentReviewConsumed = true;
    action = verdict === 'APPROVED' ? 'approved' : 'user-decision';
  } else if (verdict === 'APPROVED') action = 'approved';
  else if (count < cap) action = 'revise';
  else {
    action = 'deadlock';
    state.deadlock = true;
  }
  state.lastVerdict = {
    stage,
    action,
    verdict,
    round,
    coverage,
    planHash: pending.planHash,
    critiqueHash: sha256(content),
    reviewLogHash: sha256(readFileSync(ws.logPath)),
  };
  ws.saveState(state);
  ws.appendEvent('verdict', { stage, round, verdict, action, coverage });
  out({ action, stage, round, remaining: Math.max(0, cap - count) });
}

function cmdBeginBuild(ws) {
  const state = ws.loadState();
  if (state.phase !== 'signoff') throw new CliError(3, `begin-build requires phase "signoff" (current: "${state.phase}")`);
  requireCurrentImplementationApproval(ws, state, 'begin-build');
  requireCurrentBuilderSelection(ws, state, 'begin-build');
  if (!state.taskCount) throw new CliError(3, 'run lock-plan before begin-build');
  if (!state.headBaseRef && [HEAD_BASE_REF_NAME, WORKTREE_BASE_REF_NAME]
    .some((ref) => Boolean(tryGit(ws.cwd, ['rev-parse', '--verify', ref])))) {
    throw new CliError(3, 'another forge run in this Git repository already owns the pinned baseline refs');
  }

  const headBaseSha = git(ws.cwd, ['rev-parse', 'HEAD']);
  git(ws.cwd, ['update-ref', HEAD_BASE_REF_NAME, headBaseSha]);
  const porcelain = tryGit(ws.cwd, ['status', '--porcelain']) ?? '';
  const trackedDirty = porcelain.split('\n').filter((l) => l && !l.startsWith('??')).length > 0;
  let worktreeBaseSha;
  let snapshotted = false;
  if (trackedDirty) {
    const stashSha = tryGit(ws.cwd, ['stash', 'create', 'plan-forge-flow baseline']);
    if (stashSha) {
      worktreeBaseSha = stashSha;
      snapshotted = true;
    }
  }
  if (!worktreeBaseSha) worktreeBaseSha = headBaseSha;
  git(ws.cwd, ['update-ref', WORKTREE_BASE_REF_NAME, worktreeBaseSha]);

  const untracked = (tryGit(ws.cwd, ['ls-files', '--others', '--exclude-standard']) ?? '')
    .split('\n')
    .filter(Boolean)
    .sort();
  state.untrackedBaseline = untracked.map((rel) => ({
    path: rel,
    hash: existsSync(join(ws.cwd, rel)) ? sha256(readFileSync(join(ws.cwd, rel))) : 'missing',
  }));

  state.headBaseRef = HEAD_BASE_REF_NAME;
  state.headBaseSha = headBaseSha;
  state.worktreeBaseRef = WORKTREE_BASE_REF_NAME;
  state.worktreeBaseSha = worktreeBaseSha;
  state.phase = 'build';
  state.taskIndex = 0;
  ws.saveState(state);
  ws.appendEvent('begin-build', {
    headBaseSha,
    worktreeBaseSha,
    snapshotted,
    untrackedBaseline: state.untrackedBaseline.length,
  });
  out({
    action: 'build-started',
    baseRef: headBaseSha,
    headBaseRef: HEAD_BASE_REF_NAME,
    headBaseSha,
    worktreeBaseRef: WORKTREE_BASE_REF_NAME,
    worktreeBaseSha,
    snapshotted,
    untrackedBaseline: state.untrackedBaseline.map((u) => u.path),
  });
}

function isProbablyBinary(buffer) {
  const sample = buffer.subarray(0, Math.min(buffer.length, 8192));
  return sample.includes(0);
}

function parseAllowedFiles(flags) {
  if (typeof flags['allow-files'] !== 'string') return new Set();
  let values;
  try {
    values = JSON.parse(flags['allow-files']);
  } catch {
    values = flags['allow-files'].split(',').map((value) => value.trim()).filter(Boolean);
  }
  if (!Array.isArray(values) || !values.every((value) => typeof value === 'string')) {
    throw new CliError(1, '--allow-files must be a JSON array or comma-separated paths');
  }
  return new Set(values.map((value) => value.replaceAll('\\', '/')));
}

function diffPaths(cwd, refs) {
  const output = git(cwd, ['diff', '--name-only', '-z', ...refs, '--', '.']);
  return output.split('\0').filter(Boolean).map((path) => path.replaceAll('\\', '/'));
}

function contentSignatureForTracked(ws, path, ref) {
  const absolute = join(ws.cwd, path);
  if (existsSync(absolute)) {
    try {
      const signature = classifySensitiveContent(readFileSync(absolute));
      if (signature) return signature;
      return null;
    } catch (error) {
      throw new CliError(1, `cannot screen tracked file ${path} for secrets: ${error.message}`);
    }
  }
  if (ref) {
    const content = tryGit(ws.cwd, ['show', `${ref}:${path}`]);
    if (content !== null) {
      const signature = classifySensitiveContent(content);
      if (signature) return signature;
    }
  }
  return null;
}

export function chunkPathspecs(paths, budget = 24 * 1024) {
  const chunks = [[]];
  let used = 0;
  for (const path of paths) {
    const spec = `:(top,literal)${path}`;
    if (used + spec.length + 1 > budget && chunks.at(-1).length) {
      chunks.push([]);
      used = 0;
    }
    chunks.at(-1).push(spec);
    used += spec.length + 1;
  }
  return chunks.filter((chunk) => chunk.length);
}

function writeSelectedDiff(cwd, refs, paths, target) {
  writeFileSync(target, '');
  for (const chunk of chunkPathspecs(paths)) {
    gitToFile(
      cwd,
      ['diff', '--no-ext-diff', '--binary', ...refs, '--', ...chunk],
      target,
      { append: true },
    );
  }
}

function addReviewSection(sections, section, budget) {
  const encoded = Buffer.from(section);
  const remaining = Math.max(0, REVIEW_AGGREGATE_LIMIT - budget.used);
  if (encoded.length <= remaining) {
    sections.push(encoded);
    budget.used += encoded.length;
    return { included: true, truncated: false, includedBytes: encoded.length };
  }
  if (remaining > 0) {
    sections.push(encoded.subarray(0, remaining));
    budget.used += remaining;
  }
  return { included: remaining > 0, truncated: true, includedBytes: remaining };
}

function cmdPrepareReview(ws, flags) {
  const state = ws.loadState();
  if (state.phase !== 'code-review') {
    throw new CliError(3, `prepare-review requires phase "code-review" (current: "${state.phase}")`);
  }
  if (!state.headBaseRef || !state.worktreeBaseRef) {
    throw new CliError(3, 'begin-build baselines are missing');
  }
  const requestedAllowances = parseAllowedFiles(flags);
  if (typeof flags['allow-files'] === 'string' &&
      (typeof flags['user-note'] !== 'string' || !flags['user-note'].trim())) {
    throw new CliError(1, '--allow-files requires --user-note "<the user consent to disclose these files>"');
  }
  const allowed = new Set(state.reviewAllowances?.files ?? []);
  for (const path of requestedAllowances) allowed.add(path);
  if (requestedAllowances.size) {
    state.reviewAllowances = {
      files: [...allowed].sort(),
      userNote: flags['user-note'].trim(),
      grantedAt: new Date().toISOString(),
    };
  }
  const baseline = new Map((state.untrackedBaseline ?? []).map((item) => [item.path.replaceAll('\\', '/'), item.hash]));
  const untracked = (tryGit(ws.cwd, ['ls-files', '--others', '--exclude-standard']) ?? '')
    .split('\n')
    .filter(Boolean)
    .sort();
  const entries = [];
  const untrackedSections = [];
  const budget = { used: 0 };
  for (const gitPath of untracked) {
    const normalized = gitPath.replaceAll('\\', '/');
    if (normalized === 'PLAN.md' || normalized === 'PLAN-REVIEW-LOG.md') {
      entries.push({
        path: normalized,
        size: null,
        hash: null,
        preExisting: baseline.has(normalized),
        changedDuringRun: false,
        classification: 'excluded-by-design',
        included: false,
        requiresPermission: false,
        excludedByDesign: true,
      });
      continue;
    }
    const absolute = resolve(ws.cwd, gitPath);
    let fileStat;
    try {
      fileStat = statSync(absolute);
    } catch (error) {
      entries.push({
        path: normalized,
        size: null,
        hash: null,
        preExisting: baseline.has(normalized),
        changedDuringRun: true,
        classification: 'read-failed',
        included: false,
        requiresPermission: true,
        withheldReason: `stat-failed:${error.code ?? 'unknown'}`,
      });
      continue;
    }
    if (fileStat.size > REVIEW_TEXT_LIMIT) {
      entries.push({
        path: normalized,
        size: fileStat.size,
        hash: null,
        preExisting: baseline.has(normalized),
        changedDuringRun: true,
        classification: 'larger-than-100KB',
        included: false,
        requiresPermission: true,
        withheldReason: 'larger-than-100KB',
      });
      continue;
    }
    let data;
    try {
      data = readFileSync(absolute);
    } catch (error) {
      entries.push({
        path: normalized,
        size: fileStat.size,
        hash: null,
        preExisting: baseline.has(normalized),
        changedDuringRun: true,
        classification: 'read-failed',
        included: false,
        requiresPermission: true,
        withheldReason: `read-failed:${error.code ?? 'unknown'}`,
      });
      continue;
    }
    const contentSignature = classifySensitiveContent(data);
    const secretLike = classifySensitivePath(normalized) || Boolean(contentSignature);
    const binary = isProbablyBinary(data);
    const withheldReason = secretLike
      ? `secret-like${contentSignature ? `:${contentSignature}` : '-path'}`
      : binary ? 'binary' : null;
    const permitted = !withheldReason || allowed.has(normalized);
    const hash = sha256(data);
    const baselineHash = baseline.get(normalized);
    const preExisting = baseline.has(normalized);
    const entry = {
      path: normalized,
      size: data.length,
      hash,
      preExisting,
      changedDuringRun: !preExisting || baselineHash !== hash,
      classification: withheldReason ?? 'text',
      included: permitted,
      requiresPermission: Boolean(withheldReason && !permitted),
      withheldReason: withheldReason && !permitted ? withheldReason : null,
    };
    if (permitted) {
      const body = binary
        ? `[binary content permitted; encoding=base64; sha256=${hash}; size=${data.length}]\n${data.toString('base64')}`
        : data.toString('utf8');
      const result = addReviewSection(
        untrackedSections,
        `\n--- PLAN-FORGE UNTRACKED: ${normalized} ---\n` +
        `preExisting=${preExisting} classification=${entry.classification}\n${body}\n`,
        budget,
      );
      entry.included = result.included;
      entry.truncated = result.truncated;
      entry.includedBytes = result.includedBytes;
      if (result.truncated) entry.withheldReason = 'aggregate-budget-exceeded';
    }
    entries.push(entry);
  }
  const currentUntracked = new Set(untracked.map((path) => path.replaceAll('\\', '/')));
  for (const [path, hash] of baseline) {
    if (currentUntracked.has(path)) continue;
    if (path === 'PLAN.md' || path === 'PLAN-REVIEW-LOG.md') {
      entries.push({
        path,
        size: null,
        hash: null,
        baselineHash: hash,
        preExisting: true,
        changedDuringRun: true,
        classification: 'excluded-by-design',
        included: false,
        requiresPermission: false,
        excludedByDesign: true,
      });
      continue;
    }
    const entry = {
      path,
      size: null,
      hash: null,
      baselineHash: hash,
      preExisting: true,
      changedDuringRun: true,
      classification: 'deleted-during-run',
      included: true,
      requiresPermission: false,
    };
    const result = addReviewSection(
      untrackedSections,
      `\n--- PLAN-FORGE UNTRACKED DELETION: ${path} ---\n` +
      `preExisting=true baselineSha256=${hash}\n`,
      budget,
    );
    entry.included = result.included;
    entry.truncated = result.truncated;
    entry.includedBytes = result.includedBytes;
    if (result.truncated) entry.withheldReason = 'aggregate-budget-exceeded';
    entries.push(entry);
  }

  const netTrackedFiles = diffPaths(ws.cwd, [state.headBaseRef]);
  const preExistingTrackedFiles = diffPaths(ws.cwd, [state.headBaseRef, state.worktreeBaseRef]);
  const inRunTrackedFiles = diffPaths(ws.cwd, [state.worktreeBaseRef]);
  const allTrackedFiles = [...new Set([
    ...netTrackedFiles,
    ...preExistingTrackedFiles,
    ...inRunTrackedFiles,
  ])].sort();
  const trackedEntries = [];
  const safeTracked = new Set();
  const inRunTracked = new Set(inRunTrackedFiles);
  for (const path of allTrackedFiles) {
    let classification;
    if (classifySensitivePath(path)) {
      classification = 'secret-like-path';
    } else if (allowed.has(path)) {
      classification = 'user-allowed';
    } else {
      const deletedRef = inRunTracked.has(path)
        ? state.worktreeBaseRef
        : state.headBaseRef;
      const signature = contentSignatureForTracked(ws, path, deletedRef);
      classification = signature ? `secret-like:${signature}` : 'text';
    }
    const permitted = classification === 'text' || allowed.has(path);
    trackedEntries.push({
      path,
      classification,
      included: permitted,
      requiresPermission: !permitted,
      withheldReason: permitted ? null : classification,
    });
    if (permitted) safeTracked.add(path);
  }
  const manifestPath = join(ws.forgeDir, 'review-manifest.json');
  const preExistingPath = join(ws.forgeDir, 'pre-existing.patch');
  const inRunPath = join(ws.forgeDir, 'in-run.patch');
  const untrackedPath = join(ws.forgeDir, 'untracked-review.patch');
  const summaryPath = join(ws.forgeDir, 'changed-files.txt');
  const obsoleteUnionPath = join(ws.forgeDir, 'final-review.patch');
  if (existsSync(obsoleteUnionPath)) unlinkSync(obsoleteUnionPath);
  writeSelectedDiff(
    ws.cwd,
    [state.headBaseRef, state.worktreeBaseRef],
    preExistingTrackedFiles.filter((path) => safeTracked.has(path)),
    preExistingPath,
  );
  writeSelectedDiff(
    ws.cwd,
    [state.worktreeBaseRef],
    inRunTrackedFiles.filter((path) => safeTracked.has(path)),
    inRunPath,
  );
  writeFileSync(untrackedPath, Buffer.concat(untrackedSections));
  writeFileSync(
    summaryPath,
    [
      ...allTrackedFiles.map((path) => `tracked\t${path}`),
      ...entries.map((entry) => `untracked:${entry.classification}\t${entry.path}`),
    ].join('\n') + '\n',
  );
  const limitations = [
    ...trackedEntries.filter((entry) => entry.requiresPermission),
    ...entries.filter((entry) => entry.requiresPermission || entry.truncated),
  ].map((entry) => ({ path: entry.path, reason: entry.withheldReason ?? entry.classification }));
  const manifest = {
    version: 2,
    workspaceRoot: ws.cwd,
    diffScope: { cwd: ws.cwd, pathspec: '.' },
    headBaseRef: state.headBaseRef,
    headBaseSha: state.headBaseSha,
    worktreeBaseRef: state.worktreeBaseRef,
    worktreeBaseSha: state.worktreeBaseSha,
    tracked: {
      preExistingPatch: '.forge/pre-existing.patch',
      inRunPatch: '.forge/in-run.patch',
      nameSummary: '.forge/changed-files.txt',
      preExistingFiles: preExistingTrackedFiles,
      files: trackedEntries,
    },
    untrackedEvidence: '.forge/untracked-review.patch',
    untracked: entries,
    withheld: limitations.map((entry) => entry.path),
    limitations,
    coverageReady: limitations.length === 0,
    aggregateBudgetBytes: REVIEW_AGGREGATE_LIMIT,
    aggregateIncludedBytes: budget.used,
    allowances: state.reviewAllowances ?? null,
    preExistingFixAuthorization: state.preExistingFixAuthorization ?? [],
    generatedAt: new Date().toISOString(),
  };
  writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + '\n');
  delete state.reviewManifest;
  ws.saveState(state);
  ws.appendEvent('prepare-review', {
    untracked: entries.length,
    withheldCount: manifest.withheld.length,
    preExistingTrackedFileCount: preExistingTrackedFiles.length,
  });
  out({
    action: 'review-prepared',
    patches: {
      preExisting: '.forge/pre-existing.patch',
      inRun: '.forge/in-run.patch',
      untracked: '.forge/untracked-review.patch',
      changedFiles: '.forge/changed-files.txt',
    },
    manifest: '.forge/review-manifest.json',
    untracked: entries,
    withheld: manifest.withheld,
    coverageReady: manifest.coverageReady,
  });
}

function cmdAuthorizePreExisting(ws, flags) {
  const state = ws.loadState();
  if (typeof flags.files !== 'string') {
    throw new CliError(1, 'authorize-preexisting requires --files <JSON array>');
  }
  if (typeof flags['user-note'] !== 'string' || !flags['user-note'].trim()) {
    throw new CliError(1, 'authorize-preexisting requires --user-note "<the user opt-in>"');
  }
  let files;
  try {
    files = JSON.parse(flags.files);
  } catch {
    throw new CliError(1, '--files must be a JSON array');
  }
  if (!Array.isArray(files) || !files.every((file) => typeof file === 'string')) {
    throw new CliError(1, '--files must be a JSON array of paths');
  }
  state.preExistingFixAuthorization = [...new Set(files)];
  ws.saveState(state);
  ws.appendEvent('authorize-preexisting', {
    files: state.preExistingFixAuthorization,
    userNote: flags['user-note'].trim(),
  });
  out({ action: 'pre-existing-fixes-authorized', files: state.preExistingFixAuthorization });
}

function observedSpawn(flags) {
  return {
    model: typeof flags['observed-model'] === 'string' ? flags['observed-model'].trim() : null,
    effort: typeof flags['observed-effort'] === 'string' ? flags['observed-effort'].trim() : null,
  };
}

function modelPinMismatchEvent(role, pin, observed, extra = {}) {
  const mismatch = Boolean(
    (observed.model && observed.model !== pin?.model) ||
    (observed.effort && observed.effort !== pin?.effort),
  );
  if (!mismatch) return null;
  note(
    `warning: observed ${role} ${observed.model ?? '<unknown>'}/${observed.effort ?? '<unknown>'} ` +
    `does not match pin ${pin?.model ?? '<unknown>'}/${pin?.effort ?? '<unknown>'}`,
  );
  return { role, ...extra, requested: pin ?? null, observed };
}

function cmdBuilderSession(ws, flags) {
  const state = ws.loadState();
  if (typeof flags.id !== 'string' || !flags.id.trim()) {
    throw new CliError(1, 'builder-session requires --id <native agent id>');
  }
  const previous = state.builderAgentId ?? null;
  if (previous && previous !== flags.id && typeof flags['user-note'] !== 'string') {
    throw new CliError(1, 'replacing a pinned builder id requires --user-note with the recovery reason');
  }
  state.builderAgentId = flags.id.trim();
  const observed = observedSpawn(flags);
  state.builderSessionObservation = {
    id: state.builderAgentId,
    ...observed,
  };
  if (state.pendingDispatch?.stage === 'build' || state.pendingDispatch?.stage === 'fix') {
    state.pendingDispatch.observedModel = observed.model;
    state.pendingDispatch.observedEffort = observed.effort;
  }
  const pin = state.modelSelection?.builder;
  const pinMismatchEvent = modelPinMismatchEvent('builder', pin, observed);
  ws.saveState(state);
  if (pinMismatchEvent) ws.appendEvent('model-pin-not-applied', pinMismatchEvent);
  ws.appendEvent('builder-session', {
    id: state.builderAgentId,
    previous,
    recovered: Boolean(previous && previous !== state.builderAgentId),
    userNote: flags['user-note'] ?? null,
    observedModel: observed.model,
    observedEffort: observed.effort,
  });
  out({ action: previous ? 'builder-session-recovered' : 'builder-session-pinned', id: state.builderAgentId });
}

function cmdReviewerSession(ws, flags) {
  const state = ws.loadState();
  const pending = state.pendingDispatch;
  if (!pending || (pending.stage !== 'plan' && pending.stage !== 'code')) {
    throw new CliError(3, 'reviewer-session requires a pending plan or code dispatch');
  }
  if (typeof flags.id !== 'string' || !flags.id.trim()) {
    throw new CliError(1, 'reviewer-session requires --id <fresh native agent id>');
  }
  if (flags['dispatch-id'] !== pending.dispatchId) {
    throw new CliError(3, 'reviewer-session --dispatch-id does not match the pending dispatch');
  }
  const id = flags.id.trim();
  state.reviewerAgentIds ??= [];
  if (state.reviewerAgentIds.includes(id)) {
    throw new CliError(3, `reviewer agent ${id} was already used in this run; spawn a fresh reviewer`);
  }
  state.reviewerAgentIds.push(id);
  pending.reviewerAgentId = id;
  const observed = observedSpawn(flags);
  pending.observedModel = observed.model;
  pending.observedEffort = observed.effort;
  const pin = state.modelSelection?.reviewer;
  const pinMismatchEvent = modelPinMismatchEvent(
    'reviewer',
    pin,
    observed,
    { dispatchId: pending.dispatchId },
  );
  ws.saveState(state);
  if (pinMismatchEvent) ws.appendEvent('model-pin-not-applied', pinMismatchEvent);
  ws.appendEvent('reviewer-session', {
    id,
    dispatchId: pending.dispatchId,
    stage: pending.stage,
    observedModel: observed.model,
    observedEffort: observed.effort,
  });
  out({ action: 'fresh-reviewer-registered', id, dispatchId: pending.dispatchId, stage: pending.stage });
}

function cmdStatus(ws, flags) {
  if (!ws.hasState()) {
    out({ exists: false });
    return;
  }
  const state = ws.loadState();
  const gdir = globalAgentsDir();
  const files = {
    plan: existsSync(ws.planPath),
    log: existsSync(ws.logPath),
    reviewerAgent: existsSync(join(gdir, 'forge_reviewer.toml')),
    builderAgent: existsSync(join(gdir, 'forge_builder.toml')),
  };
  const nextTask =
    state.taskCount && state.taskIndex < state.taskCount ? state.tasks[state.taskIndex] : null;
  const lastEvents = ws.lastEvents().map((event) =>
    Buffer.byteLength(JSON.stringify(event)) <= 512
      ? event
      : { ts: event.ts ?? null, cmd: event.cmd ?? null, truncated: true });
  const summary = {
    exists: true,
    version: state.version,
    phase: state.phase,
    amendmentReview: state.amendmentReview,
    deadlock: state.deadlock,
    round: state.round,
    fixRound: state.fixRound,
    taskIndex: state.taskIndex,
    taskCount: state.taskCount,
    nextTask,
    pendingDispatch: state.pendingDispatch,
    modelSelection: state.modelSelection,
    builderSelectionPlanHash: state.builderSelectionPlanHash,
    implementationApproval: state.implementationApproval,
    lastVerdict: state.lastVerdict,
    caps: {
      planRounds: state.maxRounds,
      fixRounds: state.maxFixRounds,
      buildRetries: state.maxBuildRetries,
      verdictRetries: state.maxVerdictRetries,
    },
    maxRounds: state.maxRounds,
    maxFixRounds: state.maxFixRounds,
    maxBuildRetries: state.maxBuildRetries,
    maxVerdictRetries: state.maxVerdictRetries,
    files,
    lastEvents,
  };
  if (!flags.full) {
    out(summary);
    return;
  }
  let pendingFingerprintMatches = null;
  let treeFingerprint = null;
  if (state.pendingDispatch &&
      (state.pendingDispatch.stage === 'build' || state.pendingDispatch.stage === 'fix')) {
    treeFingerprint = ws.treeFingerprint();
    pendingFingerprintMatches = treeFingerprint === state.pendingDispatch.fingerprint;
  }
  out({ ...state, ...summary, treeFingerprint, pendingFingerprintMatches });
}

const SET_KEYS = new Set([
  'phase',
  'maxRounds',
  'maxFixRounds',
  'maxBuildRetries',
  'maxVerdictRetries',
  'deadlock',
  'verifyCommands',
]);
const SENSITIVE_KEYS = new Set([
  'maxRounds',
  'maxFixRounds',
  'maxBuildRetries',
  'maxVerdictRetries',
  'deadlock',
]);
// Legal forward transitions; special guards below refine several of them.
const TRANSITIONS = {
  grill: ['review'],
  review: ['signoff', 'build'],
  signoff: ['review'],
  build: ['review', 'code-review'],
  'code-review': ['done', 'done-with-findings'],
  done: [],
  'done-with-findings': [],
};

function cmdSet(ws, flags, positional) {
  const state = ws.loadState();
  const updates = {};
  for (const pair of positional) {
    const eq = pair.indexOf('=');
    if (eq === -1) throw new CliError(1, `set expects key=value pairs (got "${pair}")`);
    const key = pair.slice(0, eq);
    const value = pair.slice(eq + 1);
    if (!SET_KEYS.has(key)) throw new CliError(1, `unknown or CLI-owned key "${key}" — use the dedicated subcommand`);
    updates[key] = value;
  }
  if (Object.keys(updates).length === 0) throw new CliError(1, 'set requires at least one key=value pair');

  const updatedKeys = Object.keys(updates);
  const override = Boolean(flags['user-override']);
  const userNote = typeof flags['user-note'] === 'string' ? flags['user-note'] : null;
  const needsNote = override || updatedKeys.some((k) => SENSITIVE_KEYS.has(k));
  if (needsNote && !userNote) {
    throw new CliError(1, 'sensitive mutations require --user-note "<the user\'s words authorizing this>"');
  }

  if ('phase' in updates) {
    const next = updates.phase;
    if (!PHASES.includes(next)) throw new CliError(1, `unknown phase "${next}"`);
    if (state.phase === 'code-review' && next === 'done') {
      const lv = state.lastVerdict;
      if (!lv || lv.stage !== 'code' || lv.action !== 'approved' || lv.coverage !== 'FULL') {
        throw new CliError(3, 'code-review → done requires an approved FULL-coverage verdict');
      }
    }
    if (state.phase === 'code-review' && next === 'done-with-findings') {
      const lv = state.lastVerdict;
      const cappedReview = state.deadlock && lv?.stage === 'code' && lv.action === 'deadlock';
      const cappedInvalidVerdict =
        state.pendingDispatch?.stage === 'code' &&
        state.pendingDispatch.lastInvalidVerdict &&
        state.pendingDispatch.retries >= (state.maxVerdictRetries ?? 2);
      if (!override || !userNote || (!cappedReview && !cappedInvalidVerdict)) {
        throw new CliError(
          3,
          'done-with-findings requires a capped code review or invalid-verdict recovery plus --user-override --user-note "<accepted risk>"',
        );
      }
      if (cappedInvalidVerdict) state.pendingDispatch = null;
    }
    if (!override) {
      const legal = TRANSITIONS[state.phase] ?? [];
      if (!legal.includes(next)) {
        const hint = state.phase === 'signoff' && next === 'build' ? ' (use begin-build)' : '';
        throw new CliError(3, `illegal transition ${state.phase} → ${next}${hint}`);
      }
      if (state.phase === 'review' && next === 'signoff') {
        const lv = state.lastVerdict;
        if (!lv || lv.stage !== 'plan' || lv.action !== 'approved') {
          throw new CliError(3, 'review → signoff requires an approved plan verdict (or --user-override for a user-sanctioned deadlock sign-off)');
        }
        if (lv.planHash !== ws.planHash()) {
          throw new CliError(
            3,
            'PLAN.md changed after the approving review; send the current plan through another review round ' +
            'before sign-off (or --user-override --user-note to accept an unreviewed edit)',
          );
        }
      }
      if (state.phase === 'build' && next === 'review') {
        if (!flags.amendment) throw new CliError(3, 'build → review requires --amendment (the amendment loop)');
      }
      if (state.phase === 'review' && next === 'build') {
        if (!state.amendmentReview) throw new CliError(3, 'review → build is only legal when returning from an amendment review');
        const lv = state.lastVerdict;
        if (!lv || lv.stage !== 'plan' || lv.action !== 'approved') {
          throw new CliError(3, 'amendment review must be approved before returning to build');
        }
        if (lv.planHash !== ws.planHash()) {
          throw new CliError(
            3,
            'the amendment review approved a different PLAN.md revision; dispatch a fresh amendment review',
          );
        }
        requireCurrentImplementationApproval(ws, state, 'returning to build after an amendment');
        requireCurrentBuilderSelection(ws, state, 'returning to build after an amendment');
      }
      if (state.phase === 'build' && next === 'code-review') {
        if (state.taskIndex !== state.taskCount) {
          throw new CliError(3, `build → code-review requires all tasks complete (${state.taskIndex}/${state.taskCount})`);
        }
        if (state.pendingDispatch) throw new CliError(3, 'build → code-review requires no pending dispatch');
      }
    }
    if (state.phase === 'review' && next === 'signoff') {
      state.implementationApproval = null;
      if (state.pendingDispatch?.stage === 'plan') state.pendingDispatch = null;
    }
    if (state.phase === 'signoff' && next === 'review') {
      state.implementationApproval = null;
    }
    if (state.phase === 'build' && next === 'review') {
      state.amendmentReview = true;
      state.amendmentReviewConsumed = false;
      state.lastVerdict = null;
      state.implementationApproval = null;
    }
    if (state.phase === 'review' && next === 'build') state.amendmentReview = false;
    state.phase = next;
    delete updates.phase;
  }
  for (const key of ['maxRounds', 'maxFixRounds', 'maxBuildRetries', 'maxVerdictRetries']) {
    if (!(key in updates)) continue;
    const next = parsePositiveInt(updates[key], key);
    const current = state[key];
    if (next !== current + 1) {
      throw new CliError(3, `${key} may only be extended by exactly one round at a reached cap (${current} → ${current + 1})`);
    }
    const atCap =
      key === 'maxRounds'
        ? state.lastVerdict?.stage === 'plan' && state.lastVerdict.action === 'deadlock' && state.round >= current
        : key === 'maxFixRounds'
          ? state.lastVerdict?.stage === 'code' && state.lastVerdict.action === 'deadlock' && state.fixRound >= current
          : key === 'maxBuildRetries'
            ? (state.pendingDispatch?.stage === 'build' || state.pendingDispatch?.stage === 'fix') &&
              state.pendingDispatch.retries >= current
            : (state.pendingDispatch?.stage === 'plan' || state.pendingDispatch?.stage === 'code') &&
              state.pendingDispatch.lastInvalidVerdict &&
              state.pendingDispatch.retries >= current;
    if (!atCap) {
      throw new CliError(3, `${key} may only be extended after its current cap (${current}) is reached`);
    }
    state[key] = next;
    if (key === 'maxRounds' || key === 'maxFixRounds') state.deadlock = false;
  }
  if ('deadlock' in updates) {
    if (updates.deadlock !== 'true' && updates.deadlock !== 'false') throw new CliError(1, 'deadlock must be true or false');
    state.deadlock = updates.deadlock === 'true';
  }
  if ('verifyCommands' in updates) {
    let arr;
    try {
      arr = JSON.parse(updates.verifyCommands);
    } catch {
      throw new CliError(1, 'verifyCommands must be a JSON array literal');
    }
    if (!Array.isArray(arr) || !arr.every((c) => typeof c === 'string')) {
      throw new CliError(1, 'verifyCommands must be a JSON array of strings');
    }
    state.verifyCommands = arr;
  }
  ws.saveState(state);
  ws.appendEvent('set', { updates: updatedKeys, phase: state.phase, override, userNote });
  out({
    action: 'state-updated',
    phase: state.phase,
    maxRounds: state.maxRounds,
    maxFixRounds: state.maxFixRounds,
    maxBuildRetries: state.maxBuildRetries,
    maxVerdictRetries: state.maxVerdictRetries,
    deadlock: state.deadlock,
    amendmentReview: state.amendmentReview,
    verifyCommands: state.verifyCommands,
  });
}

function cmdCleanup(ws, flags) {
  const report = {
    removedAgents: [],
    keptAgents: [],
    removedRefs: [],
    removedExcludeBlock: false,
    removedState: false,
    removedArtifacts: [],
    keptArtifacts: [],
    artifactDeletionBlocked: false,
    removedLedgerEntries: 0,
  };

  const artifactTargets = [
    { path: ws.planPath, name: 'PLAN.md' },
    { path: ws.logPath, name: 'PLAN-REVIEW-LOG.md' },
  ];
  if (flags['delete-artifacts']) {
    const invalid = artifactTargets.filter(({ path }) =>
      !existsSync(path) || !readFileSync(path, 'utf8').includes(OWNED_MARKER));
    if (invalid.length) {
      report.artifactDeletionBlocked = true;
      report.keptArtifacts = artifactTargets.filter(({ path }) => existsSync(path)).map(({ name }) => name);
      report.artifactDeletionReason =
        `pair deletion blocked: missing file or ownership marker for ${invalid.map(({ name }) => name).join(', ')}`;
    } else {
      for (const artifact of artifactTargets) {
        unlinkSync(artifact.path);
        report.removedArtifacts.push(artifact.name);
      }
    }
  } else {
    report.keptArtifacts = artifactTargets.filter(({ path }) => existsSync(path)).map(({ name }) => name);
  }

  // The subagents are global and shared across repos/runs — only remove them on
  // an explicit machine-wide purge, and only if plan-forge generated them.
  if (flags['purge-agents']) {
    const gdir = globalAgentsDir();
    for (const role of AGENT_ROLES) {
      const name = `forge_${role}.toml`;
      const p = join(gdir, name);
      if (!existsSync(p)) continue;
      if (readFileSync(p, 'utf8').includes(GENERATED_MARKER)) {
        unlinkSync(p);
        report.removedAgents.push(name);
      } else {
        report.keptAgents.push(`${name} (hand-authored — preserved)`);
      }
    }
  }
  if (flags['purge-ledger']) {
    for (const dir of [materializationJournalDir(), nonceTombstoneDir()]) {
      if (!existsSync(dir)) continue;
      report.removedLedgerEntries += readdirSync(dir).length;
      rmSync(dir, { recursive: true, force: true });
    }
  }
  for (const ref of [HEAD_BASE_REF_NAME, WORKTREE_BASE_REF_NAME]) {
    if (tryGit(ws.cwd, ['rev-parse', '--verify', ref])) {
      git(ws.cwd, ['update-ref', '-d', ref]);
      report.removedRefs.push(ref);
    }
  }
  const excludePath = ws.excludePath();
  if (excludePath) {
    removeExcludeBlock(excludePath);
    report.removedExcludeBlock = true;
  }
  const markerPath = ws.repositoryMarkerPath();
  if (markerPath && existsSync(markerPath)) {
    try {
      const marker = JSON.parse(readFileSync(markerPath, 'utf8'));
      const markerRoot = typeof marker.workspaceRoot === 'string' && existsSync(marker.workspaceRoot)
        ? realpathSync(marker.workspaceRoot)
        : typeof marker.workspaceRoot === 'string' ? resolve(marker.workspaceRoot) : null;
      if (markerRoot === ws.cwd) unlinkSync(markerPath);
    } catch (error) {
      note(`warning: repository forge marker was not removed: ${error.message}`);
    }
  }
  ws.appendEvent('cleanup', report);
  if (existsSync(ws.forgeDir)) {
    rmSync(ws.forgeDir, { recursive: true, force: true });
    report.removedState = true;
  }
  out({
    action: 'cleaned',
    ...report,
    purgedAgents: Boolean(flags['purge-agents']),
    purgedLedger: Boolean(flags['purge-ledger']),
    deleteArtifactsRequested: Boolean(flags['delete-artifacts']),
  });
}

function parsePositiveInt(value, name) {
  const n = Number(value);
  if (!Number.isInteger(n) || n < 1) throw new CliError(1, `${name} must be a positive integer`);
  return n;
}

function requireDefaultModeForCommand(cwd, action) {
  try {
    requireDefaultMode(cwd, action);
  } catch (error) {
    throw new CliError(3, error.message);
  }
}

// ---------------------------------------------------------------------------
// Entry point

const LOCKING_COMMANDS = new Set([
  'lock-plan',
  'dispatch',
  'complete',
  'resolve-build',
  'verdict',
  'begin-build',
  'prepare-review',
  'authorize-preexisting',
  'builder-session',
  'reviewer-session',
  'set',
  'cleanup',
]);

export async function main(argv = process.argv.slice(2)) {
  try {
    const [cmd, ...rest] = argv;
    const { flags, positional } = parseArgs(rest);
    const cwd = typeof flags.cwd === 'string' ? flags.cwd : process.cwd();
    if (cmd === 'models') {
      await cmdModels(cwd, flags);
      return;
    }
    if (cmd === 'picker') {
      await cmdPicker(cwd, flags, positional);
      return;
    }
    if (cmd === 'issue-approval') {
      await cmdIssueApproval(cwd, flags, positional);
      return;
    }
    if (cmd === 'configure-reviewer') {
      await cmdConfigureReviewer(cwd, flags);
      return;
    }
    if (cmd === 'configure-builder') {
      await cmdConfigureBuilder(cwd, flags);
      return;
    }
    if (cmd === 'install-agents') {
      requireDefaultModeForCommand(cwd, 'install-agents');
      cmdInstallAgents();
      return;
    }
    if (cmd === 'resume') {
      if (positional.length > 0 || Object.keys(flags).some((name) => name !== 'cwd')) {
        throw new CliError(1, 'resume accepts only --cwd; approval data comes from the authenticated native prompt');
      }
      const { materializeApprovedRun } = await import('./materialize.mjs');
      out(await materializeApprovedRun({
        cwd,
        stateFactory: stateFromApprovedEnvelope,
        amendmentStateFactory: stateFromApprovedAmendment,
      }));
      return;
    }
    const ws = new Workspace(cwd);
    if (cmd === 'doctor') {
      cmdDoctor(ws);
      return;
    }
    if (cmd === 'status') {
      cmdStatus(ws, flags);
      return;
    }
    if (!LOCKING_COMMANDS.has(cmd)) {
      throw new CliError(
        1,
        `unknown command "${cmd ?? ''}" — expected doctor|models|picker|issue-approval|configure-reviewer|configure-builder|resume|install-agents|lock-plan|dispatch|complete|resolve-build|verdict|begin-build|prepare-review|authorize-preexisting|builder-session|reviewer-session|status|set|cleanup`,
      );
    }
    requireDefaultModeForCommand(cwd, cmd);
    ws.acquireLock();
    try {
      switch (cmd) {
        case 'lock-plan':
          cmdLockPlan(ws, flags);
          break;
        case 'dispatch':
          await cmdDispatch(ws, flags);
          break;
        case 'complete':
          cmdComplete(ws, flags);
          break;
        case 'resolve-build':
          cmdResolveBuild(ws, flags);
          break;
        case 'verdict':
          cmdVerdict(ws, flags);
          break;
        case 'begin-build':
          cmdBeginBuild(ws);
          break;
        case 'prepare-review':
          cmdPrepareReview(ws, flags);
          break;
        case 'authorize-preexisting':
          cmdAuthorizePreExisting(ws, flags);
          break;
        case 'builder-session':
          cmdBuilderSession(ws, flags);
          break;
        case 'reviewer-session':
          cmdReviewerSession(ws, flags);
          break;
        case 'set':
          cmdSet(ws, flags, positional);
          break;
        case 'cleanup':
          cmdCleanup(ws, flags);
          break;
      }
    } finally {
      ws.releaseLock();
    }
  } catch (e) {
    if (e instanceof CliError) {
      out({ error: e.message, ...e.extra });
      process.exitCode = e.code;
      return;
    }
    out({ error: e?.message ?? String(e), unexpected: true });
    process.exitCode = 1;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main();
}
