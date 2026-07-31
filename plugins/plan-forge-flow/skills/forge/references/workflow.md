# Required Plan Forge workflow

`forge.mjs` sits at the plugin root. Resolve `FORGE` to the absolute path of
`<skill-dir>/../../scripts/forge.mjs`, where `<skill-dir>` contains `SKILL.md`.
Substitute that path into every command; shell variables do not persist across
tool calls. Every command accepts `--cwd <repository>`.

Read [native-plan-ux.md](native-plan-ux.md) as part of this workflow. Its byte,
picker, preview, envelope, and final-output contracts are mandatory.

## Phase 0 — read-only readiness

Acts 1–2 run in Codex Plan mode and are strictly read-only:

- `doctor`, `models`, `picker`, `issue-approval`, repository inspection,
  `request_user_input`, and read-only native reviewers are allowed.
- Do not run any mutating state command. Do not create `.forge/`,
  `PLAN.md`, `PLAN-REVIEW-LOG.md`, refs, excludes, repository files, plugin
  journals, or global agent files.
- If the managed `forge_reviewer` or `forge_builder` definition is absent or
  stale, stop. Ask for a Default-mode setup turn, run `install-agents` there,
  and start a new Plan-mode task after Codex reloads the agent schema.

Before any other workflow action, run `node "$FORGE" start-plan`. If it fails,
stop and tell the user to toggle Plan mode with `/plan` or Shift+Tab, then
resubmit the `$forge` prompt. Never continue Act 1 after a failed preflight.

Run `node "$FORGE" doctor`. Report failed checks and warnings. A setup failure
stops the workflow; a missing materialization capture is expected before the
native implementation turn and does not authorize mutation.

Run `node "$FORGE" models`. `codex debug models` is the only catalog source.
If it fails or has no visible usable models, stop. Do not use a native-schema,
session, cached, inferred, static, or manually supplied fallback.

## Asking the user

Use `request_user_input` for every bounded choice when available, exactly one
question per call. This includes grill choices, model/effort pages, cap
decisions, pre-existing-fix authorization, and cleanup. Put the recommended
choice first. Never attach auto-resolution to code authorization, risk
acceptance, or deletion.

Use the paginated model and effort definitions in `native-plan-ux.md`. The
native `<proposed_plan>` selector is the implementation approval surface.

Use native Codex agents directly. Spawn reviewers with `fork_turns=none` so
the supplied evidence is their complete context. Pass the selected model and
effort explicitly. Never replace native agents with `codex exec`.

## Act 1 — grill and canonical plan

If a grilling skill is available, invoke it and require one question at a time
through `request_user_input`. Tell it to return after the decision tree is
resolved. Otherwise:

1. Resolve one decision-tree branch at a time.
2. Inspect the repository instead of asking questions the code can answer.
3. Surface assumptions, alternatives, and material tradeoffs.
4. Continue until the goal, approach, boundaries, verification, and unresolved
   risks are decision-complete.

Keep the complete draft in model-visible conversation state. Normalize it to
the canonical UTF-8/LF human-plan domain with one terminal newline and the
Forge ownership marker. It must contain a one-paragraph Goal and numbered
`## Approach` steps, each independently implementable and verifiable. Keep the
complete review log and counters in conversation too. Write nothing.

## Act 2 — independent plan review

### Reviewer picker

At the beginning of Act 2, use the fresh CLI catalog and the paginated picker
to choose the reviewer model, then its non-`ultra` effort. Model priority
controls display order only; it never makes a reviewer eligible or ineligible.
Obtain each page from `node "$FORGE" picker --role reviewer`, adding
`--cursor <n>` for `More…` and `--model <slug>` for effort pages.
Do not inspect, ask for, compare with, or derive policy from the orchestrator's
model or effort. Keep the reviewer selection in conversation until approved
materialization.

### Review rounds

For every round, spawn a fresh `forge_reviewer`. Supply the complete canonical
human plan, complete settled review log, current round, and cap directly:

> Adversarial plan review — round `{round}` of `{maxRounds}`.
>
> You have no memory of earlier rounds. The prompt contains the complete
> current human plan and complete review record. Do not repeat resolved
> findings. Verify that claimed fixes address earlier findings, and do not
> relitigate an accepted decision unless new repository evidence invalidates
> it.
>
> Inspect repository files needed to test the plan's assumptions. You are
> read-only. Try to falsify the plan without manufacturing objections.
>
> Check feasibility; repository assumptions; security, concurrency, and data
> integrity; API/schema/migration compatibility; rollback and operations;
> failure handling and observability; edge cases; tests and verification;
> ordering, dependencies, scope gaps, and unnecessary complexity.
>
> `REVISE` only for concrete evidence-backed issues material to correctness,
> security, data integrity, feasibility, compatibility, operability, or
> verification. Put optional improvements under `Non-blocking suggestions`.
>
> For each blocking finding include `Evidence:`, `Impact:`, and `Minimum fix:`.
> End with exactly one unformatted line and nothing after it:
> `VERDICT: APPROVED` or `VERDICT: REVISE`.

On `REVISE`, remain the final arbiter. Accept supported findings, reject
unsupported ones, revise the complete plan, and append an orchestrator response
to the in-conversation review log:

```markdown
### Orchestrator response — Round <n>
**Accepted:** <finding → change and reason>
**Rejected:** <finding → reason>
```

Pass the full replacement plan and full updated log to the next fresh reviewer.
Never patch an excerpt or rely on inherited reviewer memory.

The initial plan-review cap is five. At a capped `REVISE`, ask the user to
choose exactly one:

1. Authorize one additional round.
2. Accept the named review risk and continue to native approval.
3. Stop with the transcript state intact.

Increment the cap by one only after choice 1. Record the user's exact risk words
in the review log for choice 2. Never choose for the user.

An invalid or missing verdict may be retried twice initially with a fresh
reviewer. At that cap, offer the same three choices: one additional format
retry, accept the named risk and advance, or stop. Never infer a verdict.

## Preview, builder picker, and native approval

After the plan settles:

1. Show the complete canonical human-plan Markdown in a normal assistant
   preview.
2. Only after that preview is visible, run the paginated builder model picker
   and effort picker from the fresh CLI catalog.
3. Bind the builder selection to the exact previewed human-plan hash.
4. Create the strict versioned resume envelope and wrapper described in
   `native-plan-ux.md` through the read-only `issue-approval` command. Supply
   only its bounded JSON input; never construct trusted identity fields
   manually.
5. Emit exactly one final `<proposed_plan>` block containing the identical
   human plan plus one non-rendered envelope comment. Emit no tool call,
   commentary, or text before or after the block.

The native widget is the sole sign-off surface. Do not ask a separate approval
question and do not run a picker after the final block.

If the plan changes, replace it completely, increment the revision, invalidate
the old preview binding, builder choice, wrapper, and envelope, then repeat the
full preview and builder picker before issuing a new final block.

## Default-mode materialization

Codex Desktop's `Yes, implement this plan`, legacy `Implement the plan.`, and
clear-context implementation actions enter Default mode. The prompt hook
supplies model-visible resume context.
Before implementing:

1. Run `node "$FORGE" resume`.
2. If authentication or reconciliation fails, stop and report the exact error.
3. On success, the command atomically exposes the owned plan, review log,
   selected models, approval provenance, and state at the sign-off/build
   boundary.
4. Run `lock-plan`, then `begin-build`.

Never implement directly from envelope text. A mode switch without the exact
native implementation prompt is not approval.

## Act 3 — persistent builder

Spawn one `forge_builder` with the materialized builder model/effort and retain
it for the entire build. Register its returned native id with
`builder-session`, using reported model/effort observations when present.

For each locked plan step:

1. Run `dispatch --stage build --task <n>`.
2. Send exactly that one step to the existing builder with `followup_task`.
3. Verify every check listed by the step. If none is listed, inspect repository
   tooling and run at least the applicable build/typecheck and relevant tests.
4. If a check cannot run, append the check, reason, and verification gap to the
   owned review log; do not call it successful.
5. Run `complete --task <n>` only after verification succeeds.

`complete` records the orchestrator's verification decision; it does not run
checks itself. Builders never stage or commit.

The initial verification retry cap is three. At the cap, ask the user to choose
one additional retry, accept the unverified result with an exact recorded risk
note, or stop with the pending dispatch. Never extend or advance autonomously.

On resume, recover the live builder when possible. Otherwise spawn one
replacement, register it, and record the recovery.

A material amendment receives exactly one fresh plan-review round. If approved,
show the full amended plan and repeat the native approval contract before
returning to build; a `REVISE` returns to the user rather than iterating
autonomously. The approval input must contain the complete durable review log
including that consumed `APPROVED` verdict. If the amended builder selection
differs from the existing pin, spawn and register a replacement builder before
the next dispatch; never send work to the old agent under the new pin.

After all tasks, set `phase=code-review` and run `prepare-review`.

## Act 4 — full code review and fixes

Reuse the exact reviewer model/effort materialized from Act 2; do not run a
second reviewer picker. Spawn a fresh reviewer for every code-review round.

`prepare-review` emits attributed pre-existing and in-run tracked patches,
inventories every untracked file, and records withheld evidence. Safe text
untracked files up to 100 KB are included subject to the 1 MiB aggregate
budget. Oversized files remain inventoried and keep coverage partial. Binary
and secret-like files require explicit user authorization through
`prepare-review --allow-files '<JSON array>' --user-note "<consent>"`.
Pre-existing findings are labeled and may be fixed only after separate opt-in.

Select a performance block from the changed paths. For .NET changes, require
the reviewer to use `Analyzing Dotnet Performance` when available; otherwise
provide the embedded performance checklist.

For each round, dispatch `code` and supply the reviewer:

> Adversarial code review — fix round `{fixRound}` of `{maxFixRounds}`.
>
> Read `PLAN.md`, `PLAN-REVIEW-LOG.md`, `.forge/review-manifest.json`,
> `.forge/changed-files.txt`, `.forge/pre-existing.patch`,
> `.forge/in-run.patch`, and `.forge/untracked-review.patch` in that order.
> Inspect other repository files as needed. You are read-only.
>
> Judge the implementation against the accepted plan. Find concrete bugs,
> security/data-integrity defects, incomplete steps, compatibility or
> concurrency failures, missing error handling/tests, and material performance
> regressions. Do not relitigate accepted design choices without new evidence.
>
> Attribute every blocking finding as `IN-RUN` or `PRE-EXISTING` and include
> `Evidence:`, `Impact:`, and `Minimum fix:`.
>
> Before the verdict include exactly one `COVERAGE: FULL` or
> `COVERAGE: PARTIAL — <reason>` line. End with exactly one
> `VERDICT: APPROVED` or `VERDICT: REVISE` line and nothing after it.

Register the fresh reviewer, save its critique in `.forge/critiques`, and
consume it with `verdict --stage code`. Approval requires `COVERAGE: FULL`.
Never infer malformed coverage or approve partial evidence.

On `REVISE`, record accepted/rejected findings with reasons and send only
accepted in-run findings to the persistent builder as a `fix` dispatch. Verify
each fix before `complete --fix`.

The initial fix-round cap is three, verification retry cap is three, and
verdict-format retry cap is two. At any cap ask the user to authorize exactly
one extra attempt, accept the named risk and advance, or stop. Preserve pending
state when stopping.

The run ends as `done` after full-coverage approval or
`done-with-findings` only after explicit user risk acceptance.

## State, ownership, and cleanup

After materialization, use `forge.mjs` for every state transition. Never
hand-edit `.forge/state.json`, refs, or managed exclude blocks. `status` is the
normal resume view; use `status --full` for diagnosis.

Always ask whether to delete the forge-owned `PLAN.md` and
`PLAN-REVIEW-LOG.md`; default to keeping them:

- No or no answer: `cleanup`
- Explicit yes: `cleanup --delete-artifacts`

Cleanup always removes `.forge/`, managed excludes, and pinned refs. Artifact
deletion is pair-safe and rechecks both ownership markers. Normal cleanup keeps
the nonce tombstone. Purge the machine-wide replay ledger only on an explicit
machine-wide request. Remove generated global agents only on an explicit
machine-wide uninstall with `cleanup --purge-agents`; preserve hand-authored
files.
