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

## Asking the user

Every question you put to the user follows this section unless the user asks otherwise: the
interview, the Scout, critic and builder selections, the instruction questions, decisions on
findings, the round cap, narrowing a code-review round, approval, and the values a gate needs. It
belongs to forge and holds whichever skill is running the interview.

Ask one question per call of the host's question tool — `AskUserQuestion` in Claude Code — or per
message where the host has none, and wait for the answer before you ask the next. Work out the
frontier of the decision tree — every decision that could be asked now — but ask it one question at
a time: first the one the others depend on, and the next only once that one is answered, because an
answer can change or remove the questions behind it. This overrides the `grilling` rule to ask the
whole frontier in one round: a delegated interview skill supplies the questions, never their
batching. You make two batches unasked, one per worker role, both described under "Choosing the
vendor and model": the critic's vendor, model-and-effort, Fast and instruction questions share one
call, and the builder's another. Their options come from the catalogue you fetch before the first
of them, each naming what it depends on, and answers that do not fit together get one follow-up
question. Explain each question of a batch before the call. If the user asks for questions in a
batch, batch them as they asked.

Before each question, explain it in the chat:

1. **The problem it settles** — what the code or the plan does now, and what breaks or stays
   unclear if nobody decides. In plain words: an internal ID — a finding's `F-0012`, a round
   number, a class name — appears only beside what it stands for, and every term the user has not
   met yet gets a one-sentence definition.
2. **An example**, when the question is about how code behaves — a short fragment from the
   repository with its `path:line`, the path taken from the repository root, or a minimal
   illustrative one labelled as such — and what happens to it under each answer.
3. **The options** — for each, what it means, what it changes in the plan or the code, its
   advantages, its drawbacks and its cost; then the one you recommend, and why.

The question offers the same options. Keep their descriptions short and in agreement with the
explanation, put the recommended option first, and end its label with "(Recommended)". A mechanical
question — a vendor, a model and effort, the Fast tier, a value a gate needs — needs one or two
sentences of explanation and no code.

## The tools, in order

| Tool | When |
|---|---|
| `forge.begin` | Once, before anything else. Returns the `runId`, the connecting `client` and the capability `profile`, takes a baseline of the working tree, and starts every vendor's catalogue probe in the background. Its optional `workerTools` names the MCP servers every critic, builder, and scout may call without being asked; omit it and they get the Roslyn servers (`roslyn-*`). Pass it only when the task needs another server, and never name one that changes files — all three roles get the same grant. |
| `forge.models` | At the first Scout need and again immediately before the Critic/Builder Vendor question. Returns each vendor's model catalogue, newest first, with `available` and the reason when a vendor is not; successful entries are served from `CatalogCache`, while unavailable entries are probed again. |
| `forge.scout.select` | Once, just in time when broad reconnaissance first becomes necessary. Persists one exact Scout Vendor/model/effort selection or the explicit decline to continue without Scout; a later enabling call must also be explicit. |
| `forge.scout.run` | On non-Cursor hosts, runs one bounded Scout question directly. Always pass the required `sessionMode`: `continue` only for a direct follow-up in the same investigation, or `fresh` for an independent question, new subsystem, stale evidence, or deliberate reset. It returns the complete Scout answer under `scout`, unclipped, and appends it to `SCOUT.md`. |
| `forge.instructions.set` | Once, at the end of Act 1, when the user answered either instruction question with something. Records what they want the critic told and what they want the builder told, verbatim. Omit a role to leave it as it stands, pass `""` to clear it; skip the call entirely when both answers were "no instructions". |
| `forge.plan.write` | Once per round, before the round, with the current draft. Writes it to `PLAN.md`, runs no worker, and answers in seconds with the path under `documents`. Surface that path, then run the round. |
| `forge.plan.review` | On non-Cursor hosts, once per round, after `forge.plan.write` and with `planDraft` omitted. Apply the plan decisions from the previous critique in `decisions` before the new Critic runs. **You** revise the plan and say in `revision` what changed — required from the second round on. |
| `forge.plan.show` | On a `Canvas` profile only, once the critique settles. Renders the plan as a document with the drift beside it, and records nothing. |
| `forge.plan.confirm` | When the critique settles and you have shown the user the plan and asked them. With `approved: true`, it accepts the same complete plan-decision shape as `forge.plan.review`, applies final closures, and refuses while any active plan finding is unresolved. With `approved: false`, send no `decisions`. |
| `forge.build.next` | On non-Cursor hosts, once per task, repeatedly, until `tasksCompleted` equals `taskCount`. After the builder's turn the server runs the task's gate command itself; a `gate_failed` result is the same task again on the next call. |
| `forge.review.code` | On non-Cursor hosts, once per round after the last task. Returns one critique. **You** then filter the findings and call `forge.review.fix`. |
| `forge.review.fix` | On non-Cursor hosts, applies code decisions and optionally runs the Builder for exactly `fixFindingIds` under one `fixAttemptId`. A decisions-only call starts no Builder or gate; it is also where `raises` go, and it answers with their `raisedFindingIds`. The Builder receives the ledger's verbatim findings for those IDs only, followed by your `note`. |
| `forge.status` | Before asking for approval, after a resumed run, and any time the user asks where things stand. Carries a compact ledger summary with current IDs, dispositions and active phases, the drift, job liveness, and `run.scout` with enabled/selection, current session, and last failure. |
| `forge.work.start` | On Cursor, starts one worker act, including `scout`. `plan.review` and `review.fix` take the same decisions and retry IDs as their direct tools, and `review.fix` the same `note`; invalid ledger IDs, phases, states, batch conflicts or fix-attempt sets are rejected before a job is created. If `started` is false, rejoin the returned active `jobId`. For Scout, pass only `question` and explicit `sessionMode`. |
| `forge.work.poll` | On Cursor, waits up to 45 seconds for the started job and reports its latest stdout activity and recognised event. A `running` result means call it again immediately; it is not narration-worthy and never ends your turn. |
| `forge.work.cancel` | On Cursor, requests cancellation of one job without waiting for it to stop. Use only when the user explicitly asks, or after showing its liveness and obtaining confirmation; then poll and fetch it normally. A terminal job is a successful no-op. |
| `forge.work.fetch` | On Cursor, fetches the terminal worker result after polling. |

Every tool takes `workspaceRoot` and, after `forge.begin`, `runId`. On a Cursor client, every worker
act — including Scout — goes through `forge.work.start` → `forge.work.poll` → `forge.work.fetch`;
do not call a one-call worker tool there. On non-Cursor hosts, call `forge.scout.run` directly
for Scout, and use the one-call worker tools — `forge.plan.review`, `forge.build.next`,
`forge.review.code`, `forge.review.fix` — for the other acts. Those legacy tools take `model`, an
optional `effort`, an optional `fast`, and an optional `vendor`: `claude`, `codex`, or `cursor`,
defaulting to `claude`. `forge.work.start` takes the same `fast` for every act but Scout.
The critic's selection goes to the two review tools, the builder's to `forge.build.next` and
`forge.review.fix`, and Scout always reuses its persisted selection. Direct and Cursor-background
forms have the same decision contract; a decisions-only `review.fix` has no `fixFindingIds` or
`fixAttemptId` and starts no Builder.
If `forge.work.start` returns `started: false`, rejoin its active job with poll → fetch.

Every worker act answers with its own result beside a `documents` object — the critique under
`critique`, the build under `build`, the fix under `fix`, and the complete Scout answer under `scout`.
On Cursor the act's own payload is the `result` string of `forge.work.fetch`; Scout's result string
is the answer JSON only. `forge.plan.write` answers with `documents` and nothing else, and on Cursor
`forge.work.start` and every `forge.work.poll` carry it too. `documents` always holds `flowLog` and
`plan`, each with a `path` and what to do with it, and each `null` until its file exists. Only a
successful direct Scout call or a successful completed Scout fetch also carries `documents.scout`,
whose instruction is exactly: “show the Scout report to the user now, and show it again after each
later successful Scout call appends its answer.” Non-Scout results omit it even after a report
exists. What to do with the documents is below.

Worker calls run for minutes, and the host's clock on a tool call is not yours to extend. On Cursor,
use start → poll → fetch for every act, including Scout, so the surviving server can rejoin a detached
worker; on every other host, call Scout directly and use the one-call tools for the other acts. One
`forge.work.poll` waits 45 seconds, so an act that takes minutes needs many of them in a row: keep
calling it, in the same turn, until the state is no longer `running`, and only then fetch. Each poll
result says which call it wants next. A `running` poll is not a result, not narration-worthy, and never
a reason to end your turn — never stop on one to ask the user to continue, because there is nothing
for them to answer. If the originating server process exits, an in-flight job id is unknown to a new
server and cannot be rejoined; after restart, start a new act. Persisted terminal results remain under
`.forge/<runId>/`.

`lastActivityAt` is the last stdout line, including output the parser did not recognise;
`lastEvent` is a short description of the last recognised vendor event. Use them to explain what a
running job is doing, never to invent an automatic cancellation policy: every vendor attempt already
stops after 30 minutes with no stdout. Call `forge.work.cancel` only on the user's explicit request,
or show both fields and obtain confirmation first. Cancellation is nonblocking, so continue with
poll → fetch until the failed terminal result is persisted.

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
holds is under "Fix batches and their notes".
A cut-short turn, failed gate, timeout, or retained finding retries the same attempt ID and exact ID
set. A different set under that attempt is refused. A completed attempt returns its saved terminal
result without starting the Builder or gate. Decisions-only `review.fix` omits both fix fields and
the note.

Before deciding, compare every new Critic finding semantically with current IDs from the critique
and `forge.status`. Close a redundant new ID with `duplicateOf` and a reason; do not silently merge
IDs or ask the Critic to do it. Surface each critique, decision, reopening proposal, fix/gate result,
cut-short retry, retained finding and closure through the returned documents and concise chat
narration so the user can follow the Flow audit.

## Scout reconnaissance

Use Scout for broad repository reconnaissance before the Orchestrator can settle requirements or
write a plan: multi-module exploration, caller/writer discovery, dependency tracing, or greenfield
orientation. These are the broad triggers. Keep one or two targeted semantic lookups in the
Orchestrator: they remain cheaper and clearer there. Use the local Roslyn or repository tools when
the question has a narrow file, symbol, or caller boundary. Do not turn every local lookup into a
Scout call. Scout has a second use point, the Impact pass below, which every plan gets.

Scout is lazy and independent of the separate Critic and Builder selections. On the first broad need,
make one just-in-time Scout selection round: call `forge.models`, offer up to three valid live/resolved
Vendor/model/effort combinations, clearly mark exactly one **recommended** combination, and include
the choice **continue without Scout**. Recommend the strongest combination the catalogue offers at high effort — judged by its position in the newest-first list, not by a remembered name — because the same selection serves the Impact pass, an exhaustive enumeration at which a weaker model invents test names. Do not silently select a Vendor or silently continue a session.
Before the question, say in a sentence or two what Scout is for and what continuing without it
costs: the Impact pass then falls to you.
A persisted decline suppresses later automatic Scout questions for this Run; enabling it later requires
an explicit `forge.scout.select` call. Scout selection is independent of and may happen before or
after final Critic/Builder selection; if the first need occurs before it, this Scout catalogue call
is still just-in-time and separate from the final refresh below.
When exactly one valid Vendor is available, omit the Scout Vendor question and use that Vendor for
the offered combination. On a Cursor host, when `cursor` is available, omit the Scout Vendor
question as well and offer the resolved Cursor choices directly. Offer the Fast tier for Scout by
the same rule as for the other roles below, as its own yes/no after the combination is chosen, and
pass the answer as `fast` to `forge.scout.select`; it stays with the selection.

For every direct or background Scout call, pass `sessionMode` explicitly. Use `continue` only for a
direct follow-up in the same investigation. Use `fresh` for an independent question, a new
subsystem, stale evidence, or a deliberate reset. After a failure, retry with the saved selection;
reselect explicitly to clear the old session and failure; or explicitly choose continue without
Scout. Never silently choose session continuity and never fall back to another Vendor. Scout may
use already available internet without per-call authorization or a widened grant. Require typed
repository citations (path plus positive line or non-empty symbol) and external citations (an
absolute HTTP(S) URL). Surface `SCOUT.md` only after each successful Scout call.
Do not copy Scout's report into Critic or Builder prompts automatically; only derived conclusions
the Orchestrator deliberately puts into a plan or task may reach those Workers.

### The Scout question

Write every Scout question — orientation or impact — with these parts:

- **Goal** — what is going to change and why, in one or two sentences.
- **Workspace and tooling** — which Roslyn MCP server and port serves this code, the absolute path
  to use with it, and that its reference and caller queries answer "who uses". A Scout left to
  guess falls back to text search.
- **Numbered, narrow sub-questions.**
- **Search scope** — named beyond `src/`: the test projects, `build/`, `skills/`, `docs/`,
  `prompts/`, and whatever else in the repository pins behaviour.

A question may run to 8,000 characters. When an Impact pass's Change points do not fit, split it
by area into several questions.

### The Impact pass

After you have drafted the plan's approach and made its matrix check, and before the first
`forge.plan.write`, ask the Scout one Impact pass. Every plan gets one. It is a Scout need, so it
starts the Scout selection round when none has been made. Name every Change point the approach
introduces — symbols, contracts, wire and persisted formats, counters, prompts and skill text — and
ask, for each one:

- every consumer, and what its behaviour becomes;
- every test that asserts today's behaviour, with file:line and test method name;
- every artefact outside the code that pins current values or wording: snapshots, metrics and
  baselines, gate scripts, docs, specs, skills and prompts;
- for a rule — a predicate, a classification, an identity, a mapping — every other place that
  decides the same question, and whether it agrees today.

```text
Impact check for a drafted approach. Do not redesign or judge it; find everything it breaks or must also touch.

Planned change:
1) <change point, named by symbol or contract>
2) ...

For each of 1–N, list:
a. Every consumer of what changes and what its behaviour becomes.
b. Every test that asserts today's behaviour and would fail or need updating (file:line and test method name).
c. Every artefact outside the code that pins current values or definitions: recorded snapshots, metrics or baseline files, gate scripts, docs, spec or skill text.
d. For a change point that is a rule (a predicate, classification, identity or mapping): every other place that decides the same question, and whether it agrees today.

Search <the scope, beyond src/>.
```

Run the first Impact pass with `sessionMode: fresh`, so the orientation framing cannot anchor the
enumeration. Its result carries the complete answer: fold it into the plan — tasks for affected
consumers, tests to update, artefacts to re-record — before the plan is written. When a revision
introduces a Change point no Impact pass has checked, ask again with `sessionMode: continue` about
the new Change points only, before that revision is written. When the Run continues without Scout,
make the same check yourself with reference and caller queries and text search, and record what
you searched with `forge.log.append`.

### The Evidence check

After each Impact pass, and before the review that follows it, check the plan against every fact in
`SCOUT.md` — every answer of the Run, not only the last — that bears on a Change point. Each such
fact is either used by the plan or departed from on purpose. Record a deliberate departure with
`forge.log.append`, and state it in the plan as well — in the exclusions or the task text — wherever
a Critic would otherwise raise it: the Critic never reads the Run log.

## Act 1: the interview

Call `forge.begin` first, before asking an interview question or invoking an interview skill. Then
ask exactly one mode question, unless the user already said which mode they want when they invoked
`$forge`. Offer exactly these two modes every time:

- interview without documentation;
- interview that maintains the domain model as it goes.

Keep the skill-availability chain out of that question. Each mode resolves down a two-step ladder,
taking the first step that is available:

| | without documentation | with documentation |
|---|---|---|
| 1 | `grilling` | `grilling` **and** `domain-modeling` |
| 2 | the interview paragraph below | the built-in documented rules below, plus the two references |

A step is available only when **every** skill it names is in the host's catalogue. A name absent
from the catalogue is absent for you; never guess that it is available. But absent for you is not
uninstalled: hosts keep slash-only skills out of the model-facing catalogue, so a skill you cannot
see may be sitting right under the user's `/`. Tell the user which skill is running the interview,
and never claim that a skill is missing or not installed.

If the host publishes no catalogue at all, each skill may be attempted once: make one attempt per
name, not one attempt per run, and treat any error as absence. When a catalogue exists, do not
attempt a name that it does not contain.

For the documented mode's composite step, attempt `grilling` first. If it errors, the composite
step is absent. If `grilling` succeeds and `domain-modeling` errors, the interview is already
running: continue it under the built-in documented rules and use the
[`CONTEXT-FORMAT.md`](references/CONTEXT-FORMAT.md) and [`ADR-FORMAT.md`](references/ADR-FORMAT.md)
references; do not restart it.

The built-in documented rules are to challenge terms against `CONTEXT.md`, sharpen vague language,
test the model with concrete scenarios, update `CONTEXT.md` as terms resolve, and offer an ADR only
when the decision is hard to reverse, surprising without context, and a real trade-off. Create
documentation lazily and follow the [`CONTEXT-FORMAT.md`](references/CONTEXT-FORMAT.md) and
[`ADR-FORMAT.md`](references/ADR-FORMAT.md) references. If the selected interview skill is absent,
use the built-in rules here for documented mode and the interview paragraph below for the mode
without documentation.

Ask grilling questions **one at a time**, each explained as "Asking the user" describes, and wait
for each answer, whichever skill is running the interview. You are looking for the decisions the
plan would otherwise leave to whoever implements it: what is out of scope, what happens on the
error paths, what existing behaviour must not change, how the result will be verified.

When the interview has settled the decisions, write the requirements first — the `## Requirements`
section described below. They are the interview's own output and do not depend on who implements
them, which is why they come before the vendor and model questions.

Then choose the vendors and models — the whole of the "Choosing the vendor and model" section
below — before writing the tasks. The builder's selection is an input to them: the plan's depth is
calibrated to the model and effort that will execute it, so tasks written before that choice are
written blind.

Write the plan as markdown, in three parts: the requirements, the gates, and the tasks.

The tasks live under a heading spelled exactly `## Approach`. That heading is not a suggestion:
`PlanTasks` refuses a plan without exactly one of it, and `forge.plan.confirm` parses before it
writes anything, so the wrong heading fails at approval rather than later. Anything above
`## Approach` is not walked as tasks; on a fresh builder session it is sent verbatim as the
Vendor-bound Builder Brief. Keep transient interview notes, rejected alternatives, secrets, and
host values out of it. The section ends at the next `##` heading, so put the tasks last or expect
everything after that heading to be dropped.

If a Builder Brief is rejected as sensitive, remove the sensitive content from the plan and directly
re-approve the changed plan before retrying the Builder act.

Inside it, number the tasks `1.` to `N.` in order, one task per numbered item — a gap or a repeat is
refused outright. That numbering is what `forge.build.next` walks, so a task that is really three
tasks will be built as one.

Above it, `## Requirements` states what must be true when the work is done: one numbered requirement
per item, `R1` to `Rn`, and then what the run deliberately does not do. Requirements are the
interview's answers, not the implementation — what must become true, what must not change, how it
would be observed — so no file names, no symbols, no "how". The exclusions carry as much weight as
the requirements: they are what stops the critic demanding work the user already ruled out.

Every task ends with a **Gate** — the command that would show that task done — and cites the
requirements it serves. A check that belongs to no single task goes under `## Gates` instead,
numbered `G1` to `Gn`, each citing what it discharges: the test suite, a warnings-clean build, an
invariant spanning the whole change. Leave that section out when the task gates already cover
everything; a ceremonial gate is worse than none.

**The server runs the gates, so write them to be run.** After every `forge.build.next` the server
executes the task's gate on the host — from `workspaceRoot`, in PowerShell, with the
`gateEnvironment` you pass at approval — and its exit code, not the builder's report, decides whether
the task counts. After every `forge.review.fix` it does the same with the `## Gates` entries. A gate
is executable only when the code comes **first** after the label: `**Gate:** `dotnet test …` …`.
Prose before the backticks makes the gate a condition — the server records it as `not executable`
and the builder's self-report is all you have.

**Only the server runs executable task gates**, including on retries. The act prompt communicates
the existing parser's classification to the Builder. Do not change ownership based on a command's
cost, move a task gate to `## Gates`, or require an existing plan to be revised and re-approved.
The Builder checks condition gates itself; on `failed` or `unavailable`, you check them.
Separate targeted Builder checks are optional, not a required build-and-test pass. The Builder
must not reproduce its executable gate under another command, wrapper, or sequence, even when
the gate is a single targeted test. Plan-wide gates follow the separate schedule below.

Write gate commands as follows:

- One PowerShell command line, placed immediately after `**Gate:**` (or `**G1.**`). Chain with
  `;` — a native command exiting non-zero ends the script with that code — and make a condition
  fail explicitly: `if (…) { exit 1 }`. Several commands may go in a fenced block right after the
  label, one per line; it runs as one script and stops at the first failing line.
- Reference the environment as `$env:NAME` and name every variable the gate needs; you will be
  asked for them at approval. Never write a value into the plan.
- A task that adds tests must prove they **exist**, not that the suite is green: a green suite at
  the old count is exactly what a builder that wrote no tests reports. Filter to the new class and
  count the names —
  `if ((dotnet test src/X.slnx --list-tests --filter "FullyQualifiedName~FooTests" | Select-String "FooTests\.").Count -lt 14) { exit 1 }; dotnet test src/X.slnx --filter "FullyQualifiedName~FooTests"`
  — with the count the task demands. Then say what to name them, so the count is checkable.
- A gate that needs something outside the workspace — a sibling checkout, a database — needs the
  path or the connection string in `gateEnvironment`.
- Codex Builders run without Codex's sandbox or approval prompts, including resumed retries.
  Plan their required edits directly, including top-level `.git`, `.codex`, and `.agents` when
  those edits belong to the approved scope. Access depends on the launching process's permissions.
  `builderRoots` is accepted for compatibility and does not control access. Codex Critic and Scout
  retain their read-only sandbox.

```markdown
Builder: cursor / gpt-5.3-codex / high

## Requirements

1. **R1.** What must be true once the work is done.
2. **R2.** What must not change.

**Out of scope.** What this run deliberately does not do.

## Gates

1. **G1.** `dotnet test src/X.slnx --nologo` passes. (R1, R2)

## Approach

1. **First task.** What to change. **Gate:** `dotnet test src/X.slnx --filter "FullyQualifiedName~FooTests"` (R1)
2. **Second task.** … **Gate:** `if ($env:CD_TEST_SQL_CONN) { dotnet test … } else { exit 1 }` (R2)
```

Write every task as its change-specific delta. The Brief supplies stable context once on a fresh
builder session, while every task must still stand alone with its own task gate and requirement
references. Each task starts a fresh builder session: the Brief, the list of files earlier tasks
changed, then `# Task N of M` and the task's own text. Only a retry of the same task resumes that
session, so nothing a task needs may live only in an earlier task's conversation.

A task that introduces or changes a rule more than one place decides — a predicate, a
classification, an identity, a mapping — names the rule's owner, the one function every such place
routes through, lists those places, and names the axes of the rule's input it covers: value
channels, call kinds, object kinds, one target or several. Write "wherever X" or "every Y" only
beside the list of what reads X, or narrow it explicitly. The Impact pass is where the list comes
from; a rule given one owner stayed fixed in the measured runs, and a rule added at the one place a
finding named drew findings along each of its axes for rounds.

Scale the plan's depth inversely to the builder you selected. A strong model at high effort takes
goal-level tasks. The cheaper the model or the lower the effort, the smaller and more explicit each
task must be: name the files and the symbols, decide the edge cases and the error paths yourself,
and make each task's `Gate` count what the task must produce rather than only run what already
exists — leave nothing to the builder's judgement, because the builder you chose has less of it. Judge strength from the
vendor's own catalogue — the position in its newest-first list and the chosen effort — not from a
remembered model name.

State the builder's selection at the top of the plan, above `## Requirements`, in one line — vendor,
model, effort, and `Fast` when the user chose it. The critic judges the plan's depth against the
builder named there.

The draft is not ready for its first `forge.plan.write` until the matrix check below, the Impact
pass and the Evidence check described under "Scout reconnaissance" have run against it.

### The matrix check

While you draft the `## Approach` — before the Impact pass and before the first `forge.plan.write`
— decide yourself whether the plan needs a matrix task: a task whose test walks every combination
of a rule's axes and checks each against an expectation written from the requirements, not from
the code. List every rule the plan introduces or changes — a predicate, a classification, a mapping,
the choice of a name or a form, the order in which reasons are given — and, for each, the axes its
answer depends on: the kind of value × the action × the execution it happens in, say. A rule needs
a matrix when any one of these holds:

- its answer depends on a combination of two or more axes, with about six or more values across
  them;
- the plan lists its cases one by one, and there are more than about ten;
- a neighbouring rule of the same kind already has a matrix in the codebase;
- a missed cell is a silent fault: an unsafe narrowing (an answer narrower than the truth where
  only a wider one is safe), a wrong refusal, a security gap.

A rule that needs one gets its matrix task in the first draft. Left to review, it comes back one
combination at a time: in one measured plan review the rounds ran 28, 17, 15, 11, 12, 14 and 9
findings, and from the third round on nearly every finding was an uncovered combination of one rule
or an edge where it contradicted another. The matrix arrived after the fifth round, and the next
critic found an error in its expectation table — caught in the plan, before any code. In an earlier
pass, a matrix of 1,729 probes did the work of the review rounds. Make the same check for a rule a
later revision introduces.

In the turn of the first `forge.plan.write`, beside the plan's path, say in one line which rules
you checked, what you decided for each and why: `Matrix check: Classify — value kind × action ×
execution, 11 values: matrix, task 4. ReasonOrder — one axis, 4 values: none.` When the plan
introduces or changes no rule, the line says so.

### The matrix task

The critic judges a matrix task with the rest of the plan, so the plan spells it out:

- It is a task of its own, with its own test class in the ordinary suite — never behind an
  environment flag.
- It lists every axis and every value of each, and every excluded combination — what the test's
  `Allowed` filter drops — with the reason it is left out.
- It writes the expectation table into the plan from the requirements — by axis values where the
  cells are too many to list — each answer citing its R-item. In the code, every branch of
  `Expected(cell)` cites a requirement, never the code under test.
- It lists the cells known to be wider: each by name, with the reason its expectation cannot be
  reached without a change this plan does not make, such as one to the engine the rule runs on.
  Such a cell must come out wider than its expectation, never exact, and a name on the list that
  matches no cell fails the test. For a rule whose answers have no safe side, the list is empty and
  every cell must be exact.
- Its tests assert that no cell narrows unsafely, that every cell off the wider list gets its
  expected answer, and that every value of every axis occurs in some cell.
- Every reason on the wider list becomes an open question in the project's own catalogue or design
  document, and the plan names the task that writes it.
- It replaces the one-by-one cases of the same rule in other tasks rather than repeating them. Only
  cases outside its axes stay listed by hand.

## Rounds, revision, and caps

Each non-Cursor `forge.plan.review` call, or each Cursor start → poll → fetch round, runs exactly
one round and returns a verdict of `approve` or `revise` plus findings. On `revise`, address the
findings in the plan yourself — *Writing a revision* below says how — and run the next round. The
Critic is a fresh process each round and receives only the current plan-phase ledger projection, so
it converges on current findings without being anchored by the transcript.

A revision that introduces a Change point no Impact pass has checked gets its own Impact pass and Evidence check before it is written.

From the second round on, sort each critique's new findings by the rule they are about. When more
than half of them are combinations or edge cases of one rule and the plan has no matrix task for
it, add one in this revision — do not wait for the cap — and say so in `revision`. Each of those
findings is then addressed by its cell in the table, not by a case of its own.

Every round after the first carries your answer to the one before it. The tool refuses the call
without `revision`:

- `revision` — what you changed in the plan, in a sentence or a short list, in the findings' own
  terms. It goes to the flow log and nowhere else, so the user can see your turn between the
  critic's; no worker ever reads it. Say so plainly when a round changed nothing and you are
  re-running for another reason.
- `decisions` — the typed identity-keyed changes for this logical decision set. Use `defer` or
  `reject` only with a reason and asserted decision-maker; use `addressedByRevision` only after the
  revised plan actually addresses that ID. Partial subsets are valid during review. The final
  `forge.plan.confirm(approved: true)` is the postcondition that requires every active plan ID to
  be settled or closed.

The optional `deferred` narrative remains Flow-only compatibility text. It never settles an ID and
never reaches the Critic; authoritative deferrals and rejections are typed `decisions`.

The critic judges the requirements too, and those findings are not all yours to fix. One the
interview already settles — two requirements you wrote that contradict each other, a condition
stated too vaguely to check — you revise and carry on without stopping. One it does not — a
requirement covering a question nobody asked, or an answer that would move the scope — goes to the
user before you revise, and its answer goes into `forge.log.append`. Ask the moment it comes up
rather than saving it for approval: a scope question answered late invalidates every round that ran
after it. When one critique brings several, ask them one at a time, the one the others depend on
first.

Review rounds are capped, and so is the code-review loop. When a cap is reached the tool refuses.
Before asking, call `forge.status` and show the user how many rounds have run, what the cap is, and
what the last verdict said, so the question carries its numbers. Say in plain words what the open
findings are, and what one more round would cost against stopping here. On a yes, pass
`userGrantedRound: true` on the next round tool — `forge.plan.review` or `forge.review.code`, or the
same argument on `forge.work.start` — which raises the cap by exactly one. The grant is spent by
that round, so the round after it needs a fresh answer, and never pass the argument without having
asked.

Link the drafts, do not paste them. Each round is `forge.plan.write` with the current draft, then
the round itself with `planDraft` omitted — the write puts the plan at `<runPath>/PLAN.md` in
seconds and hands you the path, and the round then reads it from there instead of carrying it a
second time. Surface that path in the same turn as the write, before the round starts, so the user
reads the plan during the minutes the critic takes rather than after them; refresh it after each
round. What stays out of the chat is the plan's *text*: five revisions pasted into the conversation
bury the one version that matters, which is why the file exists.

Rewriting an already-approved plan takes the approval back: whichever call changes the file —
`forge.plan.write`, or a round you handed a draft to — clears `approved`, resets the build progress
to zero, and records it in the flow log. That is not a silent housekeeping detail — say it in the
chat, because the next `forge.build.next` will refuse until the user approves again, and the builder
will then start from the first task. If tasks were already built, tell the user how many are about
to be rebuilt before you write the new draft.

### Writing a revision

Measured over thirteen runs, about half of the plan-review findings after round 1 sat in text a
revision had written, rising to two thirds from round 6 on: a rule that arrived without its gate, a
change made in one place and left standing in another, a claim about the code nobody had checked, a
mechanism invented to answer one finding. Before each `forge.plan.write` that answers a critique,
walk every change the revision makes through these checks; the draft is ready when every change
passes all six:

- **Gate with the rule.** A rule, requirement or step the revision adds or changes arrives with its
  gate rows, its tests and the task steps that build it, in the same revision.
- **Every mention.** A term, rule, count, name or decision the revision changes is changed wherever
  the plan states it — requirements, tasks, gates, test lists, counts — found by searching the draft.
- **Evidence for claims.** Every claim the revision makes about the code — what exists today, a
  name, a count, a behaviour — passes the Evidence check before it is written.
- **The class, not the instance.** A finding that names one member of a class — one reader of a
  value, one call form, one case of a rule — is answered for the whole class: list its members, by
  references and callers where the class is code, and cover each one, or narrow the requirement in
  words. Name the list in `revision`.
- **Mechanisms are the user's.** When the answer to a finding is a new mechanism — a pass, a state,
  a wrapper, a restart, a fixpoint — ask the user before writing it, with the simpler alternatives
  beside it.
- **The findings' scope.** The revision changes what the findings name and what the checks above
  reach. A neighbouring case enters the plan when a critic raises it.

From the second round on, label every new finding with the labels of *When a round is mostly
fallout*, reading *revision* for *fix* — `own`, `sibling`, `same-class`, `older` — and record one
line per round with `forge.log.append`:
`Plan review round 3: own F-0021; sibling F-0024; same-class F-0019; older F-0022`.

## Show the workers' output as you go

Every tool result lands in your context and nowhere else — the user sees none of it unless you
surface it. The server keeps the user-facing timeline for you: every worker call appends its
outcome to `<runPath>/flow_log.md` — Scout outcomes, critiques with verdict and findings, your own
revision and typed decisions between plan-review rounds, build results with status and files changed,
reopening proposals, fix attempts, gate results and closures. Nothing feeds this file back to a
Worker; it is the audit to show, while the bounded ledger projection is Critic input.

The plan is the second such file. `forge.plan.write` puts the draft at `<runPath>/PLAN.md` before
the round that judges it starts, and every later round rewrites it, so the user reads the current
plan while the round runs rather than meeting it once at approval.

Both travel as `documents.flowLog` and `documents.plan`, each with its `path` and what to do with
it, on every result that has something to show — `forge.plan.write`, every worker act, and on
Cursor the start and every poll as well. The Scout-only `documents.scout` report metadata appears
only after a successful direct Scout call or a successfully completed Scout job fetch. Either
ordinary document can be `null` while its file does not exist yet. **Surface the plan in the same
turn as the write that created it, and the flow log after the first Scout outcome or first critique,
whichever happens first** — surface its live path in that turn and refresh both after every later
Worker act; no host watches the disk for you. Neither waits for the end of the run: a link handed
over once the work is finished is a link to something the user could no longer watch:

- A host that renders local files (the Claude Code desktop app) — show the files, and re-send them
  after each call.
- A host attached to an editor — open each once with `code <path>` or `cursor <path>`. The Cursor
  Agents window renders them as snapshots, so re-run the same command after each call; a VS Code
  tab refreshes itself.
- A terminal or TUI host — give the user the paths once so they can open them in their own editor.

The paths are absolute, and on a host that tells the server where the session is they sit inside it —
even when `workspaceRoot` names a repository root several levels up. Where your host turns a path
relative to your working directory into a link, write them that way; a backticked absolute path
renders as text the user cannot click. If a `documents` path is not under your working directory,
your host did not declare its roots and the absolute path is all there is.

However the files are surfaced, keep one line of narration in chat per worker call: the verdict and
finding count, or the task built and its status, so the user sees the run move without opening
anything. When chat is all the host has, expand that line to the findings themselves — severity,
where, what and ID — and for code review say which IDs you fixed, deferred, rejected, reopened or
closed, with the reasons and gate outcome.
Never paste raw JSON, and never let narration grow into pasting the plan itself — that is what the
`documents.plan` link is for.

## Approval

Nothing in the server asks the user anything. Showing them the plan and getting an answer is your
job, in five steps:

1. Call `forge.status` and read `driftedFiles` — the files that changed since `forge.begin`.
2. Show the user the **whole** plan, not a summary of it, and the drifted files beside it.
   - On the `Canvas` profile — the one `forge.begin` reported, which today means Cursor — call
     `forge.plan.show` with the plan. It renders as a document, with the drift above it, in the
     host's own UI. Do not paste the plan into the chat as well; the canvas is where they read it.
   - On the `Text` profile, use whatever your host displays best: an artifact, a widget, or plain
     markdown in the chat. Do not call `forge.plan.show` — nothing renders, and the result is the
     plan you are already holding.

   Either way, if anything drifted, say so out loud rather than leaving it in a list they may not
   read.
3. Ask them to approve it or say what to change. Ask in the chat even when the canvas is up: it
   displays and nothing more, and it says so to the user. On a change, revise the plan, write it
   with `forge.plan.write`, and go back to `forge.plan.review` with `revision` saying what you
   changed — a plan amended after the last verdict has not been reviewed — then show the revised
   plan again before asking a second time.
4. With a yes, ask for what the gates need on this host: the value of every `$env:NAME` the plan's
   gates reference, one variable per question, each saying which gate reads it and what for.
   Collect them in the chat — never write a value into the plan, and never guess one.
5. Pass what they answered to `forge.plan.confirm`, with the variables as `gateEnvironment` and
   any final plan decisions under one `decisionBatchId`. `builderRoots` is accepted for
   compatibility and does not control access. Use `addressedByRevision` for IDs the final revision
   fixed and `duplicateOf` for redundant IDs. The
   call refuses approval and lists every still-unresolved active plan ID. With a no answer, pass
   `approved: false` and no decisions; leave them for a later review or approved confirmation.

Never call `forge.plan.confirm` with an answer you did not get from the user. That call is the whole
of what approval means here: it writes the approved plan over `PLAN.md`, flips `approved` in the run
state, and unlocks the builder. No code anywhere checks whether anyone was actually asked. Pass the
plan you showed them, not the last draft a round reviewed — those differ whenever step 3 sent you
back to revise, and it is the confirmed text the builder walks.

## The gate decides the task; the builder's verification is its own word

Every build and fix result carries two accounts beside its `status`. `verification` is the
builder's: `outcome` is `passed`, `failed` or `unavailable`, and `evidence` says what it ran and what
it showed — or quotes the refusal when nothing could run. `gate` is the server's: it ran the task's
gate command on the host after the builder's turn, and reports `outcome`, the `command`, its
`exitCode`, the tail of its `output`, and `seconds`. The code review reads the diff, not either of
them.

If the Builder ran no checks because an executable gate belongs to the server, its verification
is `unavailable` with that explicit reason, not `failed`. Fully implemented work is still `done`;
the server runs its gate as usual. Optional Builder checks report only their own actual results,
never a claim that the gate passed. A genuine inability to check still reports its exact reason.

Read `gate.outcome` first:

- **`passed`** — the task counts, whatever the builder said about its own verification, and whatever
  it said about its own `status`. A builder that reported `unavailable` because its environment
  could not run the gate has been checked for you; a builder that reported `blocked` for the same reason has
  had `status` rewritten to `done`, because the gate is the proof it was missing. Its `verification`
  still says what it could not check, so read that before you narrate the task as a clean success.
- **`failed`** or **`timeout`** — the `status` is `gate_failed`, `tasksCompleted` did not move, and
  the next `forge.build.next` retries the same task with the gate's command, exit code and output in
  front of the builder. The Builder fixes the cause without running the gate; separate diagnostic
  checks are optional, and the server repeats the gate. Call it again. If the same gate fails twice
  more, stop and show the user the output rather than spending a fourth turn: the gate may be wrong, the environment may be missing
  a variable, or the task may be beyond the builder. A `## Gates` failure after `forge.review.fix`
  is the same signal with no task to withhold — the next fix carries it — so do not start the next
  `forge.review.code` round on a `gate_failed` fix without deciding what to do about it.
- **`not_executable`** — the gate is a condition rather than a command, or the task states none.
  Only here does the builder's `verification` decide: on `unavailable`, run the check yourself and
  record what you ran and saw through `forge.log.append` before the next act; on `failed`, do not
  advance past it — verify yourself, and either fix forward or stop and ask the user.
- **`not_run`** — the builder reported `blocked` after a verification that `failed`, so it ran the
  check itself and watched it fail and there is nothing left for a gate to settle; or the host has no
  PowerShell. The second case is the environment's fault, not the task's: say so and treat the task
  as `not_executable`. A `blocked` turn whose verification was `unavailable` never lands here — the
  host runs the gate for it, and the outcome is one of the three above.
- **`not_run` with `status` `background_killed`** — the builder ended its turn while a command it
  had started in the background was still running, and the session killed it; `gate.detail` names
  the command. By the builder's own account the work was unfinished, so no gate ran and
  `tasksCompleted` did not move. Call `forge.build.next` again: the retry is told what was killed and
  to run it in the foreground. If it happens twice for the same command, the command probably
  outlasts the worker's 30-minute foreground limit — split the task, or run the command yourself and
  record it with `forge.log.append`. Only a claude builder reports this.

Say the gate's outcome in your one line of narration — a task whose gate failed, or whose gate
nobody could run, must never read like a clean `done` in the chat.

### When the host times the call out

`forge.build.next` and `forge.review.fix` can run longer than the host's clock — an hour on Claude
Code and Codex — and the host then cancels the call and kills the builder wherever it had got to.
You get an error such as `MCP server "plan-forge-flow" timed out after 3600s` and no result.

**It is not an act that changed nothing.** The builder may have finished the work and been killed
before it could report, so before you do anything else read the run's own account of it:

- the flow log has a `cut short` entry naming the files the builder had written, which are still on
  disk;
- the attempted `fixFindingIds` remain unresolved in the decision ledger, and the Flow audit keeps
  the cut-short attempt;
- the task was not counted and no gate ran, so nothing is verified.

Do **not** invent a fresh attempt or tell the user the work was lost before you have looked. Call the
same act again with the same `fixAttemptId` and exact `fixFindingIds`: it resumes the same Builder
session and links the retry to the eventual automatic gate closure. If the attempt already reached
terminal success, the retry returns its saved result without another Builder or gate. If you need to
know where the tree stands first, `forge.status` gives the ledger summary and drift, and `git diff`
gives the content. Should the retry time out the same way, the work is too big for one call — split
the remaining work into a new legal attempt, or run the checks yourself and record them with
`forge.log.append`.

## The code-review loop

After the last task and before the first review round, run the plan's `## Gates` entries yourself,
in your own environment — all of them, conditions included. The server runs the executable ones
only after a fix round, so at this point nobody has: they are the checks no single task owned, no
builder ran them, and the critic must not — it judges the diff, and a build writes into the very
tree it is reading. Record what you ran and what it showed with `forge.log.append`. A failing gate
is not a code-review finding: stop there and decide with the user, exactly as with a task whose gate
failed.

Then the loop is yours to run, exactly as with plan review: on non-Cursor hosts, `forge.review.code`
runs one critic round against the approved plan and `forge.review.fix` hands kept findings to the
builder; on Cursor, run both acts through start → poll → fetch. The same routing applies to Scout:
non-Cursor hosts call `forge.scout.run` directly with an explicit `sessionMode`, while Cursor runs
the `scout` act only through `forge.work.start` → `forge.work.poll` → `forge.work.fetch`. Repeat until
the verdict is `approve` or the cap refuses. The critic and the builder never talk directly — you
are between them because you are the only participant who knows what the plan deliberately left
out.

Between the two calls, classify every identified finding by ID:

- **Fix** unresolved defects the diff gets wrong. Put exactly those IDs in `fixFindingIds`, allocate
  one `fixAttemptId` for that logical execution, and let the server render their verbatim ledger
  findings for the Builder. Batch them by rule and frame each batch with a note, as "Fix batches and
  their notes" below describes.
- **Defer or reject** what the approved plan excludes or the user already decided differently, with
  asserted `by` and a reason in one decision batch. This is a decision, not a deletion.
- **Close a duplicate** when a new ID is semantically the same as a current ID: keep the canonical
  ID and close the redundant ID with `duplicateOf` plus the reason.
- **Answer reopening proposals** with `accept` or `decline`. Only acceptance changes the existing
  settled ID; the Critic cannot reopen it itself. A plan-origin ID accepted here keeps its origin and
  becomes active in code review.
- **Host-close** an active code ID only after your own verification, using `hostVerified` with
  evidence in a decisions-only `review.fix` call.

The bias to hold: when unsure whether to settle a finding, fix it. Deferral and rejection are for
decisions the plan or user settles, never for findings that are inconvenient. `review.fix` may carry
decisions and an independent fix attempt together, but the same ID cannot be both decided and fixed,
and a batch with `raises` travels without a fix attempt.

The Builder reports each fix as a rule and its places: `rule:`, then one line per place with
`fixed`, `already agreed`, or `left: <reason>`. Read that report before the next round:

- A `left:` place the approved plan excludes needs nothing more; it is out of scope, as it would be
  for a Critic. Raise every other one in a decisions-only `forge.review.fix`, then fix it in a later
  call by the ID the raise returned, or leave it for the next Critic, which has to assess it. A place
  left only in a summary comes back as a Critic finding a round or two later, at a round's price.

When the verdict settles — or the cap is reached and the user chooses to stop — the deferred
findings go to the user with the outcome. They are real findings about real gaps; the plan is the
only reason they were not fixed here, and they are candidates for the next run.

### Fix batches and their notes

A batch is the set of IDs one fix attempt fixes. Form batches by the rule the findings break, not by
file or severity:

- Never split one rule's findings across batches. Two deciders of one question in two batches — the
  effect of a write and the value it stores, the cheap filter and the precise check — get one turn
  that fixes half the rule and another that finds it half fixed.
- A batch may carry several rules. Keep rules that share an owner or a subsystem together, and keep
  apart rules whose failure should not hold the others back: every fix call runs the plan's
  `## Gates`, so each batch costs a gate run as well as a turn. A finding whose rule no other finding
  shares is a rule of its own.
- When a fix also covers a finding outside its batch, close that finding afterwards with
  `hostVerified` and the evidence: the test, the `file:line`, the gate run. `duplicateOf` cannot do
  it, because it needs a canonical ID still in the ledger, and the gate's closure has removed the
  batch's IDs.

The first call of every fix attempt carries a `note` that frames its batch by the rules its findings
break. The Builder already has the findings verbatim, so do not paste them or the critique back; the
note adds only what the ledger lacks. For each rule:

- **Rule** — one sentence: the property the code gets wrong, not the example that showed it, with
  the IDs it covers and where it comes from. Cite the requirement, `R5`: the Builder holds the
  requirements in its Brief. A fix prompt carries no task text, so when the rule rests on a task's
  wording, quote that sentence. When it rests on a decision the user made during the run, say so,
  and record the decision with `forge.log.append` if you have not.
- **Owner** — the one place that decides the question: the owner the plan names, an existing
  function, or one to create. Say that every other place calls it instead of deciding again.
- **Axes** — every kind of place where the same question is decided, listed. Take them from the
  plan first: the rules the matrix check listed with their axes, a matrix task's axes, the places
  and axes a task names for the rule's owner. Then check the kinds a plan tends to miss: the forms
  that reach the same operation (an invocation, an object creation, a delegate call); the channels
  a value enters or leaves by (a result, an `out` or `ref` parameter, a store, a value kept for
  later, a delegate's input); the executions it runs in (setup, the call, an enumeration, a startup
  or unknown execution); the kinds of input or object; the direct path and the one through a
  helper, a `ref` or an alias; the other arms of the same switch; and twin deciders — the cheap
  filter and the precise solver, the producer and each consumer, the code and a document that
  restates the rule. Tell the Builder to find them by references and callers of the data that
  carries the fact, not by searching for a word: a search finds a spelling, not a place that decides
  without it.

Once for the whole batch, and again under a rule only where that rule has its own:

- **Must not change** — recorded floors and snapshots, the fixes of other findings, and the scope
  the plan excludes.
- **Stop condition** — when to stop and report instead of widening: a floor would fall, the rule
  would contradict the plan, a place has no source for what the rule needs.

A part that does not apply says so in a few words — `Axes: none, the count is stated only here` —
rather than being left out.

A note steers the Builder as firmly when it is wrong as when it is right. In one measured run a note
told the Builder to drop a guard that was a requirement's own rule, and the next attempt had to undo
the unsafe narrowing that followed. The cited source is what lets the Builder check a rule against
the plan before it acts on it.

A retry of the same attempt resumes the Builder session that already holds the first note, so its
note says what changed since the last call — what is on disk, what failed, what is left — and
restates in full, with its source, only a rule it corrects. A retry sent to another vendor starts a
fresh session: send the whole note again.

An example, in a neutral domain — copy the form, not the words:

```text
Rule 1 (F-0031, F-0034, F-0040) — a request's tenant is the one its access token names, never one
the caller supplies (R3; task 2: "every query is scoped to the tenant of the authenticated caller").
Owner: `TenantResolver.FromToken`. Every place that needs the tenant asks it; none reads `tenantId`
from the route, the query string or the body again.
Axes: every way a request is served — HTTP handlers, background jobs run for a user, message
consumers; every way the tenant leaves a call — repository filters, cache keys, outgoing calls, the
audit log; the twin deciders — the authorization filter and the repository's own query filter. Find
them by references to the tenant value and their callers, not by searching for `tenantId`.

For the batch — Must not change: the recorded performance baseline, the schema (the plan excludes
migrations), this round's fix of F-0029.
Stop condition: a job with no token to read the tenant from — report the job rather than inventing
a source.
```

### When a round is mostly fallout

Measured over nine runs, about half of the code-review findings after the first round were fallout
of the previous round's fixes: a rule an earlier fix wrote was wrong, or a place answering the same
question was left behind. After each round, label every new finding and record the labels with
`forge.log.append`, one line per round:
`Code review round 3: own F-0150; sibling F-0152; same-class F-0149; older F-0151`.

- `own` — something an earlier fix wrote is wrong: the rule it keyed on, or anything else it changed;
- `sibling` — a place that answers the same question as an earlier fix was left behind;
- `same-class` — an older instance of a class an earlier fix covered only where a finding named it;
- `older` — anything else the review predates.

`own` and `sibling` are fallout. When half a round or more is fallout, or its findings walk one axis
of a rule case by case — field, then local, then `ref` — ask for no further round yet. Sweep instead:
list the axes of each rule the fixes touched, probe every combination against an oracle (a second
path that must agree), fix the root causes, then spend one round to confirm. Hold the fixes you make
on the host to the Builder's own rule — fix the rule, not the place — and record each with
`forge.log.append`.

### When a round is too large for the critic's vendor

The review window only grows over a run, and codex accepts at most 1,048,576 characters in one
prompt. A round over its vendor's limit is refused before any critic starts: nothing was sent, the
round was not counted, a grant the user gave for it still stands, and the flow log says the round
was not sent. The refusal lists what the prompt holds and its ten largest files, named from
`workspaceRoot`. Show the user those sizes and ask how to narrow the round, as "Asking the user"
describes, then start the same round again with what they chose:

- `untrackedByReference: true` lists the untracked files for the critic to read from the working
  tree instead of embedding them. Nothing leaves the review; the critic reads the new files with its
  own tools, and their contents still pass the secret check. Recommend it when the untracked files
  are the run's work.
- `excludePaths` takes paths or patterns out of the round, named as the refusal names them. Only
  for what is not the run's work — a plan's gate script, generated output such as an eval snapshot.
  A deliverable the plan asks for left out is one the critic cannot judge.

Neither is remembered: pass the same arguments on every later round, and say in your narration what
the critic was not handed. Do not move files out of the repository to make a round fit — a gate
that runs a moved file fails, and the window would then differ from the tree the gates check.

## Choosing the vendor and model

This happens at the end of Act 1, after the last interview question and before the first plan
draft — the depth rule above reads the builder's selection.

Critic/Builder selection remains at most six questions total: two Vendor questions, two
model/effort questions, and one Fast question for each role whose choice offers it. Ask them in two
calls, one per role, the critic's first: each call carries that role's vendor, model-and-effort,
Fast and instruction questions, in the order of the steps below — four questions at most. A single
just-in-time Scout selection round is additional; there is no numeric cap on the domain interview.
The combinations come from the server, not from your own knowledge. Call `forge.models` (no
`vendor` argument) again immediately before the Critic/Builder Vendor question: successful probes
return from `CatalogCache` immediately, while previously unavailable probes rerun so a repaired
CLI or sign-in can re-enter the choices. Do not claim that the catalogue is called only once. Build
both calls from that one answer: it holds every available vendor's models, efforts and Fast tiers,
which is what lets one call carry questions that would otherwise wait on each other's answers.

The questions in a call cannot see each other's answers, so every option says what it depends on:
a model-and-effort pair names its vendor, and the Fast question names the pairs that offer it. When
the answers do not fit together — a pair from a vendor the user did not choose, a free-text model
the chosen vendor's catalogue does not list, Fast for a pair that has none — ask one follow-up
question about the part that does not fit before that role's selection is used.

1. The vendor question offers only vendors the catalogue reports `available: true`. Never offer a
   vendor with `available: false`; its `detail` names the cause — a missing CLI, a sign-in — so
   tell the user why it is out and what would bring it back.
   - If none is available, stop and relay what each probe reported; no act can run without a
     working vendor CLI.
   - If exactly one is available, do not ask either vendor question. Tell the user which vendor
     both roles will use, and draw step 2's pairs from it alone.
   - If you are orchestrating from inside Cursor and `cursor` is available, do not ask either
     vendor question even when other vendors are too: Cursor fronts models from several vendors
     behind the one `cursor-agent` CLI, so the vendor distinction is already expressed by the
     model choice. Trust either signal alone: the `client` field `forge.begin` returned names
     cursor, or the host you are running in is Cursor. Tell the user both workers will run through
     `cursor-agent`, put both roles on the `cursor` vendor, and draw step 2's pairs from its
     catalogue alone.
2. The model-and-effort question requests a valid combination, drawn from the catalogue in the
   tool's order — it is already newest first. Offer three concrete model-plus-effort pairs, your
   recommendation first, among them the vendor's own pick where it names one (`isDefault`,
   `defaultEffort`), and say which that is; always leave free text open. While the call also asks
   for the vendor, draw the pairs from the vendors it offers, your recommended vendor's first, and
   name each pair's vendor in its label.
   Say what kind of list you are offering: `source: "live"` came from the vendor CLI just now;
   `source: "resolved"` (claude) is a list of aliases this repo remembers, each one turned by the
   CLI into the model it currently stands for — so name that model beside the alias, because
   `displayName` carries it and the alias alone does not say which model the run would use. A
   family released since this plugin shipped appears only if the CLI knew to name it. Never offer
   one vendor's model family under another vendor.

   For the `cursor` vendor, the catalogue has already collapsed the ~200 raw ids into families:
   the model id is the family (`gpt-5.3-codex`) and `efforts` are the variants the CLI actually
   listed (`low`, `high`, …, plus `default` when the bare id is itself listed), with the ones it
   also listed as a `-fast` id under `fastEfforts`. Pass the family id as `model` and the chosen
   variant as `effort` — or leave `effort` unset for the `default` variant — and the server joins
   them back into an id the CLI listed, adding `-fast` when `fast` is true. Never build an id
   yourself, never write `-fast` into a model or an effort, never offer an effort the catalogue does
   not list for that family, and never use the bracket-override syntax the CLI's own tip advertises
   (`model[effort=high]`): measured on 2026-08-19, cursor-agent rejects even the tip's own example.

3. Ask the Fast question only when a pair the call offers lists its effort under `fastEfforts` —
   `default` for a cursor family offered without an effort, any entry at all for claude or codex
   without one — with `fastUnavailable` null: should the role run at the vendor's Fast tier,
   quicker, at a higher usage price? Name the pairs that offer it. Offer "standard speed" first, as
   the recommendation unless the user has asked for speed, and name `fastHint` beside Fast where
   the catalogue gives one — it is the vendor's own word on the price. Pass `fast: true` to that
   role's tools only on a yes for a pair that offers it. A model whose `fastUnavailable` is set
   offers Fast but this account will not serve it: do not ask, and say why when the user asks for
   Fast anyway (`extra_usage_disabled` means claude bills Fast as extra usage, and the account has
   it off). The server refuses a Fast request the catalogue does not confirm, and claude refuses one
   again at the start of the worker's session; a turn claude served at standard speed for part of
   the way still counts, and its result carries `speedWarning` — tell the user.

4. The instruction question asks whether the user wants to tell that role's worker anything for
   this run, offering "no instructions" first. This is their only channel to a worker besides the
   plan: a language to answer in, a class of finding this repository does not want raised, a skill
   the builder should use, a house style. Say that much before the call, and propose no wording of
   your own. Pass what they answer to `forge.instructions.set` **verbatim**, and add nothing of your
   own. If both roles' answers are "no instructions", do not call the tool at all.

   The two roles hear it differently, which is worth saying if they ask. A critic is a fresh process
   every round and is handed its text every round. A builder holds a session for one plan task or
   one code-review round and is handed its text only when a session starts, so instructions given
   mid-task reach it with the next task or round — or sooner, when a vendor switch, a reopened plan,
   or direct re-approval after a changed Builder Brief starts a new session. The tool's answer says
   so when that is the case, and you should pass that on rather than assume it landed. The builder's text is also shown to the code-review critic as context, so that
   critic does not raise findings for a choice the user asked for.

The catalogue is advisory for model and effort: an unfamiliar model arriving as free text is worth
mentioning, not refusing, because the vendor CLI decides. Fast is the exception — the server
refuses what the catalogue does not confirm. The roles are not interchangeable in strength. The
builder works against an already-hardened plan and can be cheap; the critic is judging, so lean
nearer the strong end.

## What is not enforced

Nothing stops you from abandoning a run halfway, or from editing code during Act 1. There are no
hooks — the trade is deliberate, and the one thing the server checks is a task's gate command. The
consequences to hold yourself to:

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
- Do not stop mid-run without telling the user where you stopped and what remains.

Do not hand-edit anything under `.forge/` — including `forge.log`, which is append-only and
written through `forge.log.append`. Use that tool whenever a run does something a later reader
would have to guess at: the models and vendors you selected and why, a retry and what provoked it,
a finding you deferred, the point at which you stopped. It takes `message` plus an optional `level`
(`info`, `warn`, `error`) and an optional longer `detail`. The server already records every tool
call with its arguments, every vendor process with its full command line, and every process exit,
kill and stderr tail into the same file; your entries are the part it cannot see.

Do not stage or commit the workers' changes; leave the diff for the user to inspect.
