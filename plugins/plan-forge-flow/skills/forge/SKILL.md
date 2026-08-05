---
name: forge
description: Harden an implementation plan through one-question-at-a-time grilling, fresh independent reviews, native Plan-mode approval, a persistent builder, and a complete final code review.
---

# Plan Forge Flow

Use the bundled RID-aware launcher at `../../bin/planforge-launcher.sh` on Unix or
`../../bin/planforge-launcher.ps1` on Windows. It selects the matching
self-contained .NET executable from `bin/<rid>/`. Read [workflow.md](references/workflow.md),
[native-plan-ux.md](references/native-plan-ux.md), and
[model-selection.md](references/model-selection.md) before acting.

## Hard rules

- Ask grill questions one at a time and keep the complete canonical plan and
  review record in conversation until native approval.
- Act 1 and Act 2 do not create repository artifacts before native approval.
  Use only `run doctor`; never invoke a
  catalog command or launch Codex CLI to enumerate models.
- A pending-plan capture is created only after a Plan-mode `<proposed_plan>`
  when the next Default-mode turn begins, and is required only by
  `plan materialize`.
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
- In the first Default-mode turn, let the hook select that plan and run
  `planforge plan materialize --workspace <repo>` with the review metadata on
  stdin before implementation.
- Never stage or commit changes from agents. Never hand-edit `.forge/state.json`.
- A plan revision invalidates the previous preview and builder hold. Close the
  held builder, then repeat preview and free-text builder selection.
- At every cap, ask the user whether to retry once, accept the named risk, or
  stop. Do not extend caps autonomously.
- Pre-existing findings require separate opt-in. Cleanup always removes the
  current run's `.forge/` artifacts.
- Reviewers write the decision in `<critique-file>.json`; verdicts are not parsed
  from free-form critique text.

## Command boundary

Interactive commands emit one JSON success/error envelope. The hook command is
the protocol exception: `planforge hook capture-context` writes a native Codex
hook object at the JSON root or nothing and always exits zero for malformed or
unrelated input.
