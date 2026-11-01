---
status: accepted
---

# Carry a finding's invariant across rounds

Amends [0022](0022-feed-critics-a-canonical-decision-ledger.md) (the ledger keeps a register of
closed findings' invariants), [0025](0025-fix-the-rule-not-the-place.md) (the fix prompt carries a
finding's invariant and axes) and [0047](0047-read-before-writing-a-revision.md) (closure evidence
becomes the claim the next Critic verifies).

## Context

Five concurrency-hunter forge runs — B4c `872ce5`, B4d `8236d6`, H1 `06e38c`, H2 `01bf54` and H2a
`609459` — produced 312 Critic findings. The 263 raised after the first plan-review round were
classified under a protocol written before the classification began:

- **Most later findings break an invariant the run had already named.** 61% of them, and 66% of the
  blockers and majors, violate the same invariant as an earlier finding of the same run.
- **The fixes put them there.** Of those recurrences, 48% sit in text or code a fix or revision
  wrote (`fix-own`; 65% of the majors), 14% in a twin the fix left inconsistent (`fix-sibling`) and
  19% in new material — decisions, a reopening, a first implementation. Only 17% sit in a place that
  already existed when the invariant was first raised, which the Critic could have named then. 35%
  are on another layer than the earlier finding: a requirement against a gate, a task rule against
  its docs.
- **Phrasing the rule generally does not help on its own.** The earlier finding stated a rule
  narrowed to its instance in 45% of recurrences and the instance alone in 32%. A general phrasing
  did not lower the share a fix later broke: 53% against 58%.
- **Nothing checks the fixer's claim.** In 48% of recurrences, 69% of `fix-own`, the closure had
  claimed a class that covered the later place. The Critic's own member lists missed the later place
  in 55%, and no major ever said its list was partial.

The mechanics behind it:

- A closed entry leaves the ledger (`ApplyEntries`, `RecordFixAttempt`), so the next Critic never
  learns which invariants were fixed or what the fixer claimed.
- The finding schema has no field for the invariant; it lives, when at all, inside `what`.
- The Builder's fix prompt is `F-id | severity | where — what`, nothing more (0022, 0025).
- An `addressedByRevision` closure's evidence is validated and then dropped (0047).

Two earlier A/Bs — the 2026-09-30 Critic question pass (0025) and the 2026-10-07 second round-1
Critic — found prompt-only Critic changes swamped by the Critic's own sampling variance. So this
change is structural: it gives the invariant a field, a place to outlive closure and a route to the
Builder and to every later Critic.

## Decision

- **The finding carries its invariant.** The Critic schema's finding gains five fields, all
  required, as strict mode demands of every property:
  - `invariant` — the property violated, stated without the place and at the level of the plan's
    rule, so that it covers every place that must satisfy it;
  - `axes` — the layers and forms those places vary along: requirement, task rule, matrix, gate and
    docs for a plan; each decider or reader, tests, gate scripts and docs for code; call kinds, value
    channels, object kinds, one target or several for either. May be empty for a minor;
  - `searched` — how the Critic looked for the other places;
  - `complete` — true only when every axis was walked and the places listed are all there are. It
    replaces "say the list is partial" in the Critic contract;
  - `invariantOf` — the ID of an earlier finding whose invariant this one violates again, or empty.
    The schema constrains it to an empty string or `F-` and four digits.
- **The server keeps what came.** Ingestion stores the five on the ledger's finding, a blank string
  as none and an empty `axes` as none, and returns them in the critique. It refuses no critique for
  their content. One exception: an `invariantOf` naming an ID at or beyond the Run's next finding
  number is stored as none, and the round's Flow-log entry lists it under *Dropped on ingestion*.
  The ledger rejects a stored `invariantOf` that does not name an earlier finding, so following a
  chain always ends. Raises carry none of the fields.
- **The ledger keeps an invariant register.** `decision-ledger.json` gains `closedInvariants`: one
  record per finding that left `entries` by any closure and named an invariant. It is written in the
  same snapshot that removes the entry and keeps the finding's ID, origin phase, severity, `where`,
  invariant, axes, `invariantOf`, how it closed, and the claim:
  - the `evidence` of an `addressedByRevision` closure, which the ledger used to drop;
  - the `evidence` of a `hostVerified` closure;
  - for a fix attempt closed by its gate, the part of the Builder's `summary` that belongs to the
    finding: from the first line naming its ID up to the next line naming another ID of the same
    attempt, or the whole summary when no line names it. One attempt fixes several findings, and a
    shared summary cut to the claim's length would lose the later findings' axis reports. A blank
    summary leaves the gate output the closure carries as its evidence.

  Every claim is cut to 600 characters. A claim that trips the secret guard is not kept: the record
  holds a marker saying it was withheld, and the section prints that marker. The section is part of
  every later Critic prompt, and the guard over that prompt would otherwise refuse every round after
  it while blaming the diff. Revision and host evidence already pass the guard when they are sent;
  the Builder's summary did not. Every closure kind writes a record: a duplicate closure too,
  without a claim, and a fix attempt recorded as closed without the server's gate batch as
  `automatic_gate`, like the batch. No batch
  payload type and no digest changes: the claim reaches the closure through the normalised request,
  never through the canonical bytes.
- **Every later Critic is shown it.** After the projection, in plan review and code review alike,
  `# Invariants of earlier rounds` lists the register together with the displayed entries that name
  an invariant. Findings are grouped by root, following `invariantOf` to the first finding of each
  chain. One block per chain gives the root's ID, its phase, the highest severity among the chain's
  findings, the root's invariant, the union of the axes, every finding with how it stands (`closed:
  revision`, `unresolved`, …) and its `where`, and the latest claim — the one closed last, in the
  register's closing order, which need not be the highest ID. Blocker and major chains come first,
  then the chain last repeated first. Every line is flattened to one and cut to 600 characters. The
  whole section, heading and left-out list included, stays within 12,000 characters: a chain that
  does not fit is left out and the next one that does is still shown, so one oversized chain cannot
  crowd out the rest. The section names how many chains it left out and the first ten roots.
  Plan-review invariants reach code review, since the layer between them is one of the axes: the
  register is shown whole in both phases, while open findings come only from the entries that
  phase's projection displays. Each
  displayed entry also prints `invariant:`, `axes:` and `invariantOf:` lines, flattened to one line
  each, as the fix prompt does.
- **The Flow log marks a recurrence.** A critique's finding line ends with `(again: F-nnnn)` when it
  names an earlier finding, and the next line prints its invariant, so the per-round share of
  recurrences can be read from `flow_log.md`.
- **The fix is held to the invariant, and the Critic checks the claim.**
  - The fix prompt prints `invariant:` and `axes:` under each finding line.
  - `builder-contract.md`: a finding with an invariant is fixed along its axes. The check walks every
    listed axis, not only the reproduction, and the report says per finding ID which axes the check
    covers and which it left, and why. That report is the claim.
  - `plan-review.md` and `code-review.md`: the invariant and axes are the class to cover, closure
    evidence names the check across the axes and is shown to the next Critic, and `complete: false`
    makes the class query the Orchestrator's.
  - `critic-contract.md`: before its own passes, the Critic takes each invariant of earlier rounds
    along its axes across the whole material, every layer. It verifies the claim instead of taking
    it, and reports each violation with `invariantOf`. Reading its findings back now includes walking
    each of its own blockers' and majors' invariants along their axes.
  - `scope-contract.md` makes that the first code-review pass; `requirements-contract.md` makes it
    the first plan-review pass, across requirement, task rule, matrix, gate and docs.
- **The ledger schema stays at version 3.** Every new member is optional and left out of the file
  when null or empty, so a ledger written before this loads and serializes byte for byte as it did.

Considered and rejected:

- **A prompt-only Critic pass.** The two A/Bs above measured prompt-only Critic changes as noise
  against the Critic's sampling variance.
- **Refusing a critique for an empty or unknown invariant.** A refused critique costs a whole Critic
  round, and a round without invariants is still worth its findings. Only an `invariantOf` the Run
  never had is dropped, because a chain must end on a real finding.
- **Deriving 0049's fallout labels from `invariantOf`.** The Critic's own links are not yet measured
  against the Orchestrator's labels; once they are, the labels may follow from them.
- **Invariants on Orchestrator raises.** Out of scope: the Orchestrator writes a raise's reason
  itself, and nothing measured says the fields would change what the next Critic finds.

## Consequences

- Critic prompts grow in two places. The invariants section adds up to 12,000 characters once any
  finding shown or closed names an invariant. Every displayed entry with one adds its `invariant:`,
  `axes:` and `invariantOf:` lines on top, unbounded except by the number of open entries — the same
  growth the projection already has. The Critic also spends its first pass on earlier invariants
  before reading anything new.
- A finding is longer to write: the Critic names the invariant, its axes and how it searched.
- Fix turns grow along the axes they now have to walk, which is the trade 0025 already made.
- The claim is the fixer's assertion, as 0047's evidence is: the server keeps it and checks only
  that it is text and looks like no secret. The Critic is told to verify it, and nothing makes it.
- `invariantOf` is the Critic's link. A wrong link groups a finding under another chain; the finding
  itself is unaffected.
- The success measure for the next runs is the per-round share of findings with `invariantOf` set,
  read from the Flow log's `again:` marks, and the `fix-own` share of recurrences, against today's
  61% and 48%.
