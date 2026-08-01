# Required Plan Forge workflow

The runtime is the fixed bundled `planforge` executable in `bin/`. Commands
use `--workspace <repository>`; when omitted they use the current directory.
All interactive output is one JSON envelope with stable exit codes: success 0,
usage/environment/unexpected 1, verdict 2, and state 3.

## Phase 0 — read-only readiness

In Plan mode:

1. Run `planforge plan start --workspace <repo>`.
2. Run `planforge run doctor --workspace <repo>` and report failed checks.

Do not mutate repository state in this phase. If generated agents are missing,
switch to Default mode, run `planforge agents install`, and start a fresh
Plan-mode task.

## Acts 1–2 — plan and review

Resolve the decision tree one question at a time. Keep the full canonical plan
and review log in conversation. Spawn a fresh read-only `forge_reviewer` for
each round, pass the complete evidence, and require exactly one final
`VERDICT: APPROVED` or `VERDICT: REVISE`. The initial plan-review cap is five;
invalid verdicts have two retries. At either cap ask the user to retry, accept
the named risk, or stop.

After review settles, show the complete plan, ask for the builder pair in free
text, and spawn the builder in a no-edit hold state to validate the runtime
selection. Then run `approval issue` with the bounded stdin contract. The native
widget is the sole approval surface.

## Default-mode materialization

The hook provides a bounded v2 session capture. In the authenticated Default
turn run:

```text
planforge approval resume --workspace <repo>
planforge plan lock --workspace <repo>
planforge build begin --workspace <repo>
```

For an approved amendment, use the same resume command, then relock and begin
the retained workflow with the amendment gates:

```text
planforge approval resume --workspace <repo>
planforge plan lock --workspace <repo> --relock --amendment
planforge build begin --workspace <repo> --amendment
```

Materialization rejects v1/v2 approvals, state with the removed catalog field,
foreign repositories, stale/malformed transcripts, replayed nonces, foreign
artifacts, symlinked targets, and unacknowledged v1 journals. Use
`--purge-replay-ledger` only for the explicit recovery decision.

## Act 3 — persistent builder

For each numbered locked task, dispatch exactly one task:

```text
planforge build dispatch --workspace <repo> --stage build --task-number N
planforge session builder --workspace <repo> --id <native-id> --dispatch-id <id> \
  --model <pinned-builder-model> --effort <pinned-builder-effort>
planforge build complete --workspace <repo> --task-number N
```

The retry cap is three. Verification failures stay pending unless the user
accepts the risk with a bounded `--authorization-note`.

## Act 4 — review and fixes

After all tasks, use `review prepare --full`, then dispatch fresh code reviewers. Save
each critique under `.forge/` and consume it with `review verdict --stage code
--critique-file <file>`. Require `COVERAGE: FULL` for approval. Pre-existing
paths require `review authorize-preexisting --authorized-paths '<JSON array>'
--authorization-note '<user consent>'`. Fix rounds are capped at three and
must return accepted in-run findings to the persistent builder. A fix round is
two dispatches: the pinned builder must register with `session builder`, pass
`build complete --fix`, and only then may the pinned reviewer register with
`session reviewer` using exact `--model` and `--effort` observations. A cap
extension is exactly one round through `run set --key max-fix-rounds --value N`
with `--accept-risk --authorization-note` after the cap is reached.

## Cleanup

Run `planforge run cleanup --workspace <repo>` after asking whether the user
wants owned plan artifacts deleted. Add `--delete-owned-artifacts` only after
an explicit yes. `--purge-generated-agents` and `--purge-replay-ledger` are
separate explicit machine-wide recovery operations.
