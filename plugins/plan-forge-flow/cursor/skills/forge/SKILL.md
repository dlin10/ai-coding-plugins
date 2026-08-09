---
name: forge
description: Harden and implement changes through Cursor native Plan Mode, fresh advisory reviewers, explicit model waivers, and one-step fresh builders. Use when a Cursor user invokes /forge or asks to review and execute an editable native plan with Plan Forge Flow.
---

# Plan Forge Flow for Cursor

Use the bundled launcher at `../../../bin/planforge-launcher.ps1` on Windows or `../../../bin/planforge-launcher.sh` on Unix. Always pass `--host cursor`. Read [workflow.md](references/workflow.md), [native-plan-contract.md](references/native-plan-contract.md), and [model-waiver.md](references/model-waiver.md) completely before acting.

## Hard rules

- Work only from native Cursor Plan Mode. If it is not active, stop and ask the user to press Shift+Tab and invoke `/forge` again.
- Before local Build, preapproval commands may write only external `PendingRun` data. Do not create `.forge`, refs, or managed excludes.
- Spawn a fresh `forge-reviewer` for every review round. Its `readonly: true` flag and prompt are advisory, not a security boundary. A normal review that mutates the workspace stops the release and requires manual inspection.
- The reviewer must not mutate files, run writing shell commands, delegate, or change any local or external state. Capture its complete chat response through `review record-response`; the CLI owns critique and verdict evidence.
- Require explicit model-guarantee waiver consent on every Cursor run. Never describe the selected reviewer or builder model as guaranteed.
- Spawn a fresh `forge-builder` for every numbered implementation step or fix-list. Never reuse a Cursor builder.
- Review and approve the entire registered native plan file, including its single marker and visible execution preamble. Any meaningful post-review edit invalidates approval.
- Run `plan finalize` successfully before telling the user that Build is ready.
- The Build preamble is advisory because Cursor can ignore it. When invoked, `plan materialize` must succeed before any repository write.
- Use neither hooks nor Canvas as an approval/security boundary. Cloud Build, Cursor amendments, cross-host resume, and pre-0.5 state are unsupported.

## Command boundary

Interactive commands emit one JSON success or error envelope. Exit codes are 0 for success, 1 for usage/environment errors, 2 for verdict failures, and 3 for state failures. Forward the complete reviewer response over stdin without rewriting its terminal verdict lines.
