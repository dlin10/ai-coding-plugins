# Cursor workflow

## 1. Chat planning and automatic review

1. Determine the actual Cursor client version and run `planforge run doctor --host cursor --workspace <workspace>`. Stop on any error, including an existing `.forge` target; do not clean up or migrate an earlier run automatically. The actual client version must be 3.15.6 or newer. Then follow [roslyn-first-review.md](roslyn-first-review.md) for the optional host-side capability probe; its readiness warning never changes the doctor verdict.
2. Generate a unique run ID and use the doctor-reported canonical workspace and scope identity.
3. Grill the request one question at a time until the plan is decision-complete.
4. Ask for the reviewer model and effort as free text, then obtain the per-run waiver described in [model-waiver.md](model-waiver.md).
5. Show the complete chat plan to the user. It must contain an exact `## Approach` section with numbered implementation tasks, but it does not yet contain the native marker or execution preamble.
6. Pipe that exact chat plan over stdin to:

   `planforge plan stage --host cursor --workspace <workspace> --run-id <run-id> --model <reviewer-model> --effort <reviewer-effort> --cursor-version <actual-cursor-version> --observed-model <Auto|unavailable> --waiver-reason <consent>`

7. Dispatch a fresh `forge-reviewer`. Pass the complete chat draft and current review history, then pipe its complete bounded response to:

   `planforge review record-response --host cursor --workspace <workspace> --dispatch-id <dispatch-id> --stage plan`

8. On `REVISE`, update and show the complete chat plan again, restage it through stdin, and automatically dispatch a new fresh reviewer. Never reuse a dispatch identity. Preserve the five-round cap; after it is reached, only the existing one-round `--accept-risk --authorization-note` authorization may extend review.
9. Continue automatically until the recorded response is `APPROVED`. Do not create or open a native plan during this loop.

## 2. Builder selection, finalization, and terminal native creation

1. Ask separately for the implementation builder model and effort and obtain the builder waiver from [model-waiver.md](model-waiver.md).
2. Run:

   `planforge plan finalize --host cursor --workspace <workspace> --run-id <run-id> --model <builder-model> --effort <builder-effort> --cursor-version <actual-cursor-version> --observed-model <Auto|unavailable> --waiver-reason <consent>`

3. Require a successful `ready` result. Finalization clears the chat draft from external pending state while retaining review responses and both model waivers.
4. Compose the native plan using the exact marker and preamble from [native-plan-contract.md](native-plan-contract.md). Create the registered editable native `.plan.md` now. Native creation is the terminal action of the normal Plan turn: do not run another command or claim a later state after Cursor opens it. The plan is already review-finalized and is immediately available for local Build.

## 3. `/forge resume` recovery

Use this branch only for an interrupted run; normal flow does not require resume.

1. Run `planforge run status --host cursor --workspace <workspace>` and inspect `data.pendingRun`; require one unambiguous matching run. `data.state` may coexist with it after materialization. Do not infer a run ID or choose a plan by recency.
2. Recover by phase:
   - `reviewing`: show the retained chat draft and dispatch a fresh reviewer, then continue the automatic review loop.
   - `revision-required`: update and show the retained chat draft, restage it through stdin, and dispatch a fresh reviewer. Preserve the review-cap authorization rules.
   - `review-approved`: ask for builder model/effort and waiver, finalize, then create the native plan as the terminal action.
   - `ready`: if the matching native plan already exists, report a successful no-op. If native creation was interrupted and the chat draft is no longer available, do not reconstruct or guess it; abandon the run and start a new `/forge`.
   - `materializing`: replay `plan materialize` for the exact run. `consumed` reports already materialized; `abandoned` requires a new run.
3. An absent pending run is not recoverable: start a new `/forge`. Do not create `.forge`, refs, or excludes while diagnosing recovery.

## 4. Local Build

The registered plan's preamble tells the current or new Agent to materialize first:

`planforge plan materialize --host cursor --workspace "<workspace>" --run-id "<run-id>"`

The first call discovers exactly one bounded, safe native plan containing the matching run/workspace marker and exact preamble, validates `## Approach`, and snapshots the current complete file into the transaction. It does not compare the native body with the reviewed chat draft. If discovery, validation, state, bounds, sensitivity, or conflict checks fail, stop without repository writes and start a new `/forge` or use recovery only when a pending phase supports it. Once the transaction exists, replay uses its snapshot even if the native file is edited or deleted. A consumed replay is success.

After materialization, use a fresh `forge-builder` for exactly one numbered step at a time. For task `N`, run `build dispatch --host cursor --workspace <workspace> --stage build --task-number N`, capture its dispatch ID, execute only that task, register the fresh builder with `session builder --host cursor --workspace <workspace> --id <fresh-builder-id> --dispatch-id <dispatch-id> --model <builder-model> --effort <builder-effort>`, and finish with `build complete --host cursor --workspace <workspace> --task-number N --dispatch-id <dispatch-id> --verification-passed true`. Use a new builder ID for every task, retry, or fix-list.

## 5. Final review and cleanup

Run `review prepare --host cursor --workspace <workspace> --full`, then `build dispatch --host cursor --workspace <workspace> --stage code`. Dispatch a fresh `forge-reviewer`, register it with `session reviewer --host cursor --workspace <workspace> --id <fresh-reviewer-id> --dispatch-id <dispatch-id> --model <reviewer-model> --effort <reviewer-effort>`, and pipe its complete response to `review record-response --host cursor --workspace <workspace> --dispatch-id <dispatch-id> --stage code`. The CLI-owned critique is `.forge/cursor-review-<dispatch-id>.md`; consume it with `review verdict --host cursor --workspace <workspace> --stage code --critique-file .forge/cursor-review-<dispatch-id>.md`. Approval requires `COVERAGE: FULL` and `VERDICT: APPROVED`.

For a requested fix round, run `build dispatch --host cursor --workspace <workspace> --stage fix-build`, send the accepted fix-list to a fresh builder, register that builder for the returned dispatch, and run `build complete --host cursor --workspace <workspace> --dispatch-id <dispatch-id> --verification-passed true`. Then prepare full evidence again, run `build dispatch --host cursor --workspace <workspace> --stage fix-review`, register a fresh reviewer, pipe its response to `review record-response --stage fix-review`, and consume `.forge/cursor-review-<dispatch-id>.md` with `review verdict --host cursor --workspace <workspace> --stage fix-review --critique-file .forge/cursor-review-<dispatch-id>.md`. Every command in this sequence includes `--host cursor --workspace <workspace>`.

Cleanup removes only matching owned `.forge` artifacts, scoped refs, and eligible shared exclude state. It never edits or deletes the Cursor-owned `.plan.md` file. `plan abandon` terminates only the matching external pending run.
