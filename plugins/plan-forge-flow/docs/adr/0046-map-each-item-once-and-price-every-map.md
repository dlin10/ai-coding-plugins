---
status: accepted
---

# Map each item once, and price every map

## Context

[0036](0036-advise-the-evidence-check-through-a-configured-advisor.md) keys every judgment on all
its inputs, the shared state among them — the requirements, the Out-of-scope block and the Change
points — so "a change to any requirement re-asks the whole map", and the skill called the map
before every plan-review round. Concurrency Hunter Run `20261024-105628-872ce5` (phase 5d run B4c,
24 October 2026) shows what that costs:

- Jev was called for nine complete maps, each with `cacheHits` 0, because the Change points went
  from 16 to 34 and the requirements changed between rounds. At USD 0.042 per million input tokens
  that was USD 12.74, every map USD 0.86–1.59: about one request per item, each about 20k tokens,
  most of it the shared state. Items grew from 947 to 1,902 as Impact passes A5–A8 added answers,
  and every map asked them all again.
- By the Orchestrator's own log lines, the first map led to six plan edits; the other eight, USD
  11.88, to none. Across the five Runs that used Jev (7586e3, 0d2a47, 5a51cc, e08c4f, 872ce5), first
  maps raised something new in four of five; the 16 later maps did in two — B3b's second map with
  six edits, and one flag in e08c4f.
- The wire body escaped every non-ASCII character as `\uXXXX`, so `Measure` (UTF-8 bytes divided
  by three) saw a Cyrillic letter as two estimated tokens. B4c's final shared state measured
  98,394 bytes, 32,798 estimated tokens, past the 32,000 bound on state plus a question: from the
  third map on every pair with a task was withheld as `too_large`, and up to 1,607 of 1,902 items
  came back unchecked. Written as UTF-8 the same state is 49,498 bytes and every task fits.
- With the escaping fixed and nothing else changed, the last B4c map projects to USD 5.28 and the
  Run to about USD 24.

The alternatives were measured, not argued. Projections replay the nine B4c maps from the
reconstructed plans with the real candidate selection, packing and measure; on four real maps Jev's
reported input tokens were 1.03–1.05 times that measure, and B4c's first two maps replayed through
the tool after this change cost USD 0.82 and 0.29 — the second asking only the 281 items of the new
answer — against USD 0.86 and 1.51 before. Quality was measured by replaying two
recovered real maps (0d2a47, 5a51cc) through Jev, against the nine findings the Orchestrator had
taken from Jev in those Runs, and against Jev's own repeat of the same map.

| Option | B4c, USD | Quality |
|---|---|---|
| Today's keys, a map before every round | 23.93 | — |
| Today's keys, a map only after an Impact pass | 13.58 | unchanged |
| Pairs keyed on item and plan piece, state unchanged | 10.34–16.51 | unchanged for the pairs asked |
| Only items no complete map has judged | 2.57 | an item is not judged again against a revised plan |

Thinning the state was measured on its own: without requirements and exclusions a map costs 23–25%
less, but only 62–72% of item statuses match Jev's full-state map where Jev matches itself on
92–94%, 36–54% more items are flagged, and the nine findings are recalled seven or eight times out
of nine either way. Sending the plan piece once in state instead of in both questions saves 38–50%
and recalls five of nine.

## Decision

From 0.63.0:

- **The wire body is UTF-8 as written.** The systemone body and its measurement use relaxed JSON
  escaping, so the size bounds see the text the service decodes. Cache keys never depended on it.
- **A complete map judges each item once per Advisor and model.** A complete call adds its items'
  marks to those already in `evidence-map.json` and records who made them and how many Scout
  answers it read. A later call asks only about items without a mark — the items of new Scout
  answers and new implications — and reports the rest as `alreadyMapped`. Another Advisor or
  requested model, or a file written before the record existed, judges every item once more and
  replaces the marks. Content-addressed judgments remain, for resuming an incomplete call.
- **A new Scout answer makes a map due, not a round.** The skill maps after the first draft and after
  each later Impact pass. `evidenceMapWarning` names a Scout answer no complete map has read; a
  changed plan alone draws none.
- **Every map is priced and budgeted.** `IAdvisor` carries the Advisor's published input price.
  Before sending, the call prices what it packed; the result reports that estimate, the cost its
  reported tokens come to, the Run's spending read from `advisor.usage` records, and the Run's
  budget — 5 USD until the user names another as `budgetUsd`, which the Run keeps. A call whose
  estimate would take the Run past its budget sends nothing and answers `stoppedBy: "budget"`, and
  the Orchestrator asks the user.

Rejected: keying pairs on item and plan piece (B4c's tasks were rewritten every round, so 40–70% of
pairs were asked again), a thinner state (the status churn costs triage and buys no recall), and a
full second map before round 2 (about USD 1 more for B4c; the user chose the narrower rule).

## Consequences

An item the Advisor has judged is not judged again when the plan, the requirements or the Change
points change, nor when its source window changes; the manual Evidence check, mandatory after every
Impact pass, is what checks old evidence against a revised plan. To judge a plan from scratch,
delete `evidence-map.json`.

The estimate is the packed requests' measure; a request-wide refusal split into one request per
judgment is sent without a new estimate. A record written before `costUsd` existed counts its input
tokens at the current Advisor's price.
