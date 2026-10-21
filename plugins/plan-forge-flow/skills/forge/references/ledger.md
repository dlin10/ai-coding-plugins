# Forge reference: the decision ledger

How findings are identified, decided, closed and fixed. Read it before the first decision batch of plan review, and again before code review.

## Decision ledger protocol

`decision-ledger.json` is the Run-local source of truth for Critic findings. IDs are monotonic across
plan and code review and are never reused. Each entry keeps an immutable `origin`, a mutable
`activePhase`, its verbatim finding, and one of `unresolved`, `deferred`, or `rejected`. Closing an
entry removes it; the Flow log keeps the finding, decision, reopening proposal, fix and closure as
the human-readable audit. Flow is never Worker input, and there is no `review-log.md`.

The phase projection is deliberate. Plan review receives entries whose `activePhase` is
`plan_review`. Code review receives settled plan decisions plus entries whose `activePhase` is
`code_review`. The Critic must assess each displayed unresolved ID exactly once, creates no copy of
an existing ID, and may only propose reopening a displayed deferred or rejected ID. You accept or
decline that proposal. Accepting a plan-origin proposal during code review preserves
`origin=plan_review`, changes `activePhase` to `code_review`, and makes the same ID unresolved;
declining changes no ledger state and remains visible in the Flow audit.

Every non-empty logical decision set gets one new `decisionBatchId` and a `decisions` array. Keep
that key and the exact byte-for-byte decisions for every retry of the same logical set. Each item is
identity-keyed and has `findingId`, `action`, asserted `by`, and non-empty `reason`:

- `defer` or `reject` settles an active unresolved ID;
- `addressedByRevision` closes an active plan ID after the revised plan is ready;
- `hostVerified` closes an active code ID and also requires non-empty `evidence`;
- `duplicateOf` closes the redundant active ID and also requires the canonical existing ID;
- `accept` or `decline` answers a displayed reopening proposal; `accept` also carries the proposal's
  concrete evidence.

A code-review batch may also carry `raises`: findings you put into the ledger yourself, each with
`severity` (`blocker`, `major` or `minor`), `where`, `what`, asserted `by` and a non-empty `reason`.
Send them in a decisions-only `forge.review.fix`, with `decisions: []` when nothing else is decided;
plan review refuses them. Each raise takes the next ID of the Run's one sequence and becomes an
unresolved `code_review` entry that the next Critic sees marked `raised` and has to assess. The
result lists the new IDs under `raisedFindingIds`, in the order you sent the raises; fix them in a
later call by those IDs.

`by` names who made the choice, not who sends it, and `user` is valid for every action. Write `user`
when the user's answer settled that particular finding or how it was addressed — they picked one of
the options you offered or dictated one — and `orchestrator` otherwise. Approving the plan as a whole
settles no finding, so it does not make the closures in the final batch the user's. The server
records `by` as you assert it and checks only that it is one of those two values.

Apply plan decisions through the next `forge.plan.review` or, for the final revised plan, through
`forge.plan.confirm(approved: true)`. Both accept the same complete decision shape, including
duplicate closures and reopening answers. Confirmation checks that no unresolved
`activePhase=plan_review` IDs remain; code-review IDs do not block it. A refusal uses
`approved: false` with no batch or decisions and leaves the ledger unchanged. Apply code decisions,
including duplicate and host-verified closures, through `forge.review.fix`; `forge.review.code`
never accepts decisions.

An identical `decisionBatchId` retry is a no-op and returns the saved result. A conflicting payload
under that key also returns the saved result: honour it, then use a new key only for a legal
delta that has not already been decided. If the Critic response is structurally or semantically
invalid, the round does not count and no findings are ingested, but decisions applied before the
call stay applied; retry the round with the same batch and exact decisions.

Fix execution is separate from decisions. Give each logical execution one `fixAttemptId` and the
exact sorted `fixFindingIds`; the Builder receives the ledger's verbatim findings for those IDs only.
Your `note` follows them in a section of its own, and the Flow log records it verbatim; what it
holds is under "Fix batches and their notes" in `code-review.md`.
A cut-short turn, failed gate, timeout, or retained finding retries the same attempt ID, exact ID
set and `gate` mode. A different set or mode under that attempt is refused. A completed attempt
returns its saved terminal result without starting the Builder or gate. Decisions-only `review.fix` omits both fix fields and
the note and gate.

Before deciding, compare every new Critic finding semantically with current IDs from the critique
and `forge.status`. Close a redundant new ID with `duplicateOf` and a reason; do not silently merge
IDs or ask the Critic to do it. Surface each critique, decision, reopening proposal, fix/gate result,
cut-short retry, retained finding and closure through the returned documents and concise chat
narration so the user can follow the Flow audit.
