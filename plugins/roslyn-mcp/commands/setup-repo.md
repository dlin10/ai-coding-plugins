---
description: Migrate the current repo from Roslyn MCP 2.x per-solution ports to the plugin's roslyn-mcp server for Codex, Claude Code, and Cursor, removing global Roslyn entries only after approval.
allowed-tools: Bash(git:*), Bash(claude mcp:*), Bash(codex mcp:*), Read, Write, Edit, Grep, Glob
---

# Migrate this repo to the Roslyn MCP plugin server

The full procedure lives in the `roslyn-setup-repo` skill — it is the single source of truth for this action.

Read `${CLAUDE_PLUGIN_ROOT}/skills/roslyn-setup-repo/SKILL.md` and follow it exactly, reporting each step.
