# Native Plan-mode UX contract

Acts 1–2 keep the complete plan, review log, counters, and model selections in
conversation. They do not write `.forge/`, `PLAN.md`, review logs, Git refs,
journals, or global agent files. After a proposed plan exists, a later prompt
may stage a temporary pending plan outside the repository; staging is not
approval or materialization.

## Free-text model selection

Do not run a Codex CLI model-listing command or any catalog command. Ask the reviewer and
builder separately for a free-text model/effort pair and follow
[model-selection.md](model-selection.md) for normalization, runtime
validation, aliases, typo handling, and the three-attempt cap. `ultra` is
always forbidden.

Choose the reviewer before the first fresh plan-review agent and reuse its exact
canonical pair in Act 4. Choose the builder only after the complete reviewed
plan preview is visible; spawn it in a no-edit hold state and retain the exact
pair for materialization and Act 3.

## Preview and approval

Emit the preview only when the current turn is in native Plan mode. If the
collaboration mode is Default or unknown, stop and ask the user to enter Plan
mode and resubmit the Forge prompt. Never infer collaboration mode from a hook
`permission_mode`, which describes approval behavior. Never emit
`<proposed_plan>` as plain text outside Plan mode because it does not create the
native approval widget.

Normalize the plan as UTF-8/LF text with one terminal newline and emit exactly:

```text
<proposed_plan>
{the canonical plan}
</proposed_plan>
```

Keep the review log, counters, and model selections in conversation. If the
plan changes, invalidate the previous preview and builder hold, then repeat the
full preview and builder selection.

## Native implementation

On a prompt after a Plan-mode proposed plan, the hook stages or refreshes the
latest plan as a temporary pending artifact. On the first Default-mode
implementation turn, run `plan materialize` with the review log, review
counters, and model selections on stdin before implementation.
