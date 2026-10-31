---
status: accepted
---

# Label plan-review fallout as an argument, and stop on it

Issue #232. Amends the automatic round of ADR 0041 and the revision rules of ADR 0047.

## Context

Code review has a stop: when half a round or more is fallout of the previous fixes, ask for no
further round and sweep. Plan review borrowed the labels (`own`, `sibling`, `same-class`, `older`)
and asked the Orchestrator to write one line per round with `forge.log.append`. Nothing required the
line, nothing read it, and no rule stopped the loop when the labels showed fallout. The cap did not
catch it either: a round at the cap runs unasked whenever the count falls and no blocker is left, and
the cap question carried rounds, cap and the last verdict, not the fallout or the plan's growth.

Run `20261028-115553-01bf54` (Concurrency Hunter 5d H2, plan-forge-flow 0.68.4):

- New findings per round, recounted from its `flow_log.md`: 10, 8, 6, 11, 7, 3, 4, 6, 12, 5, 9, 15,
  11, 9, 11, 3, 5, 5, 3, 8, 3 — 154 findings in 21 rounds.
- No label line in any of them. The user asked at the round-17 cap question why the Critic kept
  finding new problems after every round; the Orchestrator's audit (D-0068) then found 20 of the 27
  valid findings of rounds 14–17 to be fallout, `own` 9 and `sibling` 11.
- The loop ran against a cap of 5 on 14 grants and 2 automatic rounds.

## Decision

- **The labels become an argument.** From the third round on, `forge.plan.review` takes `fallout`:
  `{ own, sibling, sameClass, older }`, each a list of finding IDs. It names the new findings of the
  round before, every ID under exactly one label. The call is refused, with the IDs named, when a new
  finding has no label, a label names an ID that round did not raise, or an ID has two labels. A
  round whose predecessor raised nothing, round 1's findings (no revision precedes them), and a run
  begun before this decision owe no labels, and labels passed then are ignored.
- **The server keeps what it needs.** The IDs a round leaves to label are `falloutOwed` in
  `state.json`. They are state the server reads, not something the Orchestrator acts on, so
  `forge.status` leaves them out (ADR 0042) and the resume card prints them instead. After the
  critique, as the revision and a grant are, the server writes the Flow-log entry `Plan review —
  fallout of round N`, with the line the code-review skill asks for, and puts `new` and `fallout`
  (`own` plus `sibling`) into that round's `reviewTallies` entry. A call that dies writes none of it.
- **Half a round or more is the stop.** `plan-review.md` adopts the code-review rule: when half a
  round or more is fallout, or its findings walk one axis of a rule case by case, run no further
  round yet. Sweep the rules the revisions touched — their axes, every mention, one owner per rule —
  then spend one round to confirm.
- **Automatic rounds respect it.** A round at the cap runs unasked only when, besides ADR 0041's
  conditions, fewer than half of the last round's new findings are fallout. The labels arrive in the
  very call that is at the cap, so the check reads them there; a refusal writes nothing and quotes
  the share. A grant buys the round whatever the share is, as it does whatever the count is.
- **The cap question carries it.** It shows the fallout of the last rounds, the plan's growth since
  its first draft and the widenings since the interview, and offers the sweep and a split beside one
  more round.

## Considered and rejected

- **Refusing every round while half the last one is fallout.** The labels are the Orchestrator's
  assertion, like `approved` (ADR 0003), and a sweep is a revision the server cannot tell from any
  other. A refusal on an assertion would stall a run on the first honest disagreement, with no way
  to confirm the sweep. The cap is where the user is asked anyway, so that is where the share binds.
- **Working the labels out on the server.** Which revision wrote a rule is what the Critic and the
  Orchestrator know; the server holds neither the revisions nor their reasons.
- **A round number on ledger entries**, to find a round's new IDs without `falloutOwed`. It changes
  the ledger's schema for a list that is short and needed once.
- **The IDs in `reviewTallies`.** The tallies are in `forge.status`, and 150 IDs a run in a result
  the Orchestrator reads every time is noise.
- **Labelling code review the same way.** The evidence is of plan review. `ReviewCap.Continuation`
  takes the share as an optional argument and code review passes none.

## Consequences and limits

- The server checks that the labels cover the round exactly, not that they are true. Labelling every
  finding `older` ends the stop, and the Flow log shows the line to the user who reads it. The skill
  asks for the label by what the finding names, not by how it is worded.
- Dry check on H2. The Flow log holds no per-round labels, and the snapshot analysis of 31 October
  (about 86% of rounds 2–20 fix-induced, about 98% from round 9) is not in the run folder, so the
  round at which the stop would first have fired cannot be replayed here. The aggregate D-0068 does
  give is `own` 9, `sibling` 11 and 7 others of 27: 20 × 2 ≥ 27, so rounds 14–17 as a whole are over
  the line, before the round-17 cap question. If the analysis's 98% from round 9 holds, the automatic
  round before round 10 (21 → 12) would also have been refused.
- A run in flight across the upgrade owes labels from its next round on.
