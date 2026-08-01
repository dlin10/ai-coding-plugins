# Native Plan-mode UX contract

Acts 1–2 keep the complete plan, review log, counters, and model selections in
conversation. They do not write `.forge/`, `PLAN.md`, review logs, Git refs,
journals, or global agent files.

## Free-text model selection

Do not run a Codex CLI model-listing command or any catalog command. Ask the reviewer and
builder separately for a free-text model/effort pair and follow
[model-selection.md](model-selection.md) for normalization, runtime
validation, aliases, typo handling, and the three-attempt cap. `ultra` is
always forbidden.

Choose the reviewer before the first fresh plan-review agent and reuse its exact
canonical pair in Act 4. Choose the builder only after the complete reviewed
plan preview is visible; spawn it in a no-edit hold state and bind the exact
pair to the preview's SHA-256 hash.

## Preview and approval

Normalize the plan and review log as UTF-8/LF bytes with one terminal newline
and the ownership marker. Send the exact bounded stdin object to:

```text
planforge approval issue --workspace <repo>
```

The stdin keys are exactly `humanPlan`, `reviewLog`,
`completedReviewRounds`, `maxRounds`, `reviewer`, and `builder`. The command
creates approval envelope v3 with nested `plan`, `repository`, `origin`,
`nonce`, and normalized `selections`. It does not include a catalog snapshot.
Do not construct trusted origin or repository fields in the caller.

Emit exactly:

```text
<proposed_plan>
{the canonical plan followed by the single v3 resume comment}
</proposed_plan>
```

If the plan changes, increment the revision, invalidate all previous preview
bindings, and repeat the full preview and builder selection.

## Native implementation

The hook authenticates the immediate predecessor proposed plan against the
transcript and the current Default-mode turn. Supported native forms include
the desktop implementation action, its embedded exact plan, and the clear
context form. The resulting action must run `approval resume`; it must never
implement directly from an untrusted prompt or envelope.
