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
| `forge.begin` | Once, before anything else. Returns the `runId` and takes a baseline of the working tree. |
| `forge.plan.review` | Once per round, with the current draft. Returns one critique. **You** then revise the plan and call it again. |
| `forge.plan.confirm` | When the critique settles and you have shown the user the plan and asked them. Records their answer. |
| `forge.build.next` | Once per task, repeatedly, until `tasksCompleted` equals `taskCount`. |
| `forge.review.code` | Once per round, after the last task. Returns one critique. **You** then filter the findings and call `forge.review.fix`. |
| `forge.review.fix` | After each `revise` verdict, with the findings you kept and the ones you deferred. |
| `forge.status` | Before asking for approval, and any time the user asks where things stand. Carries the drift. |

Every tool takes `workspaceRoot` and, after `forge.begin`, `runId`. The four worker tools —
`forge.plan.review`, `forge.build.next`, `forge.review.code`, `forge.review.fix` — take `model`,
an optional `effort`, and an optional `vendor`: `claude`, `codex`, or `cursor`, defaulting to
`claude`. The critic's selection goes to the two review tools, the builder's to `forge.build.next`
and `forge.review.fix`.

## Act 1: the interview

Call `forge.begin` first, before asking an interview question or invoking an interview skill. Then
ask exactly one mode question, unless the user already said which mode they want when they invoked
`$forge`. Offer exactly these two modes every time:

- interview without documentation;
- interview that maintains the domain model as it goes.

Keep the skill-availability chain out of that question. Each mode resolves down a three-step ladder,
taking the first step that is available:

| | without documentation | with documentation |
|---|---|---|
| 1 | `grill-me` | `grill-with-docs` |
| 2 | `grilling` | `grilling` **and** `domain-modeling` |
| 3 | the interview paragraph below | the built-in documented rules below, plus the two references |

A step is available only when **every** skill it names is in the host's catalogue. So step 1 of the
mode without documentation needs `grill-me` *and* the `grilling` it delegates to; step 1 of the
documented mode needs `grill-with-docs`, `grilling` and `domain-modeling`. A name absent from the
catalogue is absent; never guess that it is available.

If the host publishes no catalogue at all, each candidate step may be attempted once: make one
attempt per candidate, not one attempt per run, and treat any error as absence. When a catalogue
exists, do not attempt a name that it does not contain.

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

Write the plan as markdown, with the tasks under a heading spelled exactly `## Approach`. That
heading is not a suggestion: `PlanTasks` refuses a plan without exactly one of it, and
`forge.plan.confirm` parses before it writes anything, so the wrong heading fails at approval rather
than later. Anything above `## Approach` is context for the reader and is not walked; the section
ends at the next `##` heading, so put the tasks last or expect everything after that heading to be
dropped.

Inside it, number the tasks `1.` to `N.` in order, one task per numbered item — a gap or a repeat is
refused outright. That numbering is what `forge.build.next` walks, so a task that is really three
tasks will be built as one.

```markdown
## Approach

1. **First task.** What to change, and how it is verified.
2. **Second task.** …
```

## Rounds, revision, and caps

`forge.plan.review` runs exactly one round and returns a verdict of `approve` or `revise` plus
findings. On `revise`, address the findings in the plan yourself and call the tool again. The critic
is a fresh process each round but is given the log of earlier rounds, so it converges rather than
reopening settled points.

Review rounds are capped, and so is the code-review loop. When a cap is reached the tool refuses.
Ask the user whether to accept the remaining risk or stop — never raise a cap on your own.

Keep the drafts to yourself. The rounds are working material, and showing the user every revision
buries the one version that matters. The plan reaches them exactly once, when the critic returns
`approve`.

## Show the workers' output as you go

Every tool result lands in your context and nowhere else — the user sees none of it unless you
relay it. Do so after every worker call, in a few lines:

- `forge.plan.review` — the verdict, and each finding on one line: severity, where, what. The
  draft itself stays with you, as above; the findings are the user's only window into why the
  rounds continue.
- `forge.build.next` — which task was built, its status, and the files changed.
- `forge.review.code` — the verdict and the findings, each marked as kept or deferred once you
  have sorted them, with the reason on every deferral.
- `forge.review.fix` — the builder's status and the files changed.

This is narration, not a report: keep it short, never paste raw JSON, and never let it grow into
showing the plan drafts that the paragraph above keeps out of the chat.

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

## The code-review loop

After the last task, the loop is yours to run, exactly as with plan review: `forge.review.code`
runs one critic round against the approved plan and returns a critique, you decide what the builder
sees, and `forge.review.fix` hands it over. Repeat until the verdict is `approve` or the cap
refuses. The critic and the builder never talk directly — you are between them because you are the
only participant who knows what the plan deliberately left out.

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

Choose vendors and models in two steps, asking at most four questions total.

1. Resolve the vendors, then ask for the critic vendor and the builder vendor. Offer only vendors
   whose executables resolve on this machine using the same `ExecutableResolver` behavior as the
   server: `claude` and `codex` on `PATH`, and `cursor` when `cursor-agent` resolves on `PATH` or
   under `%LOCALAPPDATA%\cursor-agent`. Do not offer an unresolved vendor.
   - If none resolves, stop and tell the user which CLI to install (`claude`, `codex`, or
     `cursor-agent`); no act can run without one.
   - If exactly one resolves, do not ask either vendor question. Tell the user which vendor both
     roles will use and continue directly to step 2.
2. Ask one question for each role, requesting its model and effort together as a valid combination
   for that role's chosen vendor. The server publishes no catalogue, and `forge.begin` returns
   none, so the combinations come from the orchestrator's own knowledge of that vendor's current
   line-up. Offer three concrete model-plus-effort pairs, newest first, and always leave free text
   open. If the orchestrator does not know that vendor's current models, ask for free text with no
   options rather than offering stale ones. Never offer one vendor's model family under another
   vendor.

The catalogue is advisory: an unfamiliar model is worth mentioning, not refusing, because the
vendor CLI decides. The roles are not interchangeable in strength. The builder works against an
already-hardened plan and can be cheap; the critic is judging, so lean nearer the strong end.

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

Do not hand-edit anything under `.forge/`. Do not stage or commit the workers' changes; leave the
diff for the user to inspect.
