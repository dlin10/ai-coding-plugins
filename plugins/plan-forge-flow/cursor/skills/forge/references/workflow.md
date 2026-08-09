# Cursor workflow

## 1. Readiness and planning

1. Determine the actual Cursor client version, run `planforge run doctor --host cursor --workspace <workspace>`, and stop on a failed required check.
2. Generate a unique run ID and use the doctor-reported canonical workspace/scope identity. The actual client version must be 3.15.6 or newer.
3. Grill the request one question at a time until the plan is decision-complete.
4. Ask for the reviewer model and effort as free text, then obtain the per-run waiver described in [model-waiver.md](model-waiver.md).
5. Create the registered editable `.plan.md` with the exact contract from [native-plan-contract.md](native-plan-contract.md).
6. Stage it externally:

   `planforge plan stage --host cursor --workspace <workspace> --source <canonical-plan-path> --run-id <run-id> --model <reviewer-model> --effort <reviewer-effort> --cursor-version <actual-cursor-version> --observed-model <Auto|unavailable> --waiver-reason <consent>`

## 2. Review and approval

For each plan-review round, dispatch a fresh `forge-reviewer`. Pass the whole native plan and current review history. Pipe its complete bounded response to:

`planforge review record-response --host cursor --workspace <workspace> --dispatch-id <dispatch-id> --stage plan`

An approved response moves the pending run to `review-approved`; `REVISE` moves it to `revision-required`. After a revision, run `/forge resume`; the source is restaged and a fresh review is mandatory. At the review cap, ask the user to authorize exactly one additional round. Restage with the normal `plan stage` arguments plus `--accept-risk --authorization-note <bounded-note>`; the PendingRun records the one-round cap extension. Without that explicit authorization, abandon.

After approval, ask separately for the implementation builder model/effort and record the per-run waiver. Run:

`planforge plan finalize --host cursor --workspace <workspace> --run-id <run-id> --model <builder-model> --effort <builder-effort> --cursor-version <actual-cursor-version> --observed-model <Auto|unavailable> --waiver-reason <consent>`

Only a successful `ready` result permits telling the user that either local Build path is ready.

## 3. Local Build

The registered plan's preamble tells the current or new Agent to materialize first:

`planforge plan materialize --host cursor --workspace "<workspace>" --run-id "<run-id>"`

If materialization reports any path, marker, workspace, phase, hash, bounds, sensitivity, or conflict error, stop without repository writes and tell the user to invoke `/forge resume`. A consumed replay is success.

After materialization, use a fresh `forge-builder` for exactly one numbered step at a time. For task `N`, run `build dispatch --host cursor --workspace <workspace> --stage build --task-number N`, capture its dispatch ID, execute only that task, register the fresh builder with `session builder --host cursor --workspace <workspace> --id <fresh-builder-id> --dispatch-id <dispatch-id> --model <builder-model> --effort <builder-effort>`, and finish with `build complete --host cursor --workspace <workspace> --task-number N --dispatch-id <dispatch-id> --verification-passed true`. Use a new builder ID for every task, retry, or fix-list.

## 4. Final review and cleanup

Run `review prepare --host cursor --workspace <workspace> --full`, then `build dispatch --host cursor --workspace <workspace> --stage code`. Dispatch a fresh `forge-reviewer`, register it with `session reviewer --host cursor --workspace <workspace> --id <fresh-reviewer-id> --dispatch-id <dispatch-id> --model <reviewer-model> --effort <reviewer-effort>`, and pipe its complete response to `review record-response --host cursor --workspace <workspace> --dispatch-id <dispatch-id> --stage code`. The CLI-owned critique is `.forge/cursor-review-<dispatch-id>.md`; consume it with `review verdict --host cursor --workspace <workspace> --stage code --critique-file .forge/cursor-review-<dispatch-id>.md`. Approval requires `COVERAGE: FULL` and `VERDICT: APPROVED`.

For a requested fix round, run `build dispatch --host cursor --workspace <workspace> --stage fix-build`, send the accepted fix-list to a fresh builder, register that builder for the returned dispatch, and run `build complete --host cursor --workspace <workspace> --dispatch-id <dispatch-id> --verification-passed true`. Then prepare full evidence again, run `build dispatch --host cursor --workspace <workspace> --stage fix-review`, register a fresh reviewer, pipe its response to `review record-response --stage fix-review`, and consume `.forge/cursor-review-<dispatch-id>.md` with `review verdict --host cursor --workspace <workspace> --stage fix-review --critique-file .forge/cursor-review-<dispatch-id>.md`. Every command in this sequence includes `--host cursor --workspace <workspace>`.

Cleanup removes only matching owned `.forge` artifacts, scoped refs, and eligible shared exclude state. It never edits or deletes the Cursor-owned `.plan.md` file. `plan abandon` terminates only the matching external pending run.

## Resume

`/forge resume` must inspect the matching PendingRun and current canonical plan hash. Retain unchanged active/approved/ready state, restage a revised source, recover a matching materialization transaction, or report `consumed`/`abandoned`. If a materialization transaction has already started and the native plan changed, fail before further repository mutation and require the exact reviewed file to be restored before replay; `invalidate` and `abandon` are intentionally unavailable in that phase. Never silently switch hosts or guess among multiple runs.
