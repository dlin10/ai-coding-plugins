# Required Plan Forge workflow

Set `FORGE` to the absolute path of `scripts/forge.mjs`. Every command also
accepts `--cwd <repository>`.

## Phase 0 — doctor, agents, and state

1. Run `node "$FORGE" install-agents`.
2. If either managed agent was installed or updated, tell the user a new Codex
   task is required so the native spawn schema can refresh. Stop this run.
3. Run `node "$FORGE" doctor`.
4. If `doctor` exits non-zero, stop before changing workflow state. Report every
   failed check verbatim. A missing session capture means the prompt hook is not
   loaded; ask the user to submit a new prompt after confirming the hook loaded.
   Entries in `warnings` do not stop the run, but report them to the user in the
   same message that reports progress. A stale capture is one such warning.
5. Run `node "$FORGE" init` if this repository has no active forge state.

The installed native Codex schema uses `fork_turns`; use `fork_turns=none` for
reviewers and builders so model/effort can be passed
explicitly and the supplied evidence is the only inherited context.
Use native Codex sub-agents directly. Do not replace them with `codex exec` or
other nested Codex CLI subprocesses.

## Act 1 — grill and lock the plan

If a grilling skill is already available in the current session—for example
`grill-me`, `grilling`, or a similarly described skill—invoke it for this act.
Instruct it to stop once the decision tree is resolved and hand control back to
`$forge`. Do not run the embedded procedure in parallel with that skill.

If no grilling skill is available, follow this embedded procedure:

Interview the user relentlessly about every aspect of the plan until reaching a
shared understanding. Walk down each branch of the design tree, resolving
dependencies between decisions one by one. For each question, provide a
recommended answer. Ask questions one at a time and wait for the answer before
continuing. If a question can be answered by exploring the codebase, explore
the codebase instead of asking.

Only after the decision tree is resolved, write to the owned `PLAN.md` and
`PLAN-REVIEW-LOG.md`. Structure the plan with a one-paragraph Goal and numbered
`## Approach` steps. Each numbered step must be independently implementable and
verifiable.

Move to review with `set phase=review`.

## Act 2 — independent plan review

At the beginning of this act:

1. Run `node "$FORGE" models --native-models '<JSON>'`, where the JSON contains
   the model slugs and effort levels advertised by the current `spawn_agent`
   schema.
2. Do not ask the user for the orchestrator model or effort. The orchestrator
   is the current Codex session, and `configure-reviewer` derives it from session
   discovery. If its effort is not observable and the reviewer uses the same
   model, select the highest allowed reviewer effort.
3. Choose the reviewer model/effort. Reviewer priority must be numerically no
   greater than the current orchestrator priority. For the same model, reviewer
   effort must be at least the current effort. Unknown priority requires
   explicit user override and a reason. `ultra` is forbidden.
4. Save choices using:

   `configure-reviewer --reviewer-model <slug> --reviewer-effort <effort>`.

   If priority or fresh native-spawn availability is unconfirmed, add
   `--user-override --user-note "<the user's confirmation>"`. The reviewer
   choice is fixed for the run; later changes require the same explicit reason.
   A changed current session automatically replaces the orchestrator
   observation without requiring user confirmation.

For each round:

1. Call `dispatch --stage plan`.
2. Spawn a new `forge_reviewer` with a unique task name and the pinned reviewer
   model/effort. Supply this prompt, replacing the placeholders:

   > Adversarial plan review — round `{round}` of `{maxRounds}`.
   >
   > You have no memory of earlier rounds. First read
   > `PLAN-REVIEW-LOG.md` in full. Do not repeat resolved findings, and verify
   > that claimed fixes genuinely address earlier findings. Do not relitigate
   > an explicitly accepted decision unless new repository evidence invalidates
   > it.
   >
   > Then read `PLAN.md` and inspect any repository files needed to test its
   > assumptions. You are read-only. Actively try to falsify the plan, but do
   > not manufacture objections.
   >
   > Check for:
   >
   > - incorrect repository assumptions or infeasible steps;
   > - security, concurrency, race-condition, and data-integrity risks;
   > - API, schema, migration, compatibility, and rollback conflicts;
   > - missing failure handling, recovery, observability, or operational steps;
   > - unhandled edge cases and inadequate tests or verification;
   > - broken ordering, hidden dependencies, scope gaps, or unnecessary
   >   complexity where a materially safer simpler approach exists.
   >
   > `REVISE` only for concrete, evidence-backed problems material to
   > correctness, security, data integrity, feasibility, compatibility,
   > operability, or verification. Put optional improvements under
   > `Non-blocking suggestions`; approval with such suggestions is allowed.
   >
   > For each blocking finding provide:
   >
   > - `Evidence:` repository path, symbol, contract, or plan section;
   > - `Impact:` the concrete failure or risk;
   > - `Minimum fix:` the smallest adequate plan correction.
   >
   > If no blocking finding remains, approve. End with exactly one unformatted
   > line and nothing after it: `VERDICT: APPROVED` or `VERDICT: REVISE`.
3. Register the returned native agent id with
   `reviewer-session --id <agent-id> --dispatch-id <dispatch-id>
   --observed-model <reported-model> --observed-effort <reported-effort>`.
   Record the model and effort reported by the spawn result, not merely the
   requested values. Omit an observation flag when the result does not expose
   it. Reusing any reviewer id in the run is rejected.
4. Save its response to the returned critique file.
5. Call `verdict --stage plan --file <file>`.
6. On `REVISE`, remain the final arbiter. Accept findings supported by the plan
   and repository, reject unsupported findings, revise `PLAN.md`, and append:

   ```markdown
   ### Orchestrator response — Round <n>
   **Accepted:** <finding → change and reason>
   **Rejected:** <finding → reason>
   ```

   Then repeat with a fresh reviewer.

The initial cap is five rounds. At a capped `REVISE`, present the unresolved
findings and ask the user to choose:

1. One additional review round:
   `set maxRounds=<current+1> --user-note "<the user's exact words>"`.
2. Advance to sign-off despite the findings:
   `set phase=signoff --user-override --user-note "<accepted review risk>"`.
3. Stop the workflow: leave state unchanged and do not clean up automatically.

For an invalid or missing plan verdict, use `dispatch --stage plan --retry` up
to `maxVerdictRetries`. At that cap, ask the same three-way question:

1. One additional format retry:
   `set maxVerdictRetries=<current+1> --user-note "<the user's exact words>"`,
   then retry.
2. Advance to sign-off with the explicit risk override above.
3. Stop with the pending dispatch preserved.

Never infer a verdict from prose.
After approval, set `phase=signoff` and enter the user gate below.

## Sign-off — final user gate before code

Present:

- the final `PLAN.md`;
- three concise bullets describing what Act 1 grilling and Act 2 review changed;
- the number of completed review rounds;
- any remaining non-blocking suggestions.

Ask: “The plan has been reviewed and approved. Build it now?”

Only an explicit affirmative answer grants sign-off. An ambiguous or missing
answer leaves the run in `signoff`; do not choose a builder, lock the plan,
call `begin-build`, or write code. If the user declines and wants revisions,
run `set phase=review`, revise the plan, and send it through review again.

After an explicit yes, persist the user's exact words:

`confirm-signoff --user-note "<the user's exact affirmative words>"`.

The confirmation is bound to the current `PLAN.md` hash. If the plan changes,
present the revised plan and obtain a fresh confirmation.

## Act 3 — persistent builder

Act 3 begins only after `confirm-signoff` succeeds. Ask the user to choose the
builder model and effort as a single choice. Persist the answer with:

`configure-builder --builder-model <slug> --builder-effort <effort>`.

Then call `lock-plan` followed by `begin-build`.

If fresh native-spawn availability is unconfirmed, add
`--user-override --user-note "<the user's confirmation>"`. The builder choice
is then fixed for the run; changing it requires an explicit user reason.

`begin-build` fails until sign-off, builder choice, and the locked plan are
recorded. It then pins the initial HEAD and a tracked working-tree snapshot.
Spawn one `forge_builder` with the selected model/effort and retain its returned
agent id. Register it with `builder-session --id <agent-id>
--observed-model <reported-model> --observed-effort <reported-effort>`, using
the model and effort reported by the spawn result rather than the requested
values and omitting unavailable observations. For each locked plan step:

1. Call `dispatch --stage build --task <n>`.
2. Send that one step to the existing builder with `followup_task`.
3. Verify the result. Verification is the orchestrator's responsibility:
   `forge.mjs` does not run compilation, typechecking, or tests, and
   `complete --task` is only a procedural gate recording the orchestrator's
   verification decision. Run every verification command or check listed in
   the corresponding locked plan step. If that step lists none, inspect the
   repository tooling and run, at minimum, the applicable build and/or
   typecheck plus relevant tests. If the project or environment prevents an
   expected check from running, append the check, the reason it could not run,
   and the resulting verification gap to `PLAN-REVIEW-LOG.md`; do not count
   that check as successful. `dispatch --stage build --retry` permits up to
   three verification retries initially.
4. Call `complete --task <n>` only after verification succeeds.

At the verification cap, ask the user to choose:

1. One additional retry:
   `set maxBuildRetries=<current+1> --user-note "<the user's exact words>"`,
   then retry.
2. Accept the unverified result and advance:
   `complete --task <n> --user-override --user-note "<accepted risk>"`.
   Continue to the next plan step, or Act 4 when this was the final step.
3. Stop with the pending build dispatch preserved.

On resume, recover the live builder from the agent list when available;
otherwise spawn one replacement and record the recovery in the event log.

A material plan amendment enters `set phase=review --amendment`, receives
exactly one fresh reviewer round, and is relocked. Present an approved amended
plan to the user and run `confirm-signoff` again before returning to
`set phase=build`; every build dispatch rechecks that sign-off hash. `REVISE`
is returned to the user; do not iterate autonomously.

After all steps, set `phase=code-review` and call `prepare-review`.

## Act 4 — full review and fixes

Do not perform another reviewer selection. Reuse the exact reviewer model and
effort pinned at the beginning of Act 2 for every fresh Act 4 reviewer.

`prepare-review` emits two attributed tracked patches: pre-existing changes
between initial HEAD and the build-start snapshot, and in-run changes from that
snapshot to the current tree. It also inventories every current untracked file.
Safe text files up to 100 KB are included, subject to a 1 MiB aggregate budget
for untracked content. Files larger than 100 KB are inventoried without being
read and keep coverage partial. Tracked patches are not budgeted, so a very
large tracked change can produce evidence the reviewer cannot read in full.
Binary and secret-like files within the per-file bound are withheld until the
user explicitly allows their review with
`prepare-review --allow-files '<JSON array>' --user-note "<the user's consent>"`.
Allowances persist for later preparations. Pre-existing findings are always
shown and labeled.

Build a `{performanceBlock}` before spawning. If the changed-file summary
contains .NET code or another performance-sensitive path, make it require
performance analysis. When selected, performance review is required. For .NET
changes, if the `Analyzing Dotnet Performance` skill is available, instruct the
reviewer to invoke it; otherwise include the embedded checklist below. If no
changed path is performance-sensitive, set
`{performanceBlock}` to a single sentence saying no dedicated performance
block was selected; the normal correctness review still applies.

For each round, dispatch `code` and spawn a fresh reviewer with this prompt,
replacing the placeholders:

> Adversarial code review — fix round `{fixRound}` of `{maxFixRounds}`.
>
> {performanceBlock}
>
> Read, in order:
>
> 1. `PLAN.md` for the required implementation and accepted tradeoffs;
> 2. `PLAN-REVIEW-LOG.md` for settled decisions and earlier findings;
> 3. `.forge/review-manifest.json` for scope and withheld evidence;
> 4. `.forge/changed-files.txt` for the name-only scope summary;
> 5. `.forge/pre-existing.patch` and `.forge/in-run.patch` in full to attribute
>    tracked findings correctly;
> 6. `.forge/untracked-review.patch` for permitted untracked content.
>
> You may inspect any repository file needed for context. You are read-only.
> Actively try to falsify the implementation, but do not manufacture defects.
> Judge the implementation against the accepted plan, not against a different
> design you would have preferred. Do not relitigate accepted decisions unless
> new code evidence invalidates them.
>
> Look for concrete bugs, security or data-integrity defects, incomplete plan
> steps, unintended deviations, broken API/schema compatibility, concurrency
> failures, missing error handling, and broken or missing tests.
>
> When the performance instruction above requires the embedded review, inspect
> changed performance-sensitive paths for:
>
> - worse algorithmic complexity, repeated work, or unbounded growth;
> - avoidable hot-path allocations, excessive materialization, or GC pressure;
> - blocking async calls, lock contention, thread-pool starvation, or unsafe
>   parallelism;
> - N+1 operations, excessive database/network/file-system round trips, or
>   missed batching;
> - cache, pooling, buffering, and resource-lifetime regressions;
> - material startup, latency, throughput, or memory regressions.
>
> Tie each performance finding to changed code and a plausible workload. Prefer
> existing benchmarks, profiles, telemetry, or complexity evidence. Do not
> require speculative micro-optimizations or claim measured impact without
> measurements.
>
> For every blocking finding provide:
>
> - `Attribution:` `IN-RUN` or `PRE-EXISTING`;
> - `Evidence:` file and precise location;
> - `Impact:` the concrete failure or risk;
> - `Minimum fix:` the smallest adequate correction.
>
> `REVISE` only for evidence-backed material defects. Put optional improvements
> under `Non-blocking suggestions`; approval with such suggestions is allowed.
> Report pre-existing findings, but do not request their repair as part of this
> run unless the manifest records user authorization.
>
> Before the verdict, include exactly one line: `COVERAGE: FULL` if every
> supplied patch and permitted untracked file was reviewed, or
> `COVERAGE: PARTIAL — <reason>` naming what was not reviewed. Never approve
> partial coverage. End with exactly one unformatted line and nothing after it:
> `VERDICT: APPROVED` or `VERDICT: REVISE`.

Register the reviewer with `reviewer-session`, save the response, and consume
it with `verdict`.
`APPROVED` requires `COVERAGE: FULL`. On `REVISE`, remain the final arbiter,
record accepted and rejected findings with reasons in `PLAN-REVIEW-LOG.md`, and
send only accepted in-run findings to the persistent builder as a `fix`
dispatch.

Verify each fix. Its initial retry cap is `maxBuildRetries`. At that cap, ask
the user to authorize one additional retry by incrementing
`maxBuildRetries`, accept the unverified fix with
`complete --fix --user-override --user-note "<accepted risk>"`, or stop with
the pending dispatch preserved.

The initial fix-round cap is three. At a capped `REVISE`, ask the user to choose:

1. One additional fix round:
   `set maxFixRounds=<current+1> --user-note "<the user's exact words>"`.
   Then dispatch the accepted findings to the builder and run another review.
2. Advance and retain the findings:
   `set phase=done-with-findings --user-override --user-note "<accepted risk>"`.
3. Stop with the run unresolved.

For an invalid code-review verdict or coverage line, retry up to
`maxVerdictRetries`. At that cap, ask whether to increment
`maxVerdictRetries` by one and retry, advance to `done-with-findings` with the
explicit risk override, or stop with the pending dispatch preserved. Never
infer a verdict or treat partial coverage as approval.

## State and Git integrity

Use `forge.mjs` for every state transition. Never hand-edit
`.forge/state.json`, forge refs, or the managed exclude block. Reviewers and
builders must not commit or stage changes; the working tree is the handoff.

`status` returns a compact orchestration summary; use `status --full` only for
diagnosis, including `pendingFingerprintMatches`. `verifyCommands` is
orchestrator-owned advisory state set with
`set verifyCommands='<JSON array>'`; the CLI records it but does not execute it.
If a builder reports a merge/conflict condition for a pending task, use
`resolve-build --conflict` to clear that dispatch without advancing the task.
Dispatch never fails solely because of session-capture age; stale capture data
is reported and drift checks continue against the last observed session state.

## Cleanup

Always ask: “Delete the forge-owned `PLAN.md` and
`PLAN-REVIEW-LOG.md` files?” The default is no.

- No or no answer: `cleanup`
- Explicit yes: `cleanup --delete-artifacts`

The CLI always removes `.forge/`, managed exclude blocks, and pinned refs.
Artifact deletion is pair-safe and rechecks the ownership marker immediately
before deletion. A missing or replaced marker blocks artifact deletion without
blocking internal cleanup.

Managed global agents are shared by all repositories. Remove them only for an
explicit machine-wide uninstall request with `cleanup --purge-agents`; files
without the generated marker are preserved.
