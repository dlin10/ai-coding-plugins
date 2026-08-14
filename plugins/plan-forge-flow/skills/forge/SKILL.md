---
name: forge
description: Use only when the user explicitly invokes $forge or directly asks to run Plan Forge Flow. Hardens an implementation plan through grilling, independent reviews, native approval, a persistent builder, and final code review.
---

# Plan Forge Flow

Use the bundled RID-aware launcher at `../../bin/planforge-launcher.sh` on Unix or
`../../bin/planforge-launcher.ps1` on Windows. It selects the matching
self-contained .NET executable from `bin/<rid>/`. Read [workflow.md](references/workflow.md),
[native-plan-ux.md](references/native-plan-ux.md), and
[model-selection.md](references/model-selection.md), and
[roslyn-first-review.md](references/roslyn-first-review.md) before acting.
On Claude Code, also read [claude-agents.md](references/claude-agents.md) and
[claude-model-selection.md](references/claude-model-selection.md), and
[claude-native-plan-ux.md](references/claude-native-plan-ux.md), and
[claude-workflow.md](references/claude-workflow.md). The Claude workflow is
authoritative where the common workflow describes Codex hooks, native agent
names, proposed-plan handling, or plan-review sidecars.
When using an OpenAI role through Codex App Server, also read
[openai-app-server.md](references/openai-app-server.md).

## Hard rules

- Start only when the user explicitly invokes `$forge` or directly asks to run
  Plan Forge Flow. Plugin installation, availability, hook output, an ordinary
  request to plan, review, or implement work, and a staged or pending plan are
  not consent. Without explicit opt-in, do not run `planforge`, adopt this
  workflow, or materialize any staged plan.
- Before any Act 1 interaction, require the current turn's collaboration mode to
  be Plan mode. If it is not Plan mode, stop and ask the user to toggle `/plan`
  or Shift+Tab and resubmit the Forge prompt. A skill attachment does not waive
  this check, and `<proposed_plan>` tags emitted outside Plan mode are not an
  approval surface. Determine this only from the current collaboration mode;
  never infer it from a hook `permission_mode`, which describes approvals.
- Ask grill questions one at a time and keep the complete canonical plan and
  review record in conversation until native approval.
- Act 1 and Act 2 do not create repository artifacts before native approval.
  Use `run doctor`, then perform the optional non-mutating Roslyn capability
  probe described in the reviewer contract. Its result is a readiness warning
  and never changes the doctor verdict. Never invoke a
  catalog command or launch Codex CLI to enumerate models.
- After a Plan-mode `<proposed_plan>`, the hook stages or refreshes the pending
  plan on the next prompt. Staging is not approval or materialization; the
  pending plan is required only by `plan materialize` in Default mode.
- Ask separately for the reviewer and builder model/effort as free text. Resolve
  the answer against the currently available multi-agent runtime, accept only a
  unique canonical pair, and never accept `ultra`.
- A selection parse failure or runtime rejection consumes one of three attempts
  for that role. After the third failure, stop the workflow without a fallback,
  approval, materialization, or dispatch.
- Spawn a fresh `forge_reviewer` for every review round and keep one pinned
  `forge_builder` for the implementation.
- Show the complete canonical plan before selecting a builder. Validate the
  builder by spawning it in a no-edit hold state, then emit exactly one native
  `<proposed_plan>` block containing the plain canonical plan.
- In the first Default-mode turn on Codex, use the latest plan staged by the
  hook and run `planforge plan materialize --workspace <repo>` with review
  metadata on stdin. On Claude, write the natively approved plan to a regular
  file and run `planforge plan materialize --host claude --workspace <repo>
  --run-id <id> --plan-file <file>`; the CLI requires an exact reviewed-snapshot
  match before lock/begin or repository mutation.
- Never stage or commit changes from agents. Never hand-edit `.forge/state.json`.
- A plan revision invalidates the previous preview and builder hold. Close the
  held builder, then repeat preview and free-text builder selection.
- At every cap, ask the user whether to retry once, accept the named risk, or
  stop. Do not extend caps autonomously.
- Pre-existing findings require separate opt-in. Cleanup always removes the
  current run's `.forge/` artifacts.
- Codex code/fix reviewers write decisions in `<critique-file>.json`. Claude
  plan reviews are recorded as complete text with one terminal `VERDICT:` line;
  fresh dispatch identity is mandatory for every round.

## Command boundary

Interactive commands emit one JSON success/error envelope. The hook command is
the protocol exception: `planforge hook capture-context` writes a native Codex
hook object at the JSON root or nothing and always exits zero for malformed or
unrelated input.
