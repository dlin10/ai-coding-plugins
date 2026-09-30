# Fix the rule, not the place

Extends [0019](0019-instruct-the-workers-through-the-act-prompt.md) with a per-attempt text for the
Builder, and [0022](0022-feed-critics-a-canonical-decision-ledger.md) with ledger entries that no
Critic raised.

## Context

On 2026-09-29 every code-review finding after the first round of nine concurrency-hunter runs was
classified by hand: 150 findings from phases 4 to 5d. 53% were fallout of the previous round's
fixes. In 42 the fix's own new rule was keyed on a stand-in for the property that mattered: a member
name, a syntax form, a static type, one control-flow block, a hand-written list. In 30 a place that
answers the same question was never updated: the cheap filter beside the solver, object creation
beside invocation, the `ref` path beside the direct one. Another 36 were older instances of a class
a critic had named once, each fixed at exactly the named place. No run's code review ended in
`approve`; each stopped at its cap, often after rounds granted past it.

Three causes were visible in the runs:

- The builder contract said "change the minimum needed" and "implement only the task or findings
  you were given", and builders followed it over the plan's own every-place rule. Fix summaries named
  sibling places and left them; six later findings were announced that way.
- Since 0022 the fix prompt is the ledger's `where — what` for each ID and nothing else. The
  orchestrator had no channel for what it knew about the rule behind the findings. A place the
  builder declared left had nowhere to go but its summary.
- `roslyn-contract.md` was appended to Critic and Scout only. Claude builders made no Roslyn call in
  38 fix turns, while codex critics made 63 reference searches.

Where a fix gave a question a single owner function, no later finding hit it.

An offline A/B replay tested the builder contract below: six historic fixes from four runs,
pre-fix trees rebuilt from builder transcripts, and blind raters. Recall of the places later rounds
found rose from 32% to 57% for claude opus-5-5 and from 23% to 46% for codex gpt-6-sol. That missed
the pre-registered bar, and codex made more new defects: 0.17 to 0.75 per turn, from rules made too
general. The precision clause in the contract answers that.

A critic "question pass" replayed the same way did no better than noise: 1 of 7 known siblings named
with it and without it. In 2 of 5 trees the replayed critic did not even rediscover the historic
finding, in either arm. Critic output varies more between runs than between contracts, so no critic
prompt changes here.

## Decision

Treat a finding as one instance of a rule, at every step that handles it:

1. **The builder fixes the rule.** `builder-contract.md` gains "When you fix review findings":
   - name the rule and key the fix on the property, not a stand-in;
   - list every place that answers the same question, by reference search;
   - fix them all in the turn, routed through one function;
   - test one input the finding did not name and one the rule must leave alone;
   - report `rule:` and each place as `fixed`, `already agreed` or `left: <reason>`.

   "The minimum needed" stays the rule for plan tasks.
2. **Every role gets Roslyn.** `PromptLibrary` appends `roslyn-contract.md` to the Builder as well.
   The contract is worded for any role, no longer for review alone.
3. **A fix attempt may carry a note.** `forge.review.fix` and `forge.work.start` take an optional
   `note`, and only with fix IDs.
   - It follows the verbatim findings under "From the orchestrator" and comes before the user's run
     instructions, which remain the last block.
   - It passes the secret guard, and the Flow log and run log record it verbatim.
   - It is not state: a retry sends whatever note it carries.
   - The findings stay the ledger's words; the note is the orchestrator's, in a section of its own.
4. **A code-review decision batch may carry raises.** Each raise has `severity`, `where`, `what`,
   `by` and `reason`.
   - It becomes an unresolved `code_review` entry under the next ID of the Run's one sequence, marked
     `raised` with who raised it and why.
   - Raises are part of the canonical bytes. An exact retry returns the same IDs, and a changed
     payload under the same key conflicts. A batch without raises serializes exactly as before, so
     digests saved earlier still match.
   - Plan review refuses raises. A batch with raises carries no fix attempt, because a raised finding
     has no ID until its batch applies.
   - Both fix paths return `raisedFindingIds` beside the result.
   - The next Critic assesses a raised entry like any unresolved one.
5. **The orchestrator holds the run to it.** In `SKILL.md`:
   - raise every `left:` place the plan does not exclude;
   - when half a round or more is fallout, ask for no further round; sweep the touched rules' axes
     against an oracle first;
   - hold fixes made on the host to the builder's rule.

   In plan review, `requirements-contract.md` makes a task that changes a rule several places decide
   name its owner, those places and its input axes, and makes "wherever X" carry the list of what
   reads X. The Impact pass asks for those places.

Considered and rejected:

- **A stronger builder.** The pattern appeared with claude opus/high, opus-5-5/high and codex
  gpt-6-sol/high. The vendors differ in the kind of miss, not in whether it happens.
- **A critic pass by question** in `scope-contract.md`. Replayed above, it had no effect larger
  than run-to-run variance.
- **Raise and fix in one call.** It would need IDs the caller cannot know yet, or a second identity
  scheme inside a batch.
- **The note as ledger state.** Free-form text is audit narrative (0022). A note that had to be
  byte-identical on retry would refuse a legitimate correction.
- **A schema version bump.** The new ledger fields are optional and absent when unused, so version-2
  ledgers written before this read unchanged.

## Consequences

Fixes grow. A finding may now touch every place its rule decides, and the builder spends reference
searches before its first edit. That is the trade intended: another round costs more than the extra
edits. The builder may make a rule too general, which is why its tests must pin where the rule stops.

The server records and does not enforce, as in
[0002](0002-mcp-server-surface-without-enforcement.md). Nothing checks that a note is accurate or that
a `left:` place was raised; the Flow log shows both.

The stop rule is the orchestrator's judgement. Whether all this works shows in the next runs: the
share of each round that is fallout, against today's 53%.
