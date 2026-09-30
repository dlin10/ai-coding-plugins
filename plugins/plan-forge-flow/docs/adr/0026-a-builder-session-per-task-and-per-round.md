# A builder session per plan task and per code-review round

Narrows the Builder's session from the whole Run to one plan task or one code-review round, and
with it when [0019](0019-instruct-the-workers-through-the-act-prompt.md) delivers the Builder Brief
and the user's instructions. Issue #129.

## Context

The Builder ran as one session for the whole Run: every task and every review fix resumed it. On
2026-09-29 eight concurrency-hunter Runs were measured from their telemetry and Builder transcripts:

- **Claude (1M window).** Context grew by 100–300k tokens per task. Four of the seven Claude
  sessions that ran four or more turns reached 966–971k and auto-compacted, stalling about a minute.
  78% of the Builder's cost was cache reads of the accumulated history. Replayed as a fresh session
  per turn, re-orientation included, context tokens fell from 1,400M to 706M, cost from $404 to about
  $250, and compactions from four to none. Per-call latency barely depends on context size, so wall
  time was neutral.
- **The compaction drops the Brief.** A 31–41 KB Brief became part of an 18–20 KB summary with
  none of it verbatim. In one Run, tasks 7–8 and all three review-fix rounds ran with only a summary
  of the requirements, exclusions and gates.
- **Codex (258k window)** compacted every two tasks or so anyway, so a fresh session per task
  changed its tokens by about 6%.
- **Fix rounds** are the latest turns, where a run-long context is largest and most often past a
  compaction. A resumed claude fix turn started editing after a median of 5 calls, a fresh one after
  21. [0025](0025-fix-the-rule-not-the-place.md) asks a fix to find every place a rule decides before
  it edits, and its offline replay ran every builder as a fresh process.
- **Quality** could not be separated in the data: fresh sessions were almost all first tasks.

## Decision

A Builder session covers one **scope**: a plan task (`task N`) or a code-review round
(`code review round R`). The Run records the session's scope beside its token.

- A builder turn resumes the recorded session only when the same Vendor ran it for the same scope.
  So a task's first turn and a round's first fix start fresh, while a retry inside the scope
  resumes: a gate failure, a `blocked` answer, a cut-short or killed turn, and the round's later fix
  calls. A retry should remember the attempt it repeats.
- A fresh session is handed what 0019 hands one: the Builder Brief first and the user's instructions
  last. A fresh task session is also told which files each earlier task changed, by task number,
  from the files each completed task reported. The code on disk is the rest.
- One rule for every Vendor. Codex gains little on cost, but it gains the verbatim Brief and the
  current role contract at every task.
- `Acts/BuilderSession.cs` owns the rule: the scope keys, the resume test, what a turn records, and
  forgetting the session. The two acts that start a builder and the two places that reset one go
  through it.
- A state file written before scopes existed has no scope, so its next builder turn starts fresh.

Considered and rejected:

- **A fresh session per fix call.** A round is usually split into calls of two or three findings,
  and each would re-orient from nothing: about 16 more calls and 98k more tokens apiece.
- **A rule per Vendor.** Simpler to state as one, and the Brief argument holds for every Vendor.
- **Handing a fresh task a digest of earlier tasks' reports,** or their diff. The diff brings back
  the context this removes, and a report states what a builder believed, which the code on disk
  already answers.
- **Re-sending the Brief after a compaction** (#129's third part). It needs each Vendor's compaction
  signal. The largest growth inside one task in the measured Runs was about 460k, half of Claude's
  threshold, so it stays a safeguard for later.

## Consequences

A claude Run should cost about a third less, and every task and round starts with the verbatim
Brief. Each fresh turn spends extra calls re-reading code before its first edit: about 8 for a task
and 16 for a fix round. For fixes that is the point.

A mid-task change to the user's instructions reaches the next task or round rather than the rest of
the Run's turns; `forge.instructions.set` says so. `forge.status` shows `builderSessionScope` and
`taskChanges` with the rest of the run state.

Whether fresh sessions change the Builder's quality is not measured. The next Runs give a coarse
signal: first-round findings per middle task, against 2.07 before, and each round's share of fallout
from earlier fixes. A retry inside one scope can still outgrow a window and compact; nothing here
repairs that.
