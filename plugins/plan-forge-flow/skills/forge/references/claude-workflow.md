# Required Claude Code workflow

This reference is authoritative on Claude Code. It overrides the Codex-only
pending-plan hook, `forge_reviewer`/`forge_builder` spawn names, Codex
`<proposed_plan>` handling, and plan-review sidecar instructions in the common
workflow. Claude uses native Plan mode, `ExitPlanMode`, the twelve
`forge-reviewer-<effort>` / `forge-builder-<effort>` Agent definitions, and the
host-qualified commands below. Forge supersedes Claude's default planning
workflow. Synchronous entry
hooks arm the run, and a synchronous `PreToolUse:ExitPlanMode` hook enforces
review/finalize order plus exact reviewed-snapshot identity before native
approval can be shown.

Use the bundled launcher as `planforge` in the examples. Keep one safe run ID
for the planning transaction and retain every JSON envelope needed by the next
command.

## 0. Automatic activation gate

Direct `/plan-forge-flow:forge` expansion and model-invoked
`Skill(plan-forge-flow:forge)` both run the equivalent of this command before
the skill starts:

```text
planforge run begin --host claude --workspace <repo>
```

The command is idempotent for the current `CLAUDE_CODE_SESSION_ID` and returns
the random run ID injected by the hook. Use that exact ID for every planning
command. The activation is external under `${CLAUDE_PLUGIN_DATA}`; it creates no
repository artifact. An active run in another Claude session blocks a new Forge
start for the same workspace and names its run, session, and phase.

Never call `ExitPlanMode` before `plan finalize` returns `ready`. The gate denies
missing staging, reviewing, revision-required, and review-approved phases. At
`ready`, it normalizes Claude's injected `tool_input.plan` and permits only an
ordinal match with the reviewed `DraftText`; it returns no allow decision, so
Claude's normal approval dialog remains the sole consent surface. A session
without a Forge activation is unaffected.

Use `run status --host claude` to inspect the current activation. To close an
abandoned run before materialization:

```text
planforge run abandon --host claude --workspace <repo> --run-id <run>
```

Taking over a run from another session additionally requires `--accept-risk`
and a bounded `--authorization-note`. There is no TTL: `/clear` creates another
session ID, and stale ownership must be resolved explicitly.
Pre-0.6.2 unarmed pending state is unsupported and is not migrated or adopted;
remove that legacy external state before starting a new run.

## 1. Read-only readiness

In native Plan mode, run:

```text
planforge run doctor --host claude --workspace <repo>
```

This is the only allowed Codex discovery call. Doctor resolves native Codex
executables and Windows npm `.cmd`/`.bat` shims, initializes App Server,
validates its OpenAI/ChatGPT account, and reads every model catalog page. Handle
its `codex.status` before Act 1:

- `absent`: continue with Anthropic choices only.
- `unusable`: show the returned path, launch kind, stable error code, and full
  error. Ask whether to continue without Codex or stop and repair it. Continue
  disables OpenAI for reviewer and builder for the run; stop immediately with
  no artifacts and no model-selection attempt consumed.
- `ready`: retain the returned ordered catalog and use the provider-first model
  flow in `claude-model-selection.md` for both roles.

Do not run Codex or another model-listing command directly. Report other failed
checks. Then perform the optional host-side probe from
`roslyn-first-review.md`: verify the active Roslyn solution belongs to `<repo>`
before calling a named read-only Roslyn tool. This probe is non-mutating and
nonblocking; record unavailable, wrong-solution, or inconclusive results as a
warning and use the audited text fallback. It never changes the doctor verdict.
Do not create `.forge/`, a plan file, a Git ref, or another repository artifact.
The Claude doctor payload also reports the installed Claude Code capability.
Version 2.1.232 or newer is required for the activation/approval hooks and
persistent Anthropic builder resume;
an older, missing, or unparseable Claude installation fails the Anthropic
builder finalize boundary. It does not block an OpenAI builder.

When doctor reports Codex ready, choose reviewer and builder providers
independently. Anthropic roles use
`claude-model-selection.md` and the captured Agent evidence. OpenAI roles use an
exact model and advertised effort from Codex App Server as described in
`openai-app-server.md`. Session startup revalidates the doctor-selected pair;
an environmental failure after successful selection does not consume a model
selection attempt and never silently switches provider.

## 2. Stage and review the canonical plan

Resolve the decision tree one question at a time and keep the complete
canonical plan in conversation. For each round, invoke a **fresh**, read-only
reviewer:

- Anthropic: invoke `plan-forge-flow:forge-reviewer-<effort>` with the selected
  model alias (or omit model for `inherit`). Require valid hook evidence for the
  requested alias, resolved model, `modelsUsed`, effort, and fresh Agent ID.
- OpenAI: send the full review prompt on stdin to
  `planforge session start --host claude --workspace <repo> --role reviewer
  --model <exact-model> --effort <effort>`. Poll `session status` and consume
  `session result` with its returned session ID. The session creates a fresh
  read-only thread, audits it, and deletes it after the review.

Stage the exact canonical text on stdin with the reviewer's validated provider
evidence (the notation `<canonical-plan-on-stdin>` is not a file):

```text
planforge plan stage --host claude --workspace <repo> --run-id <run> \
  --provider <anthropic|openai> --requested-model <requested> \
  --resolved-model <resolved> --models-used '<JSON-string-array>' \
  --effort <effort> <canonical-plan-on-stdin>
```

For OpenAI, requested and resolved model must be identical and every
`modelsUsed` entry must be that model. For Anthropic, use the exact captured
evidence; do not infer it from the Agent filename. If execution evidence does
not match the staged selection, reject the response and invalidate or restage
instead of normalizing a model swap.

Feed the reviewer's complete response to the following command. Use its fresh
Agent ID or detached session ID as `<review-dispatch>`:

```text
planforge review record-response --host claude --workspace <repo> \
  --run-id <run> --dispatch-id <review-dispatch> --stage plan \
  <complete-review-response-on-stdin>
```

The response must contain exactly one valid `ROSLYN:` audit marker and end with
exactly one `VERDICT: APPROVED` or `VERDICT: REVISE` line. Never reuse a reviewer
identity. On `REVISE`, revise the canonical plan, invoke another fresh reviewer,
stage the new snapshot, and record the new response.

Any plan or reviewer-selection revision invalidates every response and the
builder hold. Staging different canonical text or selection performs this
invalidation. To record an explicit revision before restaging, run:

```text
planforge plan invalidate --host claude --workspace <repo> \
  --run-id <run> --reason <bounded-reason>
```

Close or delete the invalidated hold before continuing. The five-round cap and
retry/risk/stop decision remain the same as the common workflow.

## 3. Create the persistent builder hold and finalize

After plan approval, show the full canonical plan and resolve the builder's
provider selection. Create a no-edit, read-only hold that explicitly says it
must remain available for later dispatches:

- Anthropic: invoke `plan-forge-flow:forge-builder-<effort>` with the chosen
  alias (or omitted model for `inherit`), capture its Agent ID, and validate the
  requested/resolved/`modelsUsed`/effort hook evidence.
- OpenAI: send the hold prompt on stdin to
  `planforge session start --host claude --workspace <repo> --role builder-hold
  --model <exact-model> --effort <effort>`, then poll `session status` and read
  `session result`. Retain the returned Agent `threadId`; it is the persistent
  hold identity.

Finalize with that evidence and identity:

```text
planforge plan finalize --host claude --workspace <repo> --run-id <run> \
  --provider <anthropic|openai> --requested-model <requested> \
  --resolved-model <resolved> --models-used '<JSON-string-array>' \
  --effort <effort> --builder-hold-id <agent-or-thread-id>
```

Now present the exact canonical plan through Claude's native `ExitPlanMode`.
That native control is the sole approval surface. Agent evidence and `plan
finalize` are not approval.

## 4. Exact manual materialization

Only after native approval and entry into Default mode, write the approved
canonical text once to a regular, non-symlink UTF-8 file outside `<repo>`. The
first post-approval repository action is:

```text
planforge plan materialize --host claude --workspace <repo> \
  --run-id <run> --plan-file <approved-plan-file>
```

The file must byte-for-byte match the reviewed canonical snapshot. A mismatch
fails before `.forge/`, ref, exclude, or transaction writes. Materialization
owns the lock/build-begin transition; do not separately run `plan lock` or
`build begin`. After success, resume the one held builder. Never replace it for
an ordinary protocol, process, auth, network, timeout, rate-limit, cancellation,
context, tool, permission, model, or unknown failure. Replacement is allowed
only after confirmed terminal identity loss under the explicit replacement
contract.

Successful materialization removes the activation. An interrupted
materializing transaction retains enough state for recovery; successful replay
also removes the activation.

## 5. Dispatch every locked build task

For each numbered task, create the CLI dispatch first and retain its `id`:

```text
planforge build dispatch --host claude --workspace <repo> \
  --stage build --task-number <N>
```

Resume the same builder with only that locked task:

- Anthropic: call Claude Code's `SendMessage` tool with the exact held Agent ID,
  not its name, and the complete locked task as the message:

  ```json
  {"to":"<held-agent-id>","message":"<complete locked task and dispatch evidence>"}
  ```

  A completed subagent auto-resumes under the same ID. `SendMessage` does not
  require Agent Teams. See the official
  [resume-subagents contract](https://code.claude.com/docs/en/sub-agents#resume-subagents)
  and [tools reference](https://code.claude.com/docs/en/tools-reference).
- OpenAI: send the task on stdin to
  `planforge session start --host claude --workspace <repo> --role
  builder-resume --model <resolved-model> --effort <effort> --thread-id
  <held-thread-id>`, then poll status and read its result.

Register the persistent identity against the pending dispatch, using the pinned
resolved model and effort:

```text
planforge session builder --host claude --workspace <repo> \
  --id <held-agent-or-thread-id> --dispatch-id <dispatch-id> \
  --model <resolved-model> --effort <effort>
```

After the builder reports the exact changed files and verification, run:

```text
planforge build complete --host claude --workspace <repo> \
  --task-number <N> --dispatch-id <dispatch-id> --verification-passed true
```

Do not stage or commit. A failure remains pending and follows the bounded retry
rules; it does not authorize a replacement builder.

Detached App Server request, state, result, cancel-marker, and zero-content lock
files live in the external plugin-data session directory. The lock file may
persist after completion; it is coordination metadata, not a repository
artifact. `session status` reconciles a dead cancelling worker to an atomic
terminal result, and `session cancel` never overwrites a terminal state.

## 6. Full code review and bounded fixes

After the last build task enters code review, prepare exact evidence and create
a code-review dispatch:

```text
planforge review prepare --host claude --workspace <repo> --full
planforge build dispatch --host claude --workspace <repo> --stage code
```

Invoke a fresh reviewer using the same provider-specific mechanism as in step
2, this time with the review manifest and all prepared evidence. Register its
fresh Agent or detached session ID:

```text
planforge session reviewer --host claude --workspace <repo> \
  --id <fresh-reviewer-id> --dispatch-id <dispatch-id> \
  --model <resolved-model> --effort <effort>
```

Save the complete critique under `.forge/`. For this post-materialization CLI
verdict only, write the required adjacent decision JSON
`{"verdict":"APPROVED","coverage":"FULL"}` or
`{"verdict":"REVISE","coverage":"FULL"}` and consume it:

```text
planforge review verdict --host claude --workspace <repo> \
  --stage code --critique-file .forge/CODE-REVIEW-<N>.md
```

This decision file is not the Codex plan-review sidecar overridden above; it is
the host-neutral, post-materialization review-verdict contract.

For accepted in-run findings, return only that bounded list to the persistent
builder:

- Anthropic: use `SendMessage` again with
  `{"to":"<held-agent-id>","message":"<bounded accepted fix findings and dispatch evidence>"}`.
- OpenAI: use the same `session start --role builder-resume --thread-id
  <held-thread-id>` mechanism as a normal build dispatch.

```text
planforge build dispatch --host claude --workspace <repo> --stage fix-build
planforge session builder --host claude --workspace <repo> \
  --id <held-agent-or-thread-id> --dispatch-id <fix-build-id> \
  --model <resolved-model> --effort <effort>
planforge build complete --host claude --workspace <repo> \
  --dispatch-id <fix-build-id> --verification-passed true
planforge review prepare --host claude --workspace <repo> --full
planforge build dispatch --host claude --workspace <repo> --stage fix-review
```

Invoke and register a fresh reviewer for the `fix-review` dispatch, save its
critique and adjacent decision JSON, then run `review verdict --host claude
--workspace <repo> --stage fix-review --critique-file <file>`. Repeat only
within the fix-round cap. Pre-existing findings still require explicit user
authorization through `review authorize-preexisting`.

After a full approval, close the persistent builder and run:

```text
planforge run cleanup --host claude --workspace <repo>
```

Use `run cleanup --legacy` only as a separate, ownership-audited cleanup of
recognized 0.5.x artifacts.
