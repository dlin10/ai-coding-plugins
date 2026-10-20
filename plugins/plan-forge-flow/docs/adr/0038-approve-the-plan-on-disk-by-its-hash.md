---
status: accepted
---

# Approve the plan on disk, named by its hash

Revisits the alternative [0035](0035-patch-the-plan-against-a-snapshot-outline.md) rejected as out of
its scope: approval by hash instead of the whole plan. The approval contract of
[0003](0003-approval-through-the-orchestrator.md) — the Orchestrator asks, and `forge.plan.confirm`
records what it was told — is unchanged.

## Context

Since 0035 the Orchestrator holds the plan by reference. Run 5d B3c (20 October 2026) wrote its
plan once with `forge.plan.write` and revised it with thirteen `forge.plan.patch` calls; after the
first write it never had the whole text in its context again. `forge.plan.show` and
`forge.plan.confirm` were the two tools left that demanded the whole text back.

The run's context was compacted three times. A compaction drops the forge skill and the tool schemas
`ToolSearch` loaded, and the Orchestrator then calls tools from the summary it wrote itself. Its
summary listed `forge.plan.confirm` as taking `approved`, the decision batch, `fullGate` and
`gateEnvironment` — the table row of the skill, which never named `plan` — so `forge.plan.show` and
`forge.plan.confirm` went out without it, failed in the SDK's binding with an answer that said
nothing (the same release makes it say why), and succeeded only after the Orchestrator reloaded the schemas,
read the 76 KB `PLAN.md` and sent its 51,656 characters back verbatim.

Requiring the text was protecting something 0035 had already settled elsewhere. When `forge.plan.show`
was added ([0008](0008-render-the-plan-on-a-canvas.md)) and the plan first written before a round
([0009](0009-the-plan-is-visible-from-the-first-round.md)), `PLAN.md` held the last round's draft
while the Orchestrator could be holding a newer one, so the canvas and the approval had to carry the
text. Since [0014](0014-writing-the-plan-is-its-own-call.md) and 0035 every revision goes to
`PLAN.md` before anything else sees it, so the file is the plan the user is shown.

## Decision

- **`plan` is optional on both tools.** Omitted, `forge.plan.show` renders `PLAN.md` as it stands
  and `forge.plan.confirm` approves it, without rewriting the file. A missing or blank `PLAN.md` is
  refused, naming `forge.plan.write`. A plan passed keeps its old meaning: shown, or approved and
  written over `PLAN.md`.
- **`planHash` names what the user saw.** It is the hash `forge.plan.outline` reports — the same
  value `forge.plan.write` and `forge.plan.patch` answer with, and `forge.plan.show` now returns
  beside the plan. Given, `forge.plan.confirm` refuses a plan with any other hash before it applies a
  decision or writes anything, so a plan that moved after the user saw it is not approved in their
  name. It checks the text the call approves: `PLAN.md`, or the plan passed.
- **`planHash` is optional too.** The skill passes it; an older Orchestrator that passes the plan
  whole still works, and one that passes neither approves the file as it stands.

## Considered and rejected

- **Keep `plan` required and name it in the skill's table.** It fixes the summary that lost the
  argument, and keeps a 51 KB echo of a file the server already holds on every approval.
- **A required `planHash`.** It turns the next compacted summary that drops an argument into the
  same failure, for a check that only matters when something else wrote the plan between show and
  confirm — no Worker writes `PLAN.md`, and two Orchestrators on one run already see each other as
  stale bases on patch.
- **The plan hash in `forge.status`.** Status is read before approval anyway, but 0035 kept plan
  shape out of it, and the hash is already in every answer that changed the plan.

## Consequences

The schema no longer forces the Orchestrator to restate the plan, so an approval costs no tokens in
proportion to the plan's size. What the user approved is now asserted twice by the Orchestrator —
that it asked, and which hash it showed — and the server checks only the second. A Text-profile host
shows the user `PLAN.md` itself; the `planHash` it passes is from its last write, patch or outline.
