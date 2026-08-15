---
name: forge
description: Harden and implement changes through Cursor native Plan Mode, fresh advisory reviewers, explicit model waivers, and one-step fresh builders. Use when a Cursor user invokes /forge or asks to review and execute an editable native plan with Plan Forge Flow.
---

# Plan Forge Flow for Cursor

This release supports only Windows x64. Use the bundled launcher at `../../../bin/planforge-launcher.ps1` and always pass `--host cursor`. Read [workflow.md](references/workflow.md), [native-plan-contract.md](references/native-plan-contract.md), [model-waiver.md](references/model-waiver.md), and [roslyn-first-review.md](references/roslyn-first-review.md) completely before acting.

## Hard rules

- Start only when the user explicitly invokes `/forge` or directly asks to run
  Plan Forge Flow. Plugin installation, availability, an ordinary request to
  plan, review, or implement work, and staged or pending plan data are not
  consent. Without explicit opt-in, do not run `planforge`, adopt this workflow,
  or materialize any staged plan.
- Work only from native Cursor Plan Mode. If it is not active, stop and ask the user to press Shift+Tab and invoke `/forge` again.
- After ordinary `run doctor --host cursor`, perform the optional Roslyn
  capability probe. Report failures as readiness warnings without changing the
  doctor verdict or review coverage by themselves.
- Keep the reviewed plan in chat until review, builder selection, and `plan finalize` have succeeded. Native plan creation is the terminal action of the normal Plan turn; do not perform another action after creating it.
- Before local Build, preapproval commands may write only external `PendingRun` data. Do not create `.forge`, refs, or managed excludes.
- Spawn a fresh `forge-reviewer` for every review round. Its `readonly: true` flag and prompt are advisory, not a security boundary. A normal review that mutates the workspace stops the release and requires manual inspection.
- The reviewer must not mutate files, run writing shell commands, delegate, or change any local or external state. Capture its complete chat response through `review record-response`; the CLI owns critique and verdict evidence.
- Require explicit model-guarantee waiver consent on every Cursor run. Never describe the selected reviewer or builder model as guaranteed.
- Spawn a fresh `forge-builder` for every numbered implementation step or fix-list. Never reuse a Cursor builder.
- Review the complete chat plan before creating the native plan. The review log is advisory evidence and intentionally does not bind the later native file by path or hash; user edits to the native plan do not require another plan review.
- Run `plan finalize` successfully before telling the user that Build is ready.
- Normal flow does not require `/forge resume`. Use it only to recover an interrupted external pending run, and never guess a missing chat draft, run ID, or native plan.
- The Build preamble is advisory because Cursor can ignore it. When invoked, `plan materialize` must succeed before any repository write.
- Use neither hooks nor Canvas as an approval/security boundary. Cloud Build, Cursor amendments, cross-host resume, and pre-0.5 state are unsupported.

## Command boundary

Interactive commands emit one JSON success or error envelope. Exit codes are 0 for success, 1 for usage/environment errors, 2 for verdict failures, and 3 for state failures. Forward the complete reviewer response over stdin without rewriting its terminal verdict lines.
