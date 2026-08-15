---
description: Harden, review, approve, and implement an editable native Cursor plan with Plan Forge Flow.
argument-hint: <change or plan to forge>
---

# Plan Forge Flow

Request: `$ARGUMENTS`

Read `${CURSOR_PLUGIN_ROOT}/cursor/skills/forge/SKILL.md` and every reference it requires, then follow that workflow exactly. This release supports only Windows x64; use `${CURSOR_PLUGIN_ROOT}/bin/planforge-launcher.ps1`. Always pass `--host cursor`.

If `$ARGUMENTS` is exactly `resume`, follow the `/forge resume` recovery branch. Otherwise follow the normal chat-first Plan flow. Creating the native plan is the terminal action, after staging, review, builder selection, and finalization have already succeeded.

If this chat is not currently in native Plan Mode, do not begin the workflow or modify files. Ask the user to press Shift+Tab to select Plan Mode and invoke `/forge` again.
