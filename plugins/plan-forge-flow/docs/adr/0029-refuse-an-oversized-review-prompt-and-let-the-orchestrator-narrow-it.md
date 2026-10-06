---
status: accepted
---

# Refuse an oversized review prompt, and let the orchestrator narrow the window

Run `20261004-093111-b79938` sent its fourth code-review round, one the user had granted past the
cap, to a codex critic. The process exited 1 four seconds later: `turn/start` refused an input of
1,056,671 characters against a limit of 1,048,576. The review window ran from the run baseline to
a working tree holding about 564k characters of tracked diff — `docs/SPEC.md` alone 167k, since
its paragraphs are single long lines and a changed one appears twice — and about 480k of untracked
files embedded whole. Nothing was counted, but the round was reported as a vendor failure carrying
only codex's stderr, and the orchestrator worked around it by moving a gate script out of the
repository for the length of the review. The three earlier rounds had passed at sizes just under
the same limit.

## Decision

**The server measures the code-review prompt before any critic starts.** `IVendor` states the most
characters its CLI accepts as one turn's act prompt, counted as Unicode scalar values, or none. For
codex it is `MAX_USER_INPUT_TEXT_CHARS`, `1 << 20`, which codex-rs compares with the `chars()` of
the text input — the prompt read from stdin; `developer_instructions` and the output schema are
outside it. Claude and cursor-agent state no character limit (see `CONTEXT.md`). A prompt over the
limit stops the round with an error that gives the size, the limit, the overrun, where the
characters are — tracked diff, untracked files, approved plan, ledger projection, the rest — and
the ten largest files, and the flow log records the round as not sent. Nothing is spent: neither
the round count nor a user's grant. Only code review is measured, because only its prompt grows
with the run; plan review, fixes, builds and Scout send prompts two orders of magnitude smaller.

**The orchestrator narrows the round, by either of two per-call arguments,** on
`forge.review.code` and on `forge.work.start` for `review.code`:

- `excludePaths` — paths or git pathspec patterns, relative to `workspaceRoot`, that the round
  leaves out. They join the documentation exclusions in the one pathspec the changed paths and the
  content diff share, so the sensitive-path guard still covers exactly what is sent. The critic is
  told what was left out and that neither the changes nor their absence are its to judge, and the
  critique's window summary names them. They are for what is not the run's work — run tooling such
  as a plan's gate script, generated output such as an eval snapshot.
- `untrackedByReference` — the window's untracked files are listed with their line counts for the
  critic to read from the working tree with its own tools, instead of being embedded. The window is
  unchanged; only its delivery is. Their contents still pass the secret guard, because a critic that
  reads a file sends it to its vendor as surely as a prompt that carries it. Replayed over the run's
  tree on 2026-10-05, it took the prompt from 1,026,655 characters to 572,430 while the run's new
  code stayed under review.

The refusal names tracked files relative to `workspaceRoot`, as untracked files and pathspecs
already are, so a name it lists can be handed back as an exclusion. Git's diff headers carry the
workspace's path inside the repository, which the critic's prompt keeps as it was.

## Why per call, and why the orchestrator chooses

Both arguments narrow what the critic is handed, and what the critic is handed is a scope decision
the orchestrator makes with the user, as with every other scope question in the run
([0005](0005-code-review-through-the-orchestrator.md)). Narrowing automatically on overflow would
change what was reviewed without anyone deciding it. Neither argument is stored: a round that
should keep them asks again, and each round's flow entry says what it was given, so no later round
inherits a narrowing nobody restated.

## Considered and rejected

- **By reference for tracked files.** A tracked file's change is a diff against the base commit, not
  the file on disk. Reading it takes `git diff`, and a claude critic has no shell grant
  ([0017](0017-grant-worker-tools-by-exact-server-name.md), `CONTEXT.md`). An untracked file's whole
  content is its diff, which every critic can read.
- **Truncating the diff, or splitting a round across several critics.** A truncated diff hides
  findings without saying where; split rounds would each judge part of the work against the whole
  plan, and the ledger has no notion of a partial round.
- **Measuring every act.** No other act's prompt is near any limit, and a check for an impossible
  case is code nobody can test against a real failure.

## Consequences

The limit is a fact about a vendor CLI version, recorded in code beside the vendor and in
`CONTEXT.md` with its source; a codex release that changes it needs both changed. A critic given
files by reference may skim them, which is the trade the orchestrator makes when it asks; the
critic contract's reading passes still apply to them as material. The review window of
[0016](0016-review-the-final-tree-from-the-run-baseline.md) is unchanged when neither argument is
given, and a documentation-only or fully excluded window is still approved with nothing to review.
