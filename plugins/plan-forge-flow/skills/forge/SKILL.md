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
| `forge.review.code` | Once, after the last task. Runs the whole critic-to-builder loop internally. |
| `forge.status` | Before asking for approval, and any time the user asks where things stand. Carries the drift. |

Every tool takes `workspaceRoot` and, after `forge.begin`, `runId`. The work tools also take
`model`, an optional `effort`, and an optional `vendor` — `claude`, `codex`, or `cursor`,
defaulting to `claude`.

## Act 1: the interview

Ask grilling questions **one at a time** and wait for each answer. You are looking for the decisions
the plan would otherwise leave to whoever implements it: what is out of scope, what happens on the
error paths, what existing behaviour must not change, how the result will be verified.

Write the plan as markdown with a numbered task list — one task per numbered item under a heading.
That numbering is what `forge.build.next` walks, so a task that is really three tasks will be built
as one.

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

## Choosing the vendor and model

Ask the user for the critic and builder models as free text, and pass what they say. The catalogue
is advisory: an unfamiliar model is worth mentioning, not refusing, because the vendor CLI decides.

The roles are not interchangeable in strength. The builder works against an already-hardened plan
and can be cheap; the critic is judging, so lean nearer the strong end.

## What is not enforced

Nothing stops you from abandoning a run halfway, or from editing code during Act 1. There are no
hooks and no gates in this version — the trade is deliberate. Two consequences to hold yourself to:

- Do not touch files before the plan is approved. `forge.status` compares the working tree against
  the baseline, so what you touched is visible — but only because you went and looked.
- Do not stop mid-run without telling the user where you stopped and what remains.

Do not hand-edit anything under `.forge/`. Do not stage or commit the workers' changes; leave the
diff for the user to inspect.
