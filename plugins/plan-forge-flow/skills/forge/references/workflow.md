# Required Plan Forge workflow

The runtime is the bundled RID-aware `planforge` launcher in `bin/`. Use
`bin/planforge-launcher.sh` on Unix or `bin/planforge-launcher.ps1` on Windows;
the launcher selects the matching executable from `bin/<rid>/`. Commands use
`--workspace <repository>`; when omitted they use the current directory.
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
each round, pass the complete evidence, and require a critique plus a sidecar
JSON decision file. The initial plan-review cap is five;
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

Materialization consumes the generated wrapper for the current repository,
writes the two owned artifacts and state atomically under one workspace lock,
and rejects a repeated last-used nonce. There is no v1 journal recovery path.

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
each critique under `.forge/` and place its decision in `<critique-file>.json`:
`{"verdict":"APPROVED","coverage":"FULL"}` or
`{"verdict":"REVISE","coverage":"PARTIAL"}`. Consume it with
`review verdict --stage code --critique-file <file>`. Require `FULL` for approval. Pre-existing
paths require `review authorize-preexisting --authorized-paths '<JSON array>'
--authorization-note '<user consent>'`. Fix rounds are capped at three and
must return accepted in-run findings to the persistent builder. A fix round is
two explicit dispatches: `--stage fix-build` for the pinned builder, followed
by `--stage fix-review` for the pinned reviewer after `build complete`. A cap
extension is exactly one round through `run set --key max-fix-rounds --value N`
with `--accept-risk --authorization-note` after the cap is reached.

## Cleanup

Run `planforge run cleanup --workspace <repo>` after asking whether the user
wants owned plan artifacts deleted. Add `--delete-owned-artifacts` only after
an explicit yes. `--purge-generated-agents` is a separate explicit cleanup
operation.
