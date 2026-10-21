---
status: accepted
---

# Calibrate severity, and continue past the cap while findings fall

Issue #172. Amends the review cap that `CONTEXT.md` explains under *A cap is a checkpoint, not a
stop*, where every round past it needed the user's grant, and the Critic contract's verdict rule.

## Context

Plan review had no end the Critic could reach. The contract said `approve` only "when nothing
remains that the implementer would have to guess at", and made every gap a finding; on an 85–110 KB
plan that is never. Findings per plan-review round on the concurrency-hunter runs of 12–20 October
2026, from `flow_log.md`:

| Run | Findings per round | End |
|---|---|---|
| `20261012-163837-6fc7b1` (A9) | 10, 9, 7, 6, 4, 4, 4 | approved after a granted round 7 |
| `20261014-181155-5c1f5a` (A10) | 12, 11, 8, 8, 4, 4, 4 | approved after a granted round 7 |
| `20261017-180043-675224` (B3) | 19, 11, 12, 12, 6, 6 | approved after a granted round 6 |
| `20261019-215643-7586e3` (B3b) | 24, 9, 7, 9, 5, 3 | approved after a granted round 6 |
| `20261020-123405-0d2a47` (B3c) | 11, 7, 5, 2, 2 | approved at the cap |

Thirty-one rounds, no `approve`, and nearly every tail finding a `major`. The 2026-10-06 study of
1,034 findings over 16 runs rated 371 Critic majors: 31% high value, 60% "the builder would guess
right"; 15 of 16 blockers were high. The cap question then stopped each run until the user answered
— 20–40 minutes, once 3.6 hours overnight — and a grant raised the cap by exactly one, so every
further round asked again, while the counts were still falling.

## Decision

**Severity is about the implementer, not about completeness.** `blocker` is unchanged. `major`
means that, following the material as written, the implementer the plan names would produce a wrong
result or leave a stated goal unmet. `minor` means the material says too little but that
implementer would still do the right thing, and covers wording, counts and gaps a gate already
catches; a weaker named model can turn such a gap back into a `major`. The verdict is `approve` when
no `blocker` or `major` remains among the round's new findings and the unresolved ones it assesses
as still present. Minors still go to the ledger and the Builder. The contract carries three rated
examples from each side of the line, drawn from the study's set.

**A round at the cap runs without the user while the review converges.** In `PlanReview` and
`CodeReview`, which share the cap code, a round at the cap without `userGrantedRound` still runs when
the last round's finding count is below the one before it and none of the last round's findings is
a `blocker`. Otherwise the tool refuses with the same exception as before, and the Orchestrator asks.

- *A round's count* is its new findings plus the unresolved findings it assessed as still present,
  and so is its blocker check. Counting only new findings would read an open finding the Critic
  stopped repeating as progress, and would let an open blocker through.
- *The window is the last two rounds.* The issue's worked examples fit one fall better than two
  consecutive falls; with two falls required, only A9 of the five runs would have continued at all.
- *No limit on Automatic rounds beyond the count itself.* A finding count is never negative and has
  to fall every round, so a loop that reaches the cap with N findings runs at most N Automatic
  rounds before one cannot fall and the user is asked. They are counted in
  `autoContinuedReviewRounds` and `autoContinuedCodeReviewRounds` beside the granted-round counters.
  An Automatic round does not raise the cap, so every round after it is decided again; a grant
  still raises it by one.
- Each round appends `{findings, blocker}` to `reviewTallies` or `codeReviewTallies` in the run
  state after its critique, as the grant is spent after its critique, so a round that died spends
  nothing. A run begun before tallies existed has none and asks at the cap as it did.
- The Flow log records `## <act> — round continued automatically` with `continued automatically:
  4 → 2` before the critique, and the round's result carries `continuedAutomatically: "4 → 2"`
  on `forge.plan.review`, `forge.review.code` and the `forge.work.start` path, so the Orchestrator
  announces it in one line. `forge.status` shows the counters and tallies with the rest of the
  run state; the resume card prints `+ N automatic` beside the granted rounds.

Against the table, with the count of new findings only, since the table holds no assessments: A9,
A10 and B3 would have run round 6 automatically and asked at round 7, B3b would have run rounds 6
and 7, and B3c would have asked at round 6 (2 → 2). A10's code review, 20 → 5 → 15 → 7 → 2 → 4 at a
cap of 3, would have asked at round 4, as the user wanted there.

## Considered and rejected

- **Two strictly falling rounds** (`c[n-2] > c[n-1] > c[n]`). Stricter, and it matches a three-count
  log line, but it would have bought one automatic round across the five runs.
- **New findings only.** Simpler to count, but blind to findings the Critic still finds and stops
  re-raising, which is exactly how a stuck loop can look like a falling one.
- **Raising the cap with each Automatic round.** It would make the next round free whatever its
  count, which is the runaway the cap is for.
- **A hard cap of three Automatic rounds**, as the issue proposed. The falling count already ends
  the chain, and a loop still converging after three rounds is the one the cap question would
  interrupt for nothing.

## Consequences

The Orchestrator still may defer or reject anything; severity stays the Critic's label and
disposition the Orchestrator's. A converging loop no longer waits for the user at the cap, and a
flat, rising or blocked one asks as before. The measurement the issue sets — majors at least 60%
high value with no rated-high finding demoted, and plan rounds to approval, cap questions asked and
Critic `approve` reached on live runs — is not part of this change.
