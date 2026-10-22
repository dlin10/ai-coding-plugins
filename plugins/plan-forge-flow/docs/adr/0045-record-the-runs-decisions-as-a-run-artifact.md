---
status: accepted
---

# Record the run's decisions as a Run artifact with identity

## Context

The decisions a run rests on — the user's answers in the interview, the scope answers given between
review rounds, the choices made at approval, mid-build and in code review — existed in one place a
participant could read: the Orchestrator's own conversation (issue #178). The design leans on that
conversation: the Orchestrator revises the plan because it "holds the interview context".

On the concurrency-hunter runs of 14–20 October 2026 the Orchestrator's host session was compacted
3 (A10), 1 (B3), 5 (B3b) and 6 (B3c) times, mostly during plan review. After each compaction the
interview was a 10–23 KB summary the Orchestrator wrote for itself; the B3c summary kept one decision
in one line out of 15 domain questions. What was written down went to `forge.log` as free-text
notes — 13 `Interview D-n (user):` notes in run B3 — which no Worker reads, and which a later Critic
reading only the plan could not see. A decision that settled a question outside any finding reached
the Critic only if the Orchestrator had written it into the plan text; otherwise the Critic raised it
again, and the Orchestrator rejected the finding `by: user` a round late. Every run asks 24–40
questions, 14–17 of them domain decisions, and already pairs each with a `forge.log.append`: the
record existed, in the wrong shape and the wrong file.

## Decision

From 0.61.0 a decision is a Run artifact with identity, as a finding is.

- **`forge.decision.record`** appends one record to `<runPath>/decisions.json` and a
  `## Decision D-nnnn` entry to the Flow log: `decisionId`, assigned by the server from `D-0001` on;
  `phase`, any Orchestrator phase but `done`, which opens none; `question` as asked; `options` as a
  widget offered them, at most one marked `recommended`; `answer` as given; `by`, `user` or
  `orchestrator`, as the ledger attributes; free-text `bearsOn`; and `supersedes`, the earlier record
  a changed answer replaces. Every text passes the secret guard before the call is logged, since the
  run log records the call's arguments in full. A record whose content — everything but its ID —
  equals one already there is that record: the call answers `no_op` with its ID, as a decision
  batch's retry answers with the saved result. Nothing is removed; a record already superseded
  cannot be superseded again, so each question has one current answer.
- **Who reads it.** `forge.status` returns the count and the last five as one line each,
  `D-0007 (user): question → answer`. The resume card names the file and the count, never the
  text, and shows the call's shape. Every result's `documents` carries `decisions` once the file
  exists. Every review Critic — plan and code alike — is shown the current records as a `# Decisions`
  section after its ledger projection, one line each of at most 600 characters, superseded answers
  left out; beyond 12,000 characters the oldest are left out and the section names them, because
  what the interview settled is the plan's requirements already, while what was settled between
  rounds may be nowhere else a Critic reads. The critic contract says a decision is not a gap and
  that material departing from one is. The Builder gets nothing new: what a task needs is written
  into the task.
- **The skill.** Every answer that settles something is recorded the moment it is given, and the
  record replaces the `forge.log.append` note rather than adding to it. The mechanical answers stay
  with the tools they go to, a gate value is never recorded, and an answer that only disposes of a
  finding stays in that finding's decision batch. Approval shows the user the count beside the
  plan, not the text.
- **A file that does not read back** — not an array, a gap in the numbering, an unknown phase, a
  supersession of no earlier record — stops every reader, as a malformed ledger does, and is left
  as it is.

The issue scoped the Critic section to plan review; the user extended it to code review, where a
decision taken mid-build is re-raised the same way.

Rejected: keeping decisions in `forge.log` with a stricter note format. No Worker reads that file,
and a format nothing checks drifts as the free text did. Rejected: putting decisions into the
decision ledger as entries. The ledger's entries are findings with dispositions, and its phase
projections and closure rules have no meaning for a question the user answered. Rejected: verifying
that a recorded question was asked. Nothing in the server can, as with `approved`
([0003](0003-approval-through-the-orchestrator.md)).

## Consequences

- The record is the Orchestrator's assertion. A decision it forgets to record is missing as before,
  and one it misstates is shown to every later Critic as stated.
- A Critic prompt grows by the Decisions section, at most 12,000 characters plus its framing, and
  its bytes change only when a record is added; a code-review round over its vendor's limit reports
  the section's share beside the plan's and the ledger's.
- Tool results that carry `documents` grow by the decisions entry once the first record exists, and
  `forge.status` by the count and five lines of at most 200 characters.
- A Writer worker, if one is built, has its first input.
- Measured on the next runs: records per run against the 14–17 domain questions; Critic findings
  rejected `by: user` as already decided; and whether the Orchestrator's first calls after a
  compaction cite the record instead of guessing.
