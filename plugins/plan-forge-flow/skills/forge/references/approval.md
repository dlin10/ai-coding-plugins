# Forge reference: approval

Read it when the critique settles, before you show the user the plan.

## Approval

Nothing in the server asks the user anything. Showing them the plan and getting an answer is your
job, in five steps. Declare `phase: "approval"` before the first of them:

1. Call `forge.status` and read `driftedFiles` — the files that changed since `forge.begin`.
2. Show the user the **whole** plan, not a summary of it, and the drifted files beside it.
   - On the `Canvas` profile — the one `forge.begin` reported for a host that negotiated MCP Apps — call
     `forge.plan.show` without `plan`. It renders `PLAN.md` as a document, with the drift above it,
     in the host's own UI, and answers with its `planHash`. Do not paste the plan into the chat as
     well; the canvas is where they read it.
   - On the `Text` profile, show them `PLAN.md` the way your host displays best: an artifact, a
     widget, or plain markdown in the chat. Do not call `forge.plan.show` — nothing renders. The
     `planHash` is the one your last `forge.plan.write` or `forge.plan.patch` answered with; read
     `forge.plan.outline` when you no longer have it.

   Either way, if anything drifted, say so out loud rather than leaving it in a list they may not
   read. Beside the plan, say how many decisions the run has recorded — `decisions.count` from the
   same `forge.status` — not their text; the plan is what they approve, and `decisions.json` holds
   the rest if they ask. On a re-approval after tasks were built, read `run.tasksCompleted` from the same
   `forge.status` and tell them how many built tasks the approval keeps and which task the builder
   runs next. The server does not judge whether a change to the Requirements or Gates invalidates a
   built task; that is theirs to say.
3. Ask them to approve it or say what to change. Ask in the chat even when the canvas is up: it
   displays and nothing more, and it says so to the user. On a change, declare
   `phase: "plan_revision"`, revise with `forge.plan.patch` for some targets or `forge.plan.write`
   for a rewrite, and go back to `forge.plan.review` with `revision` saying what you changed — a
   plan amended after the last
   verdict has not been reviewed — then declare `phase: "approval"` again and show the revised plan
   before asking a second time.
4. With a yes, ask for what the gates need on this host: the value of every `$env:NAME` the plan's
   gates reference, one variable per question, each saying which gate reads it and what for.
   Collect them in the chat — never write a value into the plan, and never guess one.
5. Pass what they answered to `forge.plan.confirm`, with the `planHash` of the plan they saw, the
   variables as `gateEnvironment` and any final plan decisions under one `decisionBatchId`. `builderRoots` is accepted for
   compatibility and does not control access. Use `addressedByRevision` for IDs the final revision
   fixed, with `evidence` for a blocker or major, and `duplicateOf` for redundant IDs. The
   call refuses approval and lists every still-unresolved active plan ID. With a no answer, pass
   `approved: false` and no decisions; leave them for a later review or approved confirmation.
   Send the interview's `fullGate` on first approval. On subsequent confirmations omit it unless
   the user explicitly changes timing, including approval after a future plan revision. Read the
   saved `run.fullGate` from status as authoritative; the Brief preserves the initial choice.
   A timing-only change uses explicit reconfirm of the same plan, never `plan.write` or a
   rewritten Brief. It is allowed with pending fixes and does not verify them. With approved true,
   omitted `gateEnvironment`/`builderRoots` preserve saved settings, explicit values replace them,
   and empty values clear them. Successful confirm resets the global gate-failure brief for the
   next Builder while preserving attempt history. Approved false leaves PLAN/state/ledger and any
   existing approval unchanged. Both timing values are case-sensitive; empty, whitespace and
   unknown values are refused even with approved false. When the user says a built task has to be
   built again, pass it as `rebuildFrom` with the approval: the build goes back to that task, and
   the tasks before it stay built. After `forge.plan.confirm` with a yes, declare `phase: "build"`.

Never call `forge.plan.confirm` with an answer you did not get from the user. That call is the whole
of what approval means here: it approves `PLAN.md`, flips `approved` in the run state, and unlocks
the builder. No code anywhere checks whether anyone was actually asked. Approve the plan you showed
them: every revision goes through `forge.plan.patch` or `forge.plan.write`, so `PLAN.md` is that
plan, and its `planHash` refuses one that moved after they saw it. The confirmed text is what the
builder walks. Passing `plan` approves that text instead and writes it over `PLAN.md`; do not, unless
the user approved text that is not in `PLAN.md`.
