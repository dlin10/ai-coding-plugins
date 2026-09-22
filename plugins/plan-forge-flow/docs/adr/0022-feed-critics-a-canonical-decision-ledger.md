# Feed fresh Critics a canonical decision ledger

## Context

Each review round starts a fresh Critic so it judges the current plan or review window without
defending its own earlier conclusions. Freshness still needs semantic memory: an earlier finding
that the user or Orchestrator settled must not return merely because the next process has no session
history. The transcript-era design supplied a complete append-only review log, repeating every
critique, deferral and Builder fix on every later round. Its prompt cost grew with the number of
rounds rather than with the decisions that still mattered.

`flow_log.md` already owns the complete user-facing audit trail. Issue #99 added per-attempt
`promptBytes` telemetry, making repeated Plan Forge-authored input observable without putting prompt
content into telemetry. The review loop already passes through the Orchestrator because it alone
holds the interview and approved-scope context; see
[0005](0005-code-review-through-the-orchestrator.md).

## Decision

Use the Run-local `decision-ledger.json` as the authoritative current state for findings that still
matter to a future Critic, while `flow_log.md` remains the complete audit and never becomes Worker
input. No review-log file is part of a ledger-era Run.

Plan Forge assigns every new Critic finding a monotonic Run-local identity from one sequence shared
by plan review and code review. The ledger stores only `unresolved`, `deferred` and `rejected`
entries. Deferred and rejected entries preserve the original finding verbatim, the asserted
decision-maker (`user` or `orchestrator`) and the reason. A successful plan revision or code fix
removes the entry, never reuses its identity, and records the closure in the Flow log. A defect that
later reappears is a new finding with a new identity.

Each entry has immutable `origin` and mutable `activePhase`. Plan review receives entries active in
`plan_review`. Code review receives settled plan decisions and entries active in `code_review`,
including a plan-origin entry whose accepted reopening changed only its active phase. Closed entries
and plan entries still unresolved in their original phase are absent from that projection. The
Critic assesses each displayed unresolved identity exactly once and may propose reopening only a
displayed deferred or rejected identity with evidence. The proposal changes nothing: only the
Orchestrator accepts or declines it, and the Builder never changes a disposition.

The Orchestrator sends typed, identity-keyed decisions through the MCP tool contract. Every
non-empty logical set has one `decisionBatchId`; validated decisions are canonically serialized in
identity order. An exact retry returns the saved result without another ledger or Flow mutation, and
a conflicting reuse returns the saved batch/result. A new key represents only a new legal delta.
Plan closures travel through the next review or final approved confirmation; code decisions and
host-verified closures travel through the fix act. Fix execution is independent: one
`fixAttemptId` binds one exact sorted ID set, retries retained work under the same key, and returns a
saved terminal result without another Builder or gate.

Free-form revision and summary text remains audit narrative, not state. The ledger is an indented,
versioned JSON snapshot containing the next identity number, entries in identity order, applied
decision batches and fix-attempt records. All replacements use `AtomicFile`. Malformed content, an
unsupported version, an invalid transition or an exhausted write retry fails the tool call;
authoritative decision state is not best effort like the separate telemetry file described by
[0020](0020-separate-worker-telemetry-from-the-run-log.md).

Pre-ledger Run folders are outside the supported scenario: there is no transcript migration,
fallback reader or compatibility error path. Verification uses deterministic prompt-composition and
UTF-8 byte-size tests rather than a live pre/post Vendor benchmark.

## Consequences

Critic input grows with unresolved and standing decisions rather than with every historical round.
Settled scope and requirement decisions cross from plan review into code review, while closed work
does not anchor a fresh Critic. The Flow log remains sufficient for a user or resumed Orchestrator to
reconstruct what happened, including identities, decisions, fixes, reopening proposals and
closures.

The MCP schemas and review orchestration become stricter: each displayed unresolved finding must
receive exactly one Critic assessment, final plan confirmation requires no unresolved active plan
entries, and ledger persistence failure stops progress. Review calls may apply partial legal
decision subsets. The design keeps no
machine-readable tombstone for closed findings beyond their Flow-log history, so a reintroduced
defect deliberately receives a new identity. Existing transcript-era Runs cannot continue after an
upgrade, and token savings are demonstrated deterministically rather than by paid integration
evidence.
