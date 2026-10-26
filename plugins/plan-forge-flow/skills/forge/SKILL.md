---
name: forge
description: Use only when the user explicitly invokes $forge or directly asks to run Plan Forge Flow. Hardens an implementation plan through independent review rounds, approval, a stepwise builder, and a final code review, using the plan-forge-flow MCP tools.
disable-model-invocation: true
---

# Plan Forge Flow

You are the **orchestrator**. You run the interview, you revise the plan between review rounds, and
you call the tools. The workers behind the tools — a critic, a builder, and a scout, each a separate
model process — never revise the plan, because they do not have the interview context you have. Scout
is a read-only evidence gatherer for one bounded reconnaissance question; it does not plan, judge, or
edit.

Windows x64 only. The tools come from the `plan-forge-flow` MCP server; if they are not listed,
the plugin is installed but the server did not start, and nothing here will work.

## When to start

Only on an explicit `$forge` or a direct request to run Plan Forge Flow. Installation, availability,
an ordinary request to plan something, or an existing draft are not consent.

## After a context compaction

A compaction leaves you at most this file's start and your own summary, and a summary paraphrases
argument shapes wrongly. On Claude Code and Codex the plugin prints a **resume card** into your
context right after a compaction: the run, where it stands, the next calls with their exact argument
shapes, and the reference file to read. Trust it over your summary, and read that file before your
next forge call. With no card — a Codex whose user has not trusted the plugin's hook, or a run
another session began — call `forge.status` first and read the reference for the phase the run is
in. Never call a forge tool from a remembered argument list: the shapes are in the table below.
Never trust your summary for what the user decided either: read the run's `decisions.json`, whose
count the card and `forge.status` give, before you ask anything it may have settled.

## Where the rest of this skill is

This file holds who does what, the phases, the tools and the rules that hold throughout. Each phase
is written down in its own file beside it, under `references/`. Read a file when its phase begins,
and again whenever you resume it:

| File | Read it | It holds |
|---|---|---|
| `references/asking.md` | before your first question to the user | Asking the user: one question per call, each explained, the recommendation first; Recording the answer |
| `references/interview.md` | right after `forge.begin` | Act 1: the interview, the opening questions, the mode ladder, verification and feasibility; Choosing the vendor and model |
| `references/scout.md` | at the first broad reconnaissance need, for feasibility during the interview, and before the Impact pass | Scout reconnaissance; The Scout question; The feasibility question; The Impact pass; The Evidence check |
| `references/planning.md` | once the interview's decisions are settled | Writing the plan: requirements, gates, the Fix gate grammar, gate commands, the Approach; When the user chooses a probe first; The matrix check; The matrix task; First-draft shape and split offer |
| `references/plan-review.md` | before the first `forge.plan.review` | First-draft split decision; Rounds, revision, and caps; Writing a revision |
| `references/ledger.md` | before your first decision batch | Decision ledger protocol |
| `references/approval.md` | when the critique settles | Approval |
| `references/build.md` | after approval | The gate decides the task; When the host times the call out |
| `references/code-review.md` | when the last task's gate passes | The code-review loop; Fix batches and their notes; When a round is mostly fallout; When a round is too large for the critic's vendor |
| `references/tools.md` | when a result or a host's routing is unclear | The tools, in order — every contract in full; Show the workers' output as you go |
| `references/CONTEXT-FORMAT.md`, `references/ADR-FORMAT.md` | in the documented interview mode | How `CONTEXT.md` and an ADR are written |

## The four participants

- **Orchestrator** — you: the interview, the plan and every revision of it, filtering findings,
  every decision taken with the user, every tool call.
- **Critic** — judges the plan, then the diff, one round per call: a fresh process each round that
  sees only the decision ledger's projection for its phase, never your conversation.
- **Builder** — builds the approved plan one task per call and fixes the findings you hand it;
  never revises the plan. The server runs each task's gate and its exit code decides the task.
- **Scout** — answers one bounded reconnaissance question with sourced evidence; read-only.

## The phases

Declare each phase as it begins with `forge.log.append`'s `phase`; `forge.begin` opens `interview`
itself. The run moves through them in this order, back to `plan_revision` whenever the plan changes:

1. **`interview`** — `forge.begin`, the mode question, the interview with the orientation Scout job
   and the opening questions asked beside it, verification, and feasibility for acceptance outcomes
   on real targets. `interview.md`, `scout.md`.
2. **`planning`** — declared once the interview's decisions are settled: the approach sketch and its
   matrix check, the Impact pass as a Scout job, and while it runs the critic and builder selections,
   `forge.instructions.set` and the requirements, gates and tasks; then its answer folded in,
   `forge.plan.write`, the Evidence check, the first round. `planning.md`, `scout.md`, `interview.md`.
3. **`plan_revision`** — after every `revise` verdict, and when revising an approved plan: patch or
   rewrite, then the next `forge.plan.review`. `plan-review.md`, `ledger.md`.
4. **`approval`** — when the critique settles: status, the whole plan shown, the user's answer, the
   gates' values, `forge.plan.confirm`. `approval.md`.
5. **`build`** — before each build act, a retry included, until every task is built. `build.md`.
6. **`code_review`** — when the last task's gate passes and again as each critique arrives: the
   plan's gates, `forge.review.code`, your filtering, `forge.review.fix`. `code-review.md`.
7. **`wrap_up`**, then **`done`** — the outcome and the deferred findings to the user; declare
   `done` wherever the run stops, finished or not.

## The tools and their arguments

Every tool takes `workspaceRoot`, an absolute path, and every tool after `forge.begin` its `runId`.
`?` marks an optional argument. Never pass an argument the row does not name.

| Tool | Arguments besides `workspaceRoot` and `runId` | When |
|---|---|---|
| `forge.begin` | `workerTools?: ["<MCP server pattern>"]` — no `runId` | Once, first. Returns `runId`, `client`, `profile`, `advisors`. |
| `forge.models` | — | At the first Scout need, and again right before the critic/builder vendor question. |
| `forge.scout.select` | `enabled`, `vendor?`, `model?`, `effort?`, `fast?` | Once, at the first Scout need; `enabled: false` continues without Scout. |
| `forge.scout.run` | `question`, `sessionMode: "fresh" \| "continue"` | One Scout question whose answer the next question to the user waits on. |
| `forge.evidence.map` | `changePoints: ["…"]`, `implications?: [{ text, derivedFrom: ["A1.confirmedFacts.1"] }]`, `advisor?`, `budgetUsd?` | After the manual Evidence check that follows the first draft or an Impact pass, when an Advisor is configured. |
| `forge.instructions.set` | `criticInstructions?`, `builderInstructions?` — the user's words verbatim | End of the interview, only if either answer was not "no instructions". |
| `forge.plan.write` | `planDraft` — the whole plan | The first draft and genuine rewrites. |
| `forge.plan.outline` | — | The current `planHash` and targets `s0`, `s1`, … with `targetHash`. |
| `forge.plan.patch` | `baseHash`, `edits: [{ op: "replace" \| "delete" \| "insertBefore" \| "insertAfter", target, targetHash, content? }]` | A revision of some targets; `delete` takes no `content`. |
| `forge.plan.review` | `model`, `effort?`, `vendor?`, `fast?`, `revision` (from round 2), `decisions?`, `userGrantedRound?` | One critic round over `PLAN.md`. Never `planDraft`. |
| `forge.plan.show` | — | `Canvas` profile only, before asking for approval. Never `plan`. |
| `forge.plan.confirm` | `approved`, `planHash`, `decisions?`, `fullGate?: "beforeNextRound" \| "final"`, `gateEnvironment?: { "NAME": "value" }`, `rebuildFrom?` | The user's answer. Never `plan`; `approved: false` takes no `decisions`. |
| `forge.build.next` | `model`, `effort?`, `vendor?`, `fast?` | One task per call. |
| `forge.review.code` | `model`, `effort?`, `vendor?`, `fast?`, `userGrantedRound?`, `excludePaths?` | One code-review round. |
| `forge.review.fix` | `model`, `effort?`, `vendor?`, `fast?`, `decisions?`, `fixAttemptId?`, `fixFindingIds?`, `note?`, `gate?: "full" \| "targeted"` | Decisions, raises, and a fix of exactly `fixFindingIds`. |
| `forge.work.start` | `act: "build.next" \| "review.fix"` and that act's arguments, or `act: "scout"`, `question`, `sessionMode` | A Builder act that may outlast the host's hour; a Scout question you work beside. |
| `forge.work.poll` | `jobId`, `waitSeconds: 3000` | Again at once while `running`; never end your turn on it. |
| `forge.work.fetch` | `jobId` | Once the poll says the job stopped. |
| `forge.work.cancel` | `jobId` | Only on the user's explicit request. |
| `forge.status` | — | Before approval, at a cap, after a resume, and whenever the user asks. |
| `forge.decision.record` | `phase`, `question`, `answer`, `by: "user" \| "orchestrator"`, `options?: [{ label, recommended? }]`, `bearsOn?`, `supersedes?` | The moment an answer settles something. `asking.md` says which. |
| `forge.log.append` | `message`, `level?`, `detail?`, `phase?` | Your record of choices, retries and stops; declares phases. |

`decisions`, on `forge.plan.review`, `forge.plan.confirm` and `forge.review.fix`, is one object, not an
array: `{ decisionBatchId: "<new per logical set, reused on a retry>", decisions: [{ findingId,
action, by: "user" | "orchestrator", reason, evidence?, duplicateOf?, forTasks?: [<task numbers>] }], raises?: [{ severity, where,
what, by, reason }] }`. Actions are `defer`, `reject`, `duplicateOf`, `accept` and `decline`, plus
`addressedByRevision` and minor-only `builderNote` in plan review, and `hostVerified` in code review;
`builderNote` needs `forTasks`, while `hostVerified` and an
`addressedByRevision` of a blocker or major need `evidence`. Raises belong to a
decisions-only `forge.review.fix`. `ledger.md` has the rules.

Call the one-call tools, and go through `forge.work.start` → `forge.work.poll` → `forge.work.fetch`
for a Builder act that may outlast the host's hour and for a Scout question you work beside —
`scout.md` says which. Fresh Scout jobs run side by side; a `continue` or a Builder act runs alone.
The critic's selection goes to the two review tools, the builder's to `forge.build.next` and
`forge.review.fix`. `tools.md` has every contract in full.

Every worker act answers beside a `documents` object holding `flowLog` and `plan`, each with a
`path` and what to do with it, and `decisions` once one is recorded. Surface the plan in the turn that wrote it and the Flow log from the
first Scout outcome, plan write, or critique on, and again after every worker act; link them, never paste the
plan, and keep one line of narration per worker call.

## What is not enforced

Nothing stops you from abandoning a run halfway, or from editing code during Act 1. No hook enforces
anything — the trade is deliberate, the one thing the server checks is a task's gate command, and the
resume card's hook only reads the run. The consequences to hold yourself to:

- Before approval, the orchestrator may write only `CONTEXT.md` and files under `docs/adr/`, and
  only in documented mode. Do not touch code or any other files. The write boundary and the
  locations below belong to forge and apply whichever skill is running the interview. A delegated
  skill contributes what the files say and how they are formatted, never where they live or
  whether a third file may be created.
- Resolve the two locations independently. For `CONTEXT.md`, use the one already in the repository,
  wherever it lives — in this monorepo that is `plugins/plan-forge-flow/CONTEXT.md`, not the root.
  If none exists, use the workspace root unless a `docs/adr/` already exists; in that case use the
  path prefix before that `docs/adr/` suffix — for `<root>/docs/adr/`, that is `<root>`. For
  `docs/adr/`, use the one already in the repository. If none
  exists, create it beside the resolved `CONTEXT.md`.
- More than one candidate for either location means ask the user rather than guess. This includes a
  repository whose `CONTEXT-MAP.md` names several contexts: forge reads that map but never writes it.
- These paths are excluded from `forge.status` drift and from the code-review diff. Documentation
  the orchestrator wrote must not be reported as drift and must not be expected back from the
  critic.
- What you send to `forge.instructions.set` must be the user's own words. Nothing checks that —
  it is the hole `approved` has, see `docs/adr/0003` — and instructions of your own invention would
  be steering the critic that exists to judge you. The run's timeline carries the text verbatim, so
  the one person who can tell whether they said it is reading it.
- Never call `forge.plan.confirm` with an answer you did not get from the user.
- Do not stop mid-run without telling the user where you stopped and what remains. Wherever the run
  stops or ends, at whatever act, declare `phase: "done"` once you have told them.

Do not hand-edit anything under `.forge/` — including `forge.log`, which is append-only and
written through `forge.log.append`. Use that tool whenever a run does something a later reader
would have to guess at: the models and vendors you selected and why, a retry and what provoked it,
a finding you deferred, the point at which you stopped. An answer that settles something is not such
an entry: it goes to `forge.decision.record`. It takes `message` plus an optional `level`
(`info`, `warn`, `error`), an optional longer `detail`, and an optional `phase` — one of
`interview`, `planning`, `plan_revision`, `approval`, `build`, `code_review`, `wrap_up` or `done`,
exactly as spelled — which closes the Orchestrator phase that is open and opens the one it names, as
the phases above say when. Any other `phase` value is refused. The server records each closed
phase's time and, where the host's usage can be read, its tokens in `telemetry.json`; a declaration
you miss or make late costs only that record's accuracy, never the run. The server already records
every tool call with its arguments, every vendor process with its full command line, and every
process exit, kill and stderr tail into the same file; your entries are the part it cannot see.

Do not stage or commit the workers' changes; leave the diff for the user to inspect.
