# Claude native Plan-mode contract

Claude Code uses its native Plan mode and `ExitPlanMode` as the approval
surface. Keep the canonical plan and review record in conversation until that
native approval. Synchronous workflow hooks arm Forge at skill entry and block
`ExitPlanMode` until review/finalize succeeds with the exact snapshot. The
separate agent-evidence hooks remain observational only.

Use a fresh `forge-reviewer-<effort>` agent for each review round and retain one
`forge-builder-<effort>` agent across implementation dispatches. The
orchestrator remains responsible for passing complete task context and for
checking the captured model/effort/result evidence before accepting a run.

After native approval, the first Default-mode action is manual materialization:

```text
planforge plan materialize --host claude --workspace <repo> --run-id <run> --plan-file <approved-file>
```

The file must be regular, non-symlink UTF-8 and exactly equal to the canonical
reviewed snapshot. The CLI performs this comparison before its repository lock,
pending begin, `.forge`, ref, or exclude writes. A plan or reviewer-selection
revision invalidates review evidence and the builder hold. A successful
materialize owns lock/build-begin and is the gate for resuming the persistent
builder. The pre-`ExitPlanMode` order and exact reviewed snapshot are enforced
for the armed Claude session; the native dialog still owns user approval.

Reviewer and builder providers are independent, supporting Anthropic/Anthropic,
Anthropic/OpenAI, OpenAI/Anthropic, and OpenAI/OpenAI. Codex App Server is
optional. Use the initial Claude doctor result before either role selection. An
absent Codex selects the Anthropic-only path automatically. An installed but
unusable Codex requires an explicit continue-without-Codex or stop-and-repair
decision; continuing disables OpenAI for both roles for the run. A ready Codex
enables independent provider-first selection. Never infer provider/model
validity merely from an agent filename or hook record.
