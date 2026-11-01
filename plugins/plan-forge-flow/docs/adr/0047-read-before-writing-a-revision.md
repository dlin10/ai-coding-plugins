---
status: accepted
---

# Read before writing a revision, close a major with evidence, list a class whole

## Context

Concurrency Hunter Run `20261024-105628-872ce5` (phase 5d run B4c, 24 October 2026) took ten
plan-review rounds and never reached `approve`. After the Run, three raters went through its 44
findings, working blind to each other's results. Their sources were the Orchestrator's transcript,
the replayed plan of every round and the code at the Run's base. They found:

- **The Critic's statements held.** All 42 of its claims about the code or the plan that were
  checked were true. 41 of 44 findings could be acted on without redoing its analysis. Where the
  Orchestrator followed a finding's own remedy, 10 of 34 fixes drew a later finding; where it
  devised its own fix, 6 of 7 did.
- **The revisions' statements did not hold.** Of the 36 findings of rounds 2–10, 23 were fallout
  of an earlier fix. 16 of those later findings cited the code, and for 15 of them the cited lines
  had not been read when the fix they hit was written. Of the 9 claims a revision wrote without
  reading anything, 7 turned out false. The Run's first Roslyn query came in round 8. The revision
  after round 4 rewrote the whole plan four and a half minutes after the critique, without a read
  in between.
- **The six checks of *Writing a revision* in `plan-review.md` were followed in name only.**
  - *Gate with the rule* asked for a test, not for one that could fail.
  - *Evidence for claims* deferred to the Evidence check. That check weighs the plan against
    Scout's answers and passes a claim no answer holds.
  - *The class, not the instance* ran after the Critic had already named the next instance.
  - Five of the six mechanism questions put to the user carried a code claim that a later round
    falsified.
- **A closure by revision recorded nothing it rested on.** It carried a reason only, and the server
  refused evidence on it.
- **Two habits of the Critic made each answer larger.**
  - In 25 findings a rule and its check were one demand.
  - Classes were named with part of their members. F-0040 named four of the eight call sites of the
    second decider, and the provenance deciders surfaced one per round, in rounds 3, 9 and 10.

## Decision

- **The revision checks become acts done before the patch.**
  - *Gate with the rule* names, for each clause of a rule, the test cell where a wrong
    implementation answers differently.
  - *Read before you write* replaces *Evidence for claims*. Every claim a revision makes about the
    code is read in that revision, with Read or a Roslyn query. A Scout answer is a pointer, and a
    question to the user carries only claims that were read.
  - *The class, before the patch* runs `find_callers` or `find_references` on the deciding symbol
    before writing. It covers a finding's class and any rule the revision adds.
- **`addressedByRevision` takes `evidence`.**
  - It is accepted for every severity and required for a blocker or major: what the revision read,
    the class it listed and the failing cell.
  - The server refuses a batch in which such a closure lacks evidence, naming every such closure at
    once, and the Flow log prints the evidence beside the closure.
  - The decision shape already had the field and no persisted field changes. A batch without
    evidence keeps its canonical bytes and digest, and the ledger schema stays at version 3.
- **The critic contract asks for two things.**
  - The members a rule or decision leaves out are one finding against it. The finding lists every
    member found and how the Critic looked, or calls the list partial. Places that each need an edit
    of their own stay separate findings, as before.
  - A finding that needs both a rule and its check states them apart, the rule first.

*Amended 2026-11-01 by [0051](0051-carry-a-findings-invariant-across-rounds.md).* The evidence of an
`addressedByRevision` or `hostVerified` closure is no longer only audit: when the finding named an
invariant, the ledger keeps the evidence as the claim, and every later Critic is shown it beside the
invariant to verify. A partial list is now `complete: false` on the finding rather than a sentence in
`what`.

## Consequences

- A major's closure costs a line. A revision that has read nothing cannot close one without the
  Flow log showing it.
- Revisions spend reads and queries before the patch. In the Run above, most revision time went
  into writing and rewriting plan text instead.
- The evidence is the Orchestrator's assertion, as `by` is: the server checks that it is present,
  not that it is true.
- Critic findings that name a class grow longer.
- The effect on rounds and on the fallout share is not measured yet. The measures for the next Runs
  are the fallout share per round and how many later findings cite code the fix had read.
- The second Writer A/B ([#182](https://github.com/dlin10/ai-coding-plugins/issues/182)) compares a
  Writer against this Orchestrator, not the one measured in B4c.
