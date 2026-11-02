---
status: accepted
---

# Keep long reading out of the Orchestrator's context

## Context

The Orchestrator's context fills and compacts during a Run. Measured on 2 November 2026 from the
Orchestrator transcripts of the concurrency-hunter runs B4c `872ce5`, B4d `8236d6`, H1 `06e38c`,
H2a `609459` and H2b `363a38` (Claude Code, Opus 5.5, compacting at about 968k tokens) and of H2
`01bf54` (Codex). The growth of the context from one API call to the next is the previous call's
output plus everything appended in between, so each step's growth can be split by what was
appended:

- **The Claude Runs compacted 4, 1, 1, 1 and 0 times, and called the host's `Agent` tool 0 times.**
  The skill never mentioned subagents.
- **The Orchestrator's own output was 39–54% of the growth.** Opus keeps its earlier reasoning in
  context: on steps where nothing else was appended, the context grew by about 1.25 times the
  previous call's output tokens, and reasoning was 73–81% of that output.
- **Reading was 31–42% of the growth:** code read with Read, Grep, Bash and Roslyn, the
  Orchestrator's output on turns that only read, Scout answers, and Evidence-map flags. By phase it
  was 52% of the growth in planning, 40% in the interview and 21–29% in revision, build and code
  review.
- **Scout answers alone were 7–14% of the growth.** The Orchestrator read every Impact-pass answer
  once and whole, 40–100k characters each, three at a time when the pass was split, as the
  Evidence check requires. The answers of gpt-6.1-sol at high effort held 140–370 dependents each,
  one per item, mostly without saying whether the change reached them; the Advisor judged 48–62%
  of their characters to imply no action in the plan. H2b's Scout, Sonnet at xhigh under the same
  contract, grouped dependents and said which were unaffected, and its answers were two to three
  times smaller.
- **H2 on Codex spawned 28 subagents and still compacted 26 times.** Its own reads were 11.7M
  characters against 1.3M of agent messages: left to choose, it delegated small reads and kept the
  large ones. Five of the agents were of the `worker` type, which may edit.

Replaying the Claude Runs with code reading returned as reports of a tenth of its size, Scout
answers and flags at a fifth, and reading turns' output left out lowered the context read over a
Run by 22–34% and each segment's peak by 25–35%: about one compaction fewer, not none.

Two ways to shrink the Scout part were weighed: a subagent that digests a Scout answer and returns
the uncovered items, and a Scout that writes the same facts shorter. The digest was tried before
— [0021](0021-add-a-lazily-selected-scout-worker.md) as rewritten for 0.37.0 removed a bounded
digest because the facts the Orchestrator ignored sat outside it — and it would take the Evidence
check out of the Orchestrator's hands. The Scout contract already asks for every fact, so a shorter
answer has to come from its form, not from dropping facts.

The form was tried on 2 November 2026: the H1 and H2a Impact questions, each on a checkout of its
Run's base commit with text search only, answered once per contract by gpt-6.1-sol at high effort.
Scored against the dependents each Run's final `PLAN.md` names by member or test:

| Contract | H1 answer | H2a answer | Plan's dependents missed | Listed as unaffected |
|---|---|---|---|---|
| 0.75.0 | 120k | 98k | 12 of 37, 10 of 32 | — |
| verdicts, unaffected grouped | 70k | 70k | 13, 9 | 5, 6 |
| the same, and never unaffected when it asserts what changes | 102k | 81k | 12, 10 | 4, 8 |

The verdicts dropped no fact the plans needed, but labelled some of them unaffected — one a test
that pins the prohibition H1 lifts. The sentence of the third row moved 137 of H1's dependents to
`update:`, which halved the saving, and with one answer per contract did not measurably reduce the
mislabelling. The third row is what this decision ships.

## Decision

- **The Orchestrator hands long, read-only reading to a host subagent.** A new reference,
  `delegation.md`, read right after `forge.begin` and again after a compaction — the resume card
  names it beside the phase's references in every phase — names what goes:
  the class listing before a claim, test and gate logs, project documents during the interview,
  the code an Evidence-map flag names, and the Impact pass the Orchestrator makes when the Run has
  no Scout. It names what never goes: Scout answers and the Evidence check, the plan, decisions,
  questions to the user, forge calls, filtering findings, edits and judgements. The brief carries
  the workspace and its Roslyn server; the report is about 3,000 characters, every item with its
  `path:line` and that line quoted, and says whether the list is complete. Claude Code uses
  `Explore`; Codex uses an `explorer` with `fork_turns: "none"`, never a `worker`. A report is a
  pointer, as a Scout answer is: a claim still rests on lines the Orchestrator read itself.
- **The Scout says what the change does to each dependent, and lists the unaffected ones
  together.** Each dependent's text opens with `changes:`, `update:` or `unaffected:`. A dependent
  the change reaches keeps an item of its own, cited at it; the unaffected dependents of one
  location share one item, cited at that location, listing each by `path:line` and name with why
  they are unaffected; one the Scout cannot settle is not unaffected. A test or artefact that
  asserts the rule, value, order or wording a change point modifies is `update:`, and code that
  decides the same question as a changed rule is `changes:`, never `unaffected:`. Every fact is
  still reported, once and as briefly as it can be said.

Neither changes a tool, a schema or a Worker's role. This does not revisit
[0006](0006-worker-acts-as-jobs-on-the-cursor-host.md): the Workers stay separate Vendor processes,
and a subagent here only reads for the Orchestrator.

## Consequences

- What a subagent read and how is not in the Flow log, and its usage counts as the Orchestrator's.
- A subagent's report can be wrong as a Scout's can; the read-before-write rule of
  [0047](0047-read-before-writing-a-revision.md) is what keeps a wrong report out of the plan.
- Impact answers shrink by about a sixth, not by the half the grouping alone gave.
- A dependent the Scout calls unaffected and is not comes back as a Critic finding, as an omitted
  one did; it is still named in its list, so the Evidence check can see it. The Evidence map judges
  a grouped item once, as one item.
- The Orchestrator's reasoning, about half of the growth, is untouched, and so is the plan text
  `forge.plan.show` returns on the Canvas profile.
- The effect is measured by the next Runs: compactions, the share of reading in the growth, the
  size of Impact answers, and the plan-review findings that trace to a dependent the Scout listed
  as unaffected.
