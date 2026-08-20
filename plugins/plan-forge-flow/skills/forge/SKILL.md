---
name: forge
description: Use only when the user explicitly invokes $forge or directly asks to run Plan Forge Flow. Hardens an implementation plan through independent review rounds, approval, a stepwise builder, and a final code review, using the plan-forge-flow MCP tools.
---

# Plan Forge Flow

You are the **orchestrator**. You run the interview, you revise the plan between review rounds, and
you call the tools. The workers behind the tools — a critic and a builder, each a separate model
process — never revise the plan, because they do not have the interview context you have.

Windows x64 only. The tools come from the `plan-forge-flow` MCP server; if they are not listed,
the plugin is installed but the server did not start, and nothing here will work.

## When to start

Only on an explicit `$forge` or a direct request to run Plan Forge Flow. Installation, availability,
an ordinary request to plan something, or an existing draft are not consent.

## The tools, in order

| Tool | When |
|---|---|
| `forge.begin` | Once, before anything else. Returns the `runId` and the connecting `client`, takes a baseline of the working tree, and starts every vendor's catalogue probe in the background. |
| `forge.models` | Once, before the vendor question. Returns each vendor's model catalogue, newest first, with `available` and the reason when a vendor is not. |
| `forge.plan.review` | On non-Cursor hosts, once per round with the current draft. Returns one critique. **You** then revise the plan and call it again. |
| `forge.plan.confirm` | When the critique settles and you have shown the user the plan and asked them. Records their answer. |
| `forge.build.next` | On non-Cursor hosts, once per task, repeatedly, until `tasksCompleted` equals `taskCount`. |
| `forge.review.code` | On non-Cursor hosts, once per round after the last task. Returns one critique. **You** then filter the findings and call `forge.review.fix`. |
| `forge.review.fix` | On non-Cursor hosts, after each `revise` verdict, with the findings you kept and the ones you deferred. |
| `forge.status` | Before asking for approval, and any time the user asks where things stand. Carries the drift. |
| `forge.work.start` | On Cursor, starts one worker act. If `started` is false, rejoin the returned active `jobId`; do not create another worker. Blank `findings` for `review.fix` is valid and takes the all-deferred path without starting a builder session. |
| `forge.work.poll` | On Cursor, waits for the started job. A `running` result means keep waiting and is not narration-worthy on its own. |
| `forge.work.fetch` | On Cursor, fetches the terminal worker result after polling. |

Every tool takes `workspaceRoot` and, after `forge.begin`, `runId`. On a Cursor client, every worker
act goes through `forge.work.start` → `forge.work.poll` → `forge.work.fetch`; do not call a
one-call worker tool there. On every other host, the one-call worker tools — `forge.plan.review`,
`forge.build.next`, `forge.review.code`, `forge.review.fix` — remain the instruction and take `model`,
an optional `effort`, and an optional `vendor`: `claude`, `codex`, or `cursor`, defaulting to
`claude`. The critic's selection goes to the two review tools, the builder's to `forge.build.next`
and `forge.review.fix`. For Cursor's `review.fix`, blank `findings` means all findings are
deferred, so the act completes without starting a builder session. If `forge.work.start` returns
`started: false`, rejoin its active job with poll → fetch.

Worker calls run for minutes, and the host's clock on a tool call is not yours to extend. On Cursor,
use the three work tools above so the surviving server can rejoin a detached worker; on every other
host, use the one-call worker tools. A `running` poll alone is not narration-worthy. If the
originating server process exits, an in-flight job id is unknown to a new server and cannot be
rejoined; after restart, start a new act. Persisted terminal results remain under
`.forge/<runId>/`.

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

Ask grilling questions **one at a time** and wait for each answer. You are looking for the decisions
the plan would otherwise leave to whoever implements it: what is out of scope, what happens on the
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
`## Approach` is context for the reader and is not walked; the section ends at the next `##`
heading, so put the tasks last or expect everything after that heading to be dropped.

Inside it, number the tasks `1.` to `N.` in order, one task per numbered item — a gap or a repeat is
refused outright. That numbering is what `forge.build.next` walks, so a task that is really three
tasks will be built as one.

Above it, `## Requirements` states what must be true when the work is done: one numbered requirement
per item, `R1` to `Rn`, and then what the run deliberately does not do. Requirements are the
interview's answers, not the implementation — what must become true, what must not change, how it
would be observed — so no file names, no symbols, no "how". The exclusions carry as much weight as
the requirements: they are what stops the critic demanding work the user already ruled out.

Every task ends with a **Gate** — the command or the observable condition that would show that task
done — and cites the requirements it serves. A check that belongs to no single task goes under
`## Gates` instead, numbered `G1` to `Gn`, each citing what it discharges: the test suite, a
warnings-clean build, an invariant spanning the whole change. Leave that section out when the task
gates already cover everything; a ceremonial gate is worse than none. Its entries are yours to run
after the last task — see the code-review loop below.

```markdown
Builder: cursor / gpt-5.3-codex / high

## Requirements

1. **R1.** What must be true once the work is done.
2. **R2.** What must not change.

**Out of scope.** What this run deliberately does not do.

## Gates

1. **G1.** `dotnet test …` passes. (R1, R2)

## Approach

1. **First task.** What to change. **Gate:** the command or condition showing it done. (R1)
2. **Second task.** … (R2)
```

Write every task to be read alone. The builder receives `# Task N of M` and the task's own text —
not the preamble, not the requirements, not the run-wide gates, not the other tasks, not the
interview. Context a task needs must be inlined into the task, and that includes whatever a
requirement it cites actually demands; the builder's session accretes across tasks, but task 1
starts from nothing.

Scale the plan's depth inversely to the builder you selected. A strong model at high effort takes
goal-level tasks. The cheaper the model or the lower the effort, the smaller and more explicit each
task must be: name the files and the symbols, decide the edge cases and the error paths yourself,
and make each task's `Gate` an exact command rather than a condition to interpret — leave nothing to
the builder's judgement, because the builder you chose has less of it. Judge strength from the
vendor's own catalogue — the position in its newest-first list and the chosen effort — not from a
remembered model name.

State the builder's selection at the top of the plan, above `## Requirements`, in one line — vendor,
model, effort. The critic judges the plan's depth against the builder named there.

## Rounds, revision, and caps

Each non-Cursor `forge.plan.review` call, or each Cursor start → poll → fetch round, runs exactly
one round and returns a verdict of `approve` or `revise` plus findings. On `revise`, address the
findings in the plan yourself and run the next round. The critic is a fresh process each round but
is given the log of earlier rounds, so it converges rather than reopening settled points.

The critic judges the requirements too, and those findings are not all yours to fix. One the
interview already settles — two requirements you wrote that contradict each other, a condition
stated too vaguely to check — you revise and carry on without stopping. One it does not — a
requirement covering a question nobody asked, or an answer that would move the scope — goes to the
user before you revise, and its answer goes into `forge.log.append`. Ask the moment it comes up
rather than saving it for approval: a scope question answered late invalidates every round that ran
after it.

Review rounds are capped, and so is the code-review loop. When a cap is reached the tool refuses.
Ask the user whether to accept the remaining risk or stop — never raise a cap on your own.

Keep the drafts to yourself. The rounds are working material, and showing the user every revision
buries the one version that matters. The plan reaches them exactly once, when the critic returns
`approve`.

## Show the workers' output as you go

Every tool result lands in your context and nowhere else — the user sees none of it unless you
surface it. The server keeps the user-facing timeline for you: every worker call appends its
outcome to `<runPath>/flow_log.md` — critiques with verdict and findings, build results with
status and files changed, and each fix round's kept and deferred findings. Unlike
`review-log.md`, nothing feeds this file back to a worker; it exists to be shown.

The file appears when the first plan-review result returns, whether from the one-call tool or
`forge.work.fetch`. Surface it then, with the best your host has, and refresh it after every later
worker act — no host watches the disk for you:

- A host that renders local files (the Claude Code desktop app) — show the file, and re-send it
  after each call.
- A host attached to an editor — open it once with `code <path>` or `cursor <path>`. The Cursor
  Agents window renders it as a snapshot, so re-run the same command after each call; a VS Code
  tab refreshes itself.
- A terminal or TUI host — give the user the path once so they can open it in their own editor.

However the log is surfaced, keep one line of narration in chat per worker call: the verdict and
finding count, or the task built and its status, so the user sees the run move without opening
anything. When chat is all the host has, expand that line to the findings themselves — severity,
where, what — and for code review say which findings you kept versus deferred, with the reasons.
Never paste raw JSON, and never let narration grow into showing the plan drafts that the
paragraph above keeps out of the chat.

## Approval

Nothing in the server asks the user anything. Showing them the plan and getting an answer is your
job, in four steps:

1. Call `forge.status` and read `driftedFiles` — the files that changed since `forge.begin`.
2. Show the user the **whole** plan, not a summary of it, and the drifted files beside it. Use
   whatever your host displays best: an artifact, a canvas, a widget, or plain markdown in the chat.
   If anything drifted, say so out loud rather than leaving it in a list they may not read.
3. Ask them to approve it or say what to change. On a change, revise the plan and go back to
   `forge.plan.review` — a plan amended after the last verdict has not been reviewed.
4. Pass what they answered to `forge.plan.confirm`.

Never call `forge.plan.confirm` with an answer you did not get from the user. That call is the whole
of what approval means here: it writes `PLAN.md`, flips `approved` in the run state, and unlocks the
builder. No code anywhere checks whether anyone was actually asked.

## The builder's verification is self-reported — reacting to it is yours

Every build and fix result carries a `verification` object beside its `status`: `outcome` is
`passed`, `failed` or `unavailable`, and `evidence` says what ran and what it showed — or quotes
the refusal when nothing could run. The server records it and moves on; nothing downstream
re-checks it, and the code review reads the diff, not the test results.

So when `outcome` is anything but `passed`, the verification step is yours before the run
advances:

- **`unavailable`** — the builder implemented the task but could not execute its verification
  (a broken sandbox, a denied spawn). Run the task's verification step yourself, in your own
  environment. Record what you ran and the outcome through `forge.log.append` before starting the
  next worker act, whichever tool your host reaches it through.
- **`failed`** — the check ran and did not pass. Do not advance past it: verify yourself, and
  either fix forward through the flow or stop and ask the user.

Say the outcome in your one line of narration either way — a task whose verification the builder
could not run must never read like a clean `done` in the chat.

## The code-review loop

After the last task and before the first review round, run the plan's `## Gates` entries yourself,
in your own environment. Nobody else will: they are the checks no single task owned, so no builder
ran them, and the critic must not — it judges the diff, and a build writes into the very tree it is
reading. Record what you ran and what it showed with `forge.log.append`. A failing gate is not a
code-review finding: stop there and decide with the user, exactly as with a task whose verification
failed.

Then the loop is yours to run, exactly as with plan review: on non-Cursor hosts, `forge.review.code`
runs one critic round against the approved plan and `forge.review.fix` hands kept findings to the
builder; on Cursor, run both acts through start → poll → fetch. Repeat until
the verdict is `approve` or the cap refuses. The critic and the builder never talk directly — you
are between them because you are the only participant who knows what the plan deliberately left
out.

Between the two calls, sort every finding into exactly two piles:

- **Pass through** whatever the diff gets wrong: correctness, security, a plan task done badly or
  not at all. Pass these verbatim — do not soften them, and do not add work the critic never asked
  for.
- **Defer** what the approved plan excludes or the user already decided differently. A deferral is
  a decision, not a deletion: give every one a reason in `deferred`. It lands in the review log, so
  the next round's critic treats it as settled instead of re-raising it each round.

The bias to hold: when unsure which pile a finding belongs in, pass it through. Deferral is for
findings the plan settles, never for findings that are inconvenient.

When the verdict settles — or the cap is reached and the user chooses to stop — the deferred
findings go to the user with the outcome. They are real findings about real gaps; the plan is the
only reason they were not fixed here, and they are candidates for the next run.

## Choosing the vendor and model

This happens at the end of Act 1, after the last interview question and before the first plan
draft — the depth rule above reads the builder's selection.

Choose vendors and models in two steps, asking at most four questions total. The combinations come
from the server, not from your own knowledge: `forge.begin` already started every vendor's probe in
the background, so call `forge.models` (no `vendor` argument) once before the vendor question and
work from its answer.

1. Ask for the critic vendor and the builder vendor, offering only vendors the catalogue reports
   `available: true`. Never offer a vendor with `available: false`; its `detail` names the cause —
   a missing CLI, a sign-in — so tell the user why it is out and what would bring it back.
   - If none is available, stop and relay what each probe reported; no act can run without a
     working vendor CLI.
   - If exactly one is available, do not ask either vendor question. Tell the user which vendor
     both roles will use and continue directly to step 2.
   - If you are orchestrating from inside Cursor and `cursor` is available, do not ask either
     vendor question even when other vendors are too: Cursor fronts models from several vendors
     behind the one `cursor-agent` CLI, so the vendor distinction is already expressed by the
     model choice. Trust either signal alone: the `client` field `forge.begin` returned names
     cursor, or the host you are running in is Cursor. Tell the user both workers will run through
     `cursor-agent` and continue directly to step 2 with both roles on the `cursor` vendor.
2. Ask one question for each role, requesting its model and effort together as a valid combination
   for that role's chosen vendor, drawn from that vendor's catalogue in the tool's order — it is
   already newest first. Offer three concrete model-plus-effort pairs, leading with the vendor's
   own pick where it names one (`isDefault`, `defaultEffort`), and always leave free text open.
   Say what kind of list you are offering: `source: "live"` came from the vendor CLI just now;
   `source: "declarative"` (claude) is the list this repo remembers, which the CLI may have moved
   past. Never offer one vendor's model family under another vendor.

   For the `cursor` vendor, the catalogue has already collapsed the ~200 raw ids into families:
   the model id is the family (`gpt-5.3-codex`) and `efforts` are the variants the CLI actually
   listed (`low`, `high`, `high-fast`, …, plus `default` when the bare id is itself listed). Pass
   the family id as `model` and the chosen variant as `effort` — or leave `effort` unset for the
   `default` variant — and the server joins them back into an id the CLI listed. Never build an id
   yourself, never offer an effort the catalogue does not list for that family, and never use the
   bracket-override syntax the CLI's own tip advertises (`model[effort=high]`): measured on
   2026-08-19, cursor-agent rejects even the tip's own example.

The catalogue is advisory: an unfamiliar model arriving as free text is worth mentioning, not
refusing, because the vendor CLI decides. The roles are not interchangeable in strength. The
builder works against an already-hardened plan and can be cheap; the critic is judging, so lean
nearer the strong end.

## What is not enforced

Nothing stops you from abandoning a run halfway, or from editing code during Act 1. There are no
hooks and no gates in this version — the trade is deliberate. The consequences to hold yourself to:

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
- These paths no longer appear in `forge.status` drift or in the code-review diff. Documentation the
  orchestrator wrote must not be reported as drift and must not be expected back from the critic.
  `git diff` never listed untracked files, so a new ADR was already invisible.
- Do not stop mid-run without telling the user where you stopped and what remains.

Do not hand-edit anything under `.forge/` — including `forge.log`, which is append-only and
written through `forge.log.append`. Use that tool whenever a run does something a later reader
would have to guess at: the models and vendors you selected and why, a retry and what provoked it,
a finding you deferred, the point at which you stopped. It takes `message` plus an optional `level`
(`info`, `warn`, `error`) and an optional longer `detail`. The server already records every tool
call with its arguments, every vendor process with its full command line, and every process exit,
kill and stderr tail into the same file; your entries are the part it cannot see.

Do not stage or commit the workers' changes; leave the diff for the user to inspect.
