import {
  closeSync,
  existsSync,
  fsyncSync,
  mkdirSync,
  openSync,
  readFileSync,
  readdirSync,
  realpathSync,
  renameSync,
  statSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { execFileSync } from 'node:child_process';
import { dirname, isAbsolute, join, relative, resolve } from 'node:path';
import process from 'node:process';
import {
  canonicalRepositoryIdentity,
  materializationJournalDir,
  nonceTombstoneDir,
  pluginDataDir,
  sessionContextPathFor,
} from './plugin-paths.mjs';
import {
  repositoryIdentityEquals,
  sha256,
} from './resume-envelope.mjs';
import { authorizeNativeImplementation } from './transcript-auth.mjs';
import {
  CodexDebugModelsProvider,
  ModelCatalogResolver,
} from './model-catalog.mjs';

const EXCLUDE_BEGIN = '# >>> plan-forge-flow (managed) >>>';
const EXCLUDE_END = '# <<< plan-forge-flow (managed) <<<';
const EXCLUDE_BLOCK = `${EXCLUDE_BEGIN}\n.forge/\n${EXCLUDE_END}`;
const JOURNAL_VERSION = 2;
const CRASH_EXIT_CODE = 86;
const OWNED_MARKER = '<!-- plan-forge-flow:owned -->';
const TRANSACTION_ID_RE = /^[a-f0-9]{32}$/;
const NONCE_RE = /^[A-Za-z0-9_-]{43}$/;

function fail(message) {
  throw new Error(`Forge materialization failed: ${message}`);
}

function git(cwd, args) {
  return execFileSync('git', ['-C', cwd, ...args], {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
  }).trim();
}

function fsyncDirectory(path) {
  let fd;
  try {
    fd = openSync(path, 'r');
    fsyncSync(fd);
  } catch (error) {
    if (!['EINVAL', 'EPERM', 'EISDIR'].includes(error?.code)) throw error;
  } finally {
    if (fd !== undefined) closeSync(fd);
  }
}

function ensureDirectory(path) {
  if (existsSync(path)) {
    if (!statSync(path).isDirectory()) fail(`expected directory is not a directory: ${path}`);
    return false;
  }
  ensureDirectory(dirname(path));
  mkdirSync(path);
  fsyncDirectory(dirname(path));
  return true;
}

function writeDurable(path, content, options = {}) {
  ensureDirectory(dirname(path));
  const flag = options.exclusive ? 'wx' : 'w';
  const fd = openSync(path, flag);
  try {
    writeFileSync(fd, content);
    fsyncSync(fd);
  } finally {
    closeSync(fd);
  }
  fsyncDirectory(dirname(path));
}

function writeJsonAtomic(path, value) {
  ensureDirectory(dirname(path));
  const temp = `${path}.${process.pid}.${Date.now()}.tmp`;
  writeDurable(temp, JSON.stringify(value, null, 2) + '\n', { exclusive: true });
  renameSync(temp, path);
  fsyncDirectory(dirname(path));
}

function hashFile(path) {
  return existsSync(path) ? sha256(readFileSync(path)) : null;
}

function crashAfter(label) {
  if (process.env.FORGE_CRASH_AT === label) process.exit(CRASH_EXIT_CODE);
}

function processAlive(pid) {
  if (!Number.isInteger(pid) || pid < 1) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error?.code === 'EPERM';
  }
}

function readRepositoryLock(path, label) {
  let value;
  try {
    value = JSON.parse(readFileSync(path, 'utf8'));
  } catch {
    fail(`${label} is unreadable: ${path}`);
  }
  if (value?.version !== 1 || !Number.isInteger(value.pid) || value.pid < 1 ||
      typeof value.token !== 'string' || !value.token) {
    fail(`${label} has an invalid schema: ${path}`);
  }
  return value;
}

function sameRepositoryLock(left, right) {
  return Boolean(left && right && left.pid === right.pid && left.token === right.token);
}

function acquireRepositoryLock(gitCommonDir) {
  const path = join(gitCommonDir, 'plan-forge-materialize.lock');
  const recoveryPath = `${path}.recovery`;
  const token = `${process.pid}:${Date.now()}:${Math.random()}`;
  const replacement = {
    version: 1,
    pid: process.pid,
    token,
    createdAt: new Date().toISOString(),
  };
  let guard = null;
  let acquired = false;
  if (!existsSync(recoveryPath)) {
    try {
      writeDurable(path, JSON.stringify(replacement) + '\n', { exclusive: true });
      acquired = true;
    } catch (error) {
      if (error?.code !== 'EEXIST') throw error;
    }
  }
  if (!acquired) {
    const held = existsSync(path)
      ? readRepositoryLock(path, 'repository materialization lock')
      : null;
    if (!existsSync(recoveryPath) && held && processAlive(held.pid)) {
      fail(`another materialization owns the repository lock (pid ${held.pid})`);
    }
    const freshGuard = {
      version: 1,
      pid: process.pid,
      token: `${token}:recovery`,
      phase: 'observed',
      observed: held ? { pid: held.pid, token: held.token } : null,
      replacement: { pid: process.pid, token },
      abandoned: [],
      createdAt: new Date().toISOString(),
    };
    try {
      writeDurable(recoveryPath, JSON.stringify(freshGuard) + '\n', { exclusive: true });
      guard = freshGuard;
    } catch (recoveryError) {
      if (recoveryError?.code !== 'EEXIST') throw recoveryError;
      const staleGuard = readRepositoryLock(
        recoveryPath,
        'repository materialization recovery guard',
      );
      if ((staleGuard.phase !== 'observed' && staleGuard.phase !== 'reclaiming') ||
          !Object.hasOwn(staleGuard, 'observed') ||
          !staleGuard.replacement ||
          !Number.isInteger(staleGuard.replacement.pid) ||
          typeof staleGuard.replacement.token !== 'string' ||
          !Array.isArray(staleGuard.abandoned) ||
          staleGuard.abandoned.some((lock) =>
            !Number.isInteger(lock?.pid) || typeof lock?.token !== 'string')) {
        fail(`repository materialization recovery guard has an invalid schema: ${recoveryPath}`);
      }
      if (processAlive(staleGuard.pid)) {
        fail(`another process is recovering the repository lock: ${recoveryPath}`);
      }
      const claimPath = `${recoveryPath}.stale-${sha256(token).slice(0, 16)}`;
      try {
        renameSync(recoveryPath, claimPath);
        fsyncDirectory(gitCommonDir);
      } catch (claimError) {
        if (claimError?.code === 'ENOENT' || claimError?.code === 'EEXIST') {
          fail('another process reclaimed the crashed materialization recovery guard');
        }
        throw claimError;
      }
      let installedReplacementGuard = false;
      try {
        const claimed = readRepositoryLock(
          claimPath,
          'claimed repository materialization recovery guard',
        );
        if (claimed.pid !== staleGuard.pid || claimed.token !== staleGuard.token) {
          fail('repository materialization recovery guard changed before reclamation');
        }
        guard = {
          ...staleGuard,
          pid: process.pid,
          token: freshGuard.token,
          replacement: freshGuard.replacement,
          abandoned: [...staleGuard.abandoned, staleGuard.replacement].slice(-16),
          createdAt: freshGuard.createdAt,
        };
        writeDurable(recoveryPath, JSON.stringify(guard) + '\n', { exclusive: true });
        installedReplacementGuard = true;
      } finally {
        try {
          if (!installedReplacementGuard && !existsSync(recoveryPath)) {
            renameSync(claimPath, recoveryPath);
          } else {
            unlinkSync(claimPath);
          }
          fsyncDirectory(gitCommonDir);
        } catch {
          // Retained claim/guard evidence fails closed on the next recovery.
        }
      }
    }
    crashAfter('after-materialization-recovery-guard');

    try {
      const current = existsSync(path)
        ? readRepositoryLock(path, 'repository materialization lock')
        : null;
      if (guard.phase === 'observed') {
        if (!sameRepositoryLock(current, guard.observed)) {
          fail('repository materialization lock token or PID changed during recovery');
        }
        if (current && processAlive(current.pid)) {
          fail('repository materialization lock owner is still alive');
        }
        const ownedGuard = readRepositoryLock(
          recoveryPath,
          'repository materialization recovery guard',
        );
        if (ownedGuard.pid !== process.pid || ownedGuard.token !== guard.token) {
          fail('repository materialization recovery guard ownership changed');
        }
        guard.phase = 'reclaiming';
        writeJsonAtomic(recoveryPath, guard);
      }

      const beforeUnlink = existsSync(path)
        ? readRepositoryLock(path, 'repository materialization lock')
        : null;
      const knownLocks = [guard.observed, guard.replacement, ...guard.abandoned].filter(Boolean);
      const matched = knownLocks.find((candidate) =>
        sameRepositoryLock(beforeUnlink, candidate));
      if (beforeUnlink && (!matched || processAlive(matched.pid))) {
        fail('repository materialization lock token or PID changed before unlink');
      }
      if (beforeUnlink) {
        const ownedGuard = readRepositoryLock(
          recoveryPath,
          'repository materialization recovery guard',
        );
        const finalLock = readRepositoryLock(path, 'repository materialization lock');
        if (ownedGuard.pid !== process.pid || ownedGuard.token !== guard.token ||
            !knownLocks.some((candidate) => sameRepositoryLock(finalLock, candidate))) {
          fail('repository materialization lock or guard changed before unlink');
        }
        unlinkSync(path);
        fsyncDirectory(gitCommonDir);
      }
      crashAfter('after-materialization-stale-unlink');
      writeDurable(path, JSON.stringify(replacement) + '\n', { exclusive: true });
      acquired = true;
      crashAfter('after-materialization-recovery-acquired');
    } finally {
      try {
        const ownedGuard = readRepositoryLock(
          recoveryPath,
          'repository materialization recovery guard',
        );
        if (acquired && guard &&
            ownedGuard.pid === process.pid && ownedGuard.token === guard.token) {
          unlinkSync(recoveryPath);
          fsyncDirectory(gitCommonDir);
        }
      } catch {
        // Crashes and integrity failures retain fail-closed recovery evidence.
      }
    }
  }
  return {
    release() {
      try {
        const held = JSON.parse(readFileSync(path, 'utf8'));
        if (held.token === token) {
          unlinkSync(path);
          fsyncDirectory(gitCommonDir);
        }
      } catch {
        // A crashed or competing recovery may already have removed the lock.
      }
    },
  };
}

function managedExclude(current) {
  const lines = current.replace(/\r\n/g, '\n').split('\n');
  const retained = [];
  let inManagedBlock = false;
  for (const line of lines) {
    if (line === EXCLUDE_BEGIN) {
      if (inManagedBlock) fail('git exclude contains nested Forge managed blocks');
      inManagedBlock = true;
      continue;
    }
    if (line === EXCLUDE_END) {
      if (!inManagedBlock) fail('git exclude contains an unmatched Forge managed-block terminator');
      inManagedBlock = false;
      continue;
    }
    if (!inManagedBlock) retained.push(line);
  }
  if (inManagedBlock) fail('git exclude contains an unterminated Forge managed block');
  const without = retained.join('\n').replace(/\n{3,}/g, '\n\n').trimEnd();
  return `${without ? `${without}\n` : ''}${EXCLUDE_BLOCK}\n`;
}

function captureFor(workspaceRoot) {
  const path = sessionContextPathFor(workspaceRoot);
  if (!existsSync(path)) fail('fresh UserPromptSubmit capture is missing');
  let capture;
  try {
    capture = JSON.parse(readFileSync(path, 'utf8'));
  } catch {
    fail('UserPromptSubmit capture is malformed');
  }
  if (capture.version !== 2 || capture.cwd !== workspaceRoot ||
      typeof capture.sessionId !== 'string' || typeof capture.turnId !== 'string' ||
      typeof capture.transcriptPath !== 'string') {
    fail('UserPromptSubmit capture is schema-incompatible');
  }
  const age = Date.now() - Date.parse(capture.observedAt);
  const configured = Number(process.env.FORGE_SESSION_MAX_AGE_MS);
  const maxAge = Number.isFinite(configured) && configured > 0 ? configured : 30 * 60 * 1000;
  if (!Number.isFinite(age) || age < 0 || age > maxAge) fail('UserPromptSubmit capture is stale');
  if (capture.collaborationMode !== 'default') {
    fail(`materialization requires a proven Default-mode prompt (observed ${capture.collaborationMode})`);
  }
  if (!capture.implementationCandidate) fail('captured prompt is not an implementation candidate');
  return capture;
}

function tombstonePath(nonce) {
  if (typeof nonce !== 'string' || !NONCE_RE.test(nonce)) {
    fail('approval nonce is malformed');
  }
  const dir = nonceTombstoneDir();
  const path = resolve(dir, `${nonce}.json`);
  assertContainedPath(dir, dirname(dir), 'nonce tombstone directory');
  assertContainedPath(path, dirname(dir), 'nonce tombstone path');
  return path;
}

function journalPath(nonce) {
  if (typeof nonce !== 'string' || !NONCE_RE.test(nonce)) {
    fail('approval nonce is malformed');
  }
  const dataRoot = pluginDataDir();
  const dir = materializationJournalDir();
  const path = resolve(dir, `${nonce}.json`);
  assertContainedPath(dir, dataRoot, 'materialization journal directory');
  assertContainedPath(path, dataRoot, 'materialization journal path');
  return path;
}

function revalidateJournalPath(nonce, expectedPath) {
  const current = journalPath(nonce);
  if (current !== expectedPath) fail('materialization journal path changed during recovery');
  return current;
}

function revalidateTombstonePath(nonce, expectedPath) {
  const current = tombstonePath(nonce);
  if (current !== expectedPath) fail('nonce tombstone path changed during materialization');
  return current;
}

function readJson(path, label) {
  try {
    return JSON.parse(readFileSync(path, 'utf8'));
  } catch {
    fail(`${label} is malformed: ${path}`);
  }
}

function assertJournalIdentity(journal, expected) {
  if (journal.version !== JOURNAL_VERSION ||
      !TRANSACTION_ID_RE.test(journal.transactionId) ||
      journal.transactionId !== expected.transactionId ||
      journal.payloadHash !== expected.payloadHash ||
      journal.nonce !== expected.nonce ||
      !repositoryIdentityEquals(journal.repository, expected.repository)) {
    fail('nonce is held by a foreign or mismatched pending transaction; preserve evidence and recover manually');
  }
}

function previousRevision(repositoryKey, ignoredNonce) {
  const dir = nonceTombstoneDir();
  if (!existsSync(dir)) return 0;
  let highest = 0;
  for (const entry of readdirSync(dir)) {
    if (!entry.endsWith('.json') || entry === `${ignoredNonce}.json`) continue;
    let tombstone;
    try {
      tombstone = JSON.parse(readFileSync(join(dir, entry), 'utf8'));
    } catch {
      fail(`nonce tombstone is malformed: ${entry}`);
    }
    if (tombstone.repositoryKey === repositoryKey) {
      highest = Math.max(highest, Number(tombstone.planRevision) || 0);
    }
  }
  return highest;
}

function stageOrVerify(path, content, expectedHash, crashLabel) {
  const current = hashFile(path);
  if (current === expectedHash) return;
  if (current !== null) fail(`staged materialization content is foreign or corrupt: ${path}`);
  writeDurable(path, content, { exclusive: true });
  crashAfter(crashLabel);
}

function promoteManagedFile(staged, final, expectedHash, priorHash, crashLabel) {
  const finalHash = hashFile(final);
  if (finalHash === expectedHash) return;
  if (finalHash !== priorHash) fail(`managed destination changed during materialization: ${final}`);
  if (hashFile(staged) !== expectedHash) fail(`staged content is missing or corrupt: ${staged}`);
  ensureDirectory(dirname(final));
  renameSync(staged, final);
  fsyncDirectory(dirname(final));
  crashAfter(crashLabel);
}

function assertOwnedPair(files) {
  const planExists = existsSync(files.plan.final);
  const logExists = existsSync(files.log.final);
  if (planExists !== logExists) {
    fail('retained PLAN.md and PLAN-REVIEW-LOG.md must be present as an owned pair');
  }
  if (!planExists) return;
  for (const name of ['plan', 'log']) {
    if (!readFileSync(files[name].final, 'utf8').startsWith(`${OWNED_MARKER}\n`)) {
      fail(`refusing to replace foreign retained artifact: ${files[name].final}`);
    }
  }
}

function expectedContents({ authorization, state, repository, transactionId, createdAt, journal }) {
  const forgeDir = join(repository.workspaceRoot, '.forge');
  const stagingDir = join(forgeDir, `materialize-${transactionId}`);
  const excludePathRaw = git(repository.workspaceRoot, ['rev-parse', '--git-path', 'info/exclude']);
  const excludePath = isAbsolute(excludePathRaw)
    ? excludePathRaw
    : join(repository.workspaceRoot, excludePathRaw);
  const activeMarkerPath = join(repository.gitCommonDir, 'plan-forge-active.json');
  const commitMarkerPath = join(forgeDir, 'materialization-commit.json');
  const critiquesDir = join(forgeDir, 'critiques');
  const stateContent = JSON.stringify(state, null, 2) + '\n';
  const activeMarkerContent = JSON.stringify({
    version: 2,
    workspaceRoot: repository.workspaceRoot,
    statePath: join(forgeDir, 'state.json'),
    transactionId,
    createdAt,
  }, null, 2) + '\n';
  const currentExclude = existsSync(excludePath) ? readFileSync(excludePath, 'utf8') : '';
  const excludeContent = managedExclude(currentExclude);
  const commitMarkerContent = JSON.stringify({
    version: 1,
    transactionId,
    nonce: authorization.envelope.nonce,
    humanPlanHash: authorization.envelope.humanPlanHash,
    repository,
    origin: authorization.envelope.origin,
    committedAt: createdAt,
  }, null, 2) + '\n';
  const files = {
    plan: {
      staged: join(stagingDir, 'PLAN.md'),
      final: join(repository.workspaceRoot, 'PLAN.md'),
      content: authorization.humanPlan,
    },
    log: {
      staged: join(stagingDir, 'PLAN-REVIEW-LOG.md'),
      final: join(repository.workspaceRoot, 'PLAN-REVIEW-LOG.md'),
      content: authorization.envelope.reviewLog,
    },
    state: {
      staged: join(stagingDir, 'state.json'),
      final: join(forgeDir, 'state.json'),
      content: stateContent,
    },
    activeMarker: {
      staged: join(stagingDir, 'plan-forge-active.json'),
      final: activeMarkerPath,
      content: activeMarkerContent,
    },
    exclude: {
      staged: join(stagingDir, 'exclude'),
      final: excludePath,
      content: excludeContent,
      priorHash: Object.hasOwn(journal?.expected?.exclude ?? {}, 'priorHash')
        ? journal.expected.exclude.priorHash
        : hashFile(excludePath),
    },
    commitMarker: {
      staged: join(stagingDir, 'materialization-commit.json'),
      final: commitMarkerPath,
      content: commitMarkerContent,
    },
  };
  for (const [name, file] of Object.entries(files)) {
    file.hash = sha256(file.content);
    if (name !== 'exclude') {
      file.priorHash = Object.hasOwn(journal?.expected?.[name] ?? {}, 'priorHash')
        ? journal.expected[name].priorHash
        : hashFile(file.final);
    }
  }
  return { forgeDir, critiquesDir, stagingDir, commitMarkerPath, files };
}

function finishLedger(journalFile, journal, tombstoneFile) {
  journalFile = revalidateJournalPath(journal.nonce, journalFile);
  tombstoneFile = revalidateTombstonePath(journal.nonce, tombstoneFile);
  if (journal.status !== 'committed') {
    journal.status = 'committed';
    journal.committedAt = new Date().toISOString();
    writeJsonAtomic(revalidateJournalPath(journal.nonce, journalFile), journal);
    crashAfter('after-journal-committed');
  }
  if (!existsSync(tombstoneFile)) {
    writeJsonAtomic(revalidateTombstonePath(journal.nonce, tombstoneFile), {
      version: 1,
      nonce: journal.nonce,
      transactionId: journal.transactionId,
      repositoryKey: journal.repositoryKey,
      originSessionId: journal.origin.sessionId,
      planRevision: journal.planRevision,
      committedAt: journal.committedAt,
    });
    crashAfter('after-tombstone');
  }
  if (!journal.acknowledged) {
    journal.acknowledged = true;
    journal.acknowledgedAt = new Date().toISOString();
    writeJsonAtomic(revalidateJournalPath(journal.nonce, journalFile), journal);
    crashAfter('after-journal-acknowledged');
    return { recovered: journal.recovered === true };
  }
  fail('approval nonce has already been consumed');
}

function reconcileAndCommit(journalFile, journal, contents, tombstoneFile) {
  tombstoneFile = revalidateTombstonePath(journal.nonce, tombstoneFile);
  if (existsSync(tombstoneFile) && journal.acknowledged) fail('approval nonce has already been consumed');
  if (hashFile(contents.commitMarkerPath) === contents.files.commitMarker.hash) {
    for (const [name, file] of Object.entries(contents.files)) {
      if (hashFile(file.final) !== file.hash) {
        fail(`repository commit marker exists but ${name} is missing or mismatched; preserve evidence and recover manually`);
      }
    }
    journal.recovered = journal.status !== 'committed';
    return finishLedger(journalFile, journal, tombstoneFile);
  }
  if (journal.status === 'committed') {
    fail('committed journal has no matching repository commit marker; preserve evidence and recover manually');
  }

  ensureDirectory(contents.stagingDir);
  const ordered = [
    ['plan', 'after-stage-plan'],
    ['log', 'after-stage-log'],
    ['state', 'after-stage-state'],
    ['activeMarker', 'after-stage-active-marker'],
    ['exclude', 'after-stage-exclude'],
    ['commitMarker', 'after-stage-commit-marker'],
  ];
  for (const [name, label] of ordered) {
    const file = contents.files[name];
    stageOrVerify(file.staged, file.content, file.hash, label);
  }
  for (const [name, label] of [
    ['plan', 'after-promote-plan'],
    ['log', 'after-promote-log'],
    ['state', 'after-promote-state'],
    ['activeMarker', 'after-promote-active-marker'],
  ]) {
    const file = contents.files[name];
    promoteManagedFile(file.staged, file.final, file.hash, file.priorHash, label);
  }
  const exclude = contents.files.exclude;
  promoteManagedFile(
    exclude.staged,
    exclude.final,
    exclude.hash,
    exclude.priorHash,
    'after-promote-exclude',
  );
  if (ensureDirectory(contents.critiquesDir)) crashAfter('after-create-critiques');
  const marker = contents.files.commitMarker;
  promoteManagedFile(
    marker.staged,
    marker.final,
    marker.hash,
    marker.priorHash,
    'after-commit-marker',
  );
  return finishLedger(journalFile, journal, tombstoneFile);
}

function repositoryKeyFor(repository) {
  return sha256(
    `${process.platform === 'win32' ? repository.workspaceRoot.toLowerCase() : repository.workspaceRoot}\n` +
    `${process.platform === 'win32' ? repository.gitCommonDir.toLowerCase() : repository.gitCommonDir}`,
  );
}

function pendingJournalFor(repository, repositoryKey) {
  const dataRoot = pluginDataDir();
  const dir = materializationJournalDir();
  assertContainedPath(dir, dataRoot, 'materialization journal directory');
  if (!existsSync(dir)) return null;
  const matches = [];
  for (const entry of readdirSync(dir)) {
    if (!entry.endsWith('.json')) continue;
    const path = resolve(dir, entry);
    assertContainedPath(path, dataRoot, 'recovered materialization journal path');
    const journal = readJson(path, 'materialization journal');
    if (journal.repositoryKey === repositoryKey && !journal.acknowledged) {
      if (journal.version !== JOURNAL_VERSION ||
          !TRANSACTION_ID_RE.test(journal.transactionId) ||
          typeof journal.nonce !== 'string' || !NONCE_RE.test(journal.nonce) ||
          entry !== `${journal.nonce}.json` ||
          journal.durable?.authorization?.envelope?.nonce !== journal.nonce ||
          !repositoryIdentityEquals(journal.repository, repository)) {
        fail(`pending repository journal is incompatible: ${path}`);
      }
      matches.push({ path, journal });
    }
  }
  if (matches.length > 1) fail('repository has multiple unacknowledged materialization journals');
  return matches[0] ?? null;
}

function assertContainedPath(path, root, label) {
  const absoluteRoot = resolve(root);
  const absolutePath = resolve(path);
  const lexical = relative(absoluteRoot, absolutePath);
  if (lexical === '..' || lexical.startsWith(`..${process.platform === 'win32' ? '\\' : '/'}`) ||
      isAbsolute(lexical)) {
    fail(`${label} escapes its allowed root`);
  }
  let existing = absolutePath;
  while (!existsSync(existing)) {
    const parent = dirname(existing);
    if (parent === existing) fail(`${label} has no existing containment anchor`);
    existing = parent;
  }
  const resolvedRoot = realpathSync(absoluteRoot);
  const resolvedExisting = realpathSync(existing);
  const physical = relative(resolvedRoot, resolvedExisting);
  if (physical === '..' ||
      physical.startsWith(`..${process.platform === 'win32' ? '\\' : '/'}`) ||
      isAbsolute(physical)) {
    fail(`${label} escapes its allowed root through a symlink or junction`);
  }
}

function assertContentPaths(contents, repository) {
  for (const [name, file] of Object.entries(contents.files)) {
    assertContainedPath(file.staged, repository.workspaceRoot, `${name} staged path`);
    const finalRoot = name === 'activeMarker' || name === 'exclude'
      ? repository.gitCommonDir
      : repository.workspaceRoot;
    assertContainedPath(file.final, finalRoot, `${name} destination`);
  }
  for (const [label, path] of [
    ['Forge directory', contents.forgeDir],
    ['critique directory', contents.critiquesDir],
    ['staging directory', contents.stagingDir],
    ['commit marker', contents.commitMarkerPath],
  ]) {
    assertContainedPath(path, repository.workspaceRoot, label);
  }
}

function assertBaseDestinations(repository, transactionId) {
  const forgeDir = join(repository.workspaceRoot, '.forge');
  for (const [label, path] of [
    ['PLAN.md destination', join(repository.workspaceRoot, 'PLAN.md')],
    ['review-log destination', join(repository.workspaceRoot, 'PLAN-REVIEW-LOG.md')],
    ['Forge directory', forgeDir],
    ['staging directory', join(forgeDir, `materialize-${transactionId}`)],
  ]) {
    assertContainedPath(path, repository.workspaceRoot, label);
  }
  assertContainedPath(
    join(repository.gitCommonDir, 'plan-forge-active.json'),
    repository.gitCommonDir,
    'active marker destination',
  );
  const excludeRaw = git(repository.workspaceRoot, ['rev-parse', '--git-path', 'info/exclude']);
  assertContainedPath(
    isAbsolute(excludeRaw) ? excludeRaw : join(repository.workspaceRoot, excludeRaw),
    repository.gitCommonDir,
    'managed exclude destination',
  );
}

function assertRecoveryPaths(journal, repository) {
  const forgeDir = join(repository.workspaceRoot, '.forge');
  const stagingDir = join(forgeDir, `materialize-${journal.transactionId}`);
  const excludeRaw = git(repository.workspaceRoot, ['rev-parse', '--git-path', 'info/exclude']);
  const excludePath = isAbsolute(excludeRaw) ? excludeRaw : join(repository.workspaceRoot, excludeRaw);
  const finals = {
    plan: join(repository.workspaceRoot, 'PLAN.md'),
    log: join(repository.workspaceRoot, 'PLAN-REVIEW-LOG.md'),
    state: join(forgeDir, 'state.json'),
    activeMarker: join(repository.gitCommonDir, 'plan-forge-active.json'),
    exclude: excludePath,
    commitMarker: join(forgeDir, 'materialization-commit.json'),
  };
  const stagedNames = {
    plan: 'PLAN.md',
    log: 'PLAN-REVIEW-LOG.md',
    state: 'state.json',
    activeMarker: 'plan-forge-active.json',
    exclude: 'exclude',
    commitMarker: 'materialization-commit.json',
  };
  for (const name of Object.keys(finals)) {
    if (journal.expected?.[name]?.final !== finals[name] ||
        journal.expected?.[name]?.staged !== join(stagingDir, stagedNames[name])) {
      fail(`pending journal contains an invalid recovery path (${name})`);
    }
  }
  if (journal.durable?.forgeDir !== forgeDir ||
      journal.durable?.critiquesDir !== join(forgeDir, 'critiques') ||
      journal.durable?.stagingDir !== stagingDir ||
      journal.durable?.commitMarkerPath !== finals.commitMarker) {
    fail('pending journal contains invalid durable recovery directories');
  }
}

function contentsFromJournal(journal, repository) {
  if (!journal.durable || typeof journal.durable !== 'object' ||
      !journal.expected || typeof journal.expected !== 'object') {
    fail('pending journal lacks durable recovery metadata');
  }
  assertRecoveryPaths(journal, repository);
  const files = {};
  for (const name of ['plan', 'log', 'state', 'activeMarker', 'exclude', 'commitMarker']) {
    const expected = journal.expected[name];
    const content = journal.durable.files?.[name];
    if (!expected || typeof content !== 'string' || sha256(content) !== expected.hash) {
      fail(`pending journal durable content is missing or corrupt (${name})`);
    }
    files[name] = { ...expected, content };
  }
  const contents = {
    forgeDir: journal.durable.forgeDir,
    critiquesDir: journal.durable.critiquesDir,
    stagingDir: journal.durable.stagingDir,
    commitMarkerPath: journal.durable.commitMarkerPath,
    files,
  };
  assertContentPaths(contents, repository);
  return contents;
}

function journalResult(journal, recovered) {
  return {
    action: 'forge-materialized',
    transactionId: journal.transactionId,
    phase: journal.durable.result.phase,
    recovered,
    planRevision: journal.planRevision,
    reviewer: journal.durable.result.reviewer,
    builder: journal.durable.result.builder,
  };
}

export async function materializeApprovedRun({ cwd, stateFactory, amendmentStateFactory }) {
  const repository = canonicalRepositoryIdentity(cwd);
  const lock = acquireRepositoryLock(repository.gitCommonDir);
  try {
    const repositoryKey = repositoryKeyFor(repository);
    const pending = pendingJournalFor(repository, repositoryKey);
    if (pending) {
      const contents = contentsFromJournal(pending.journal, repository);
      pending.journal.recovered = true;
      const recovered = reconcileAndCommit(
        pending.path,
        pending.journal,
        contents,
        tombstonePath(pending.journal.nonce),
      );
      return journalResult(pending.journal, recovered.recovered);
    }
    const capture = captureFor(repository.workspaceRoot);
    const authorization = authorizeNativeImplementation({
      transcriptPath: capture.transcriptPath,
      turnId: capture.turnId,
      sessionId: capture.sessionId,
    });
    if (!repositoryIdentityEquals(repository, authorization.envelope.repository)) {
      fail('approved envelope belongs to a different repository or worktree');
    }
    const transactionId = sha256(
      `${authorization.envelope.nonce}\n${authorization.envelope.humanPlanHash}\n${repositoryKey}\n` +
      `${authorization.envelope.origin.sessionId}\n${authorization.envelope.origin.itemId}`,
    ).slice(0, 32);
    const payloadHash = sha256(authorization.wrapper);
    const expectedIdentity = {
      transactionId,
      payloadHash,
      nonce: authorization.envelope.nonce,
      repository,
    };
    const journalFile = journalPath(authorization.envelope.nonce);
    const tombstoneFile = tombstonePath(authorization.envelope.nonce);
    let journal = existsSync(journalFile) ? readJson(journalFile, 'materialization journal') : null;
    if (journal) assertJournalIdentity(journal, expectedIdentity);
    if (existsSync(tombstoneFile) && (!journal || journal.acknowledged)) {
      fail('approval nonce has already been consumed');
    }
    const highestRevision = previousRevision(repositoryKey, authorization.envelope.nonce);
    if (authorization.envelope.planRevision <= highestRevision) {
      fail(`plan revision ${authorization.envelope.planRevision} is not newer than committed revision ${highestRevision}`);
    }

    const freshCatalog = await new ModelCatalogResolver({
      debug: new CodexDebugModelsProvider(),
    }).getCatalog();
    const observedApproval = {
      kind: `native-${authorization.kind}`,
      planHash: authorization.envelope.humanPlanHash,
      currentSessionId: capture.sessionId,
      currentTurnId: capture.turnId,
      originSessionId: authorization.envelope.origin.sessionId,
      originTurnId: authorization.envelope.origin.turnId,
      originItemId: authorization.envelope.origin.itemId,
      approvedAt: capture.observedAt,
    };
    assertBaseDestinations(repository, transactionId);
    const statePath = join(repository.workspaceRoot, '.forge', 'state.json');
    const activeMarkerPath = join(repository.gitCommonDir, 'plan-forge-active.json');
    const hasState = existsSync(statePath);
    const hasActiveMarker = existsSync(activeMarkerPath);
    if (hasState !== hasActiveMarker) {
      fail('active Forge state and repository marker are inconsistent; preserve evidence and recover manually');
    }
    const existingState = hasState ? readJson(statePath, 'active Forge state') : null;
    let state;
    if (existingState) {
      const currentRevision = Number(existingState.materialization?.planRevision) || 0;
      if (authorization.envelope.planRevision !== currentRevision + 1) {
        fail(`amendment revision must be exactly ${currentRevision + 1}`);
      }
      const existingPlanPath = join(repository.workspaceRoot, 'PLAN.md');
      const existingLogPath = join(repository.workspaceRoot, 'PLAN-REVIEW-LOG.md');
      if (hashFile(existingPlanPath) !== authorization.envelope.humanPlanHash ||
          !existsSync(existingLogPath) ||
          readFileSync(existingLogPath, 'utf8') !== authorization.envelope.reviewLog) {
        fail('amendment envelope does not match the reviewed durable plan and review log');
      }
      if (typeof amendmentStateFactory !== 'function') fail('amendment materialization is unavailable');
      state = amendmentStateFactory(
        existingState,
        authorization.envelope,
        freshCatalog,
        observedApproval,
        authorization.humanPlan,
      );
    } else {
      state = stateFactory(authorization.envelope, freshCatalog, observedApproval);
    }
    const createdAt = journal?.createdAt ?? new Date().toISOString();
    state.createdAt = existingState?.createdAt ?? createdAt;
    state.updatedAt = createdAt;
    state.modelCatalog = authorization.envelope.catalogObservation;
    state.modelResolutionTrace = [{
      provider: 'codex-debug-models',
      status: 'materialized',
      observedAt: authorization.envelope.catalogObservation.generatedAt,
      envelopeObservationHash: sha256(JSON.stringify(authorization.envelope.catalogObservation)),
    }];
    state.materialization = {
      version: 1,
      transactionId,
      nonce: authorization.envelope.nonce,
      origin: authorization.envelope.origin,
      planRevision: authorization.envelope.planRevision,
    };
    const contents = expectedContents({
      authorization,
      state,
      repository,
      transactionId,
      createdAt,
      journal,
    });
    assertContentPaths(contents, repository);
    assertOwnedPair(contents.files);
    if (!journal) {
      const resultMetadata = {
        phase: state.phase,
        reviewer: state.modelSelection.reviewer,
        builder: state.modelSelection.builder,
      };
      journal = {
        version: JOURNAL_VERSION,
        transactionId,
        payloadHash,
        nonce: authorization.envelope.nonce,
        status: 'pending',
        acknowledged: false,
        repository,
        repositoryKey,
        origin: authorization.envelope.origin,
        planRevision: authorization.envelope.planRevision,
        humanPlanHash: authorization.envelope.humanPlanHash,
        createdAt,
        expected: Object.fromEntries(
          Object.entries(contents.files).map(([name, file]) => [name, {
            staged: file.staged,
            final: file.final,
            hash: file.hash,
            ...(file.priorHash !== undefined ? { priorHash: file.priorHash } : {}),
          }]),
        ),
        durable: {
          authorization: {
            kind: authorization.kind,
            humanPlan: authorization.humanPlan,
            envelope: authorization.envelope,
            wrapper: authorization.wrapper,
            implementationApproval: observedApproval,
          },
          result: resultMetadata,
          forgeDir: contents.forgeDir,
          critiquesDir: contents.critiquesDir,
          stagingDir: contents.stagingDir,
          commitMarkerPath: contents.commitMarkerPath,
          files: Object.fromEntries(
            Object.entries(contents.files).map(([name, file]) => [name, file.content]),
          ),
        },
      };
      writeJsonAtomic(
        revalidateJournalPath(authorization.envelope.nonce, journalFile),
        journal,
      );
      crashAfter('after-journal-pending');
    } else {
      for (const [name, file] of Object.entries(contents.files)) {
        const expected = journal.expected?.[name];
        if (!expected || expected.staged !== file.staged || expected.final !== file.final ||
            expected.hash !== file.hash || expected.priorHash !== file.priorHash) {
          fail(`pending journal expected content does not match the authorized retry (${name})`);
        }
      }
    }
    const result = reconcileAndCommit(journalFile, journal, contents, tombstoneFile);
    return journalResult(journal, result.recovered);
  } finally {
    lock.release();
  }
}
