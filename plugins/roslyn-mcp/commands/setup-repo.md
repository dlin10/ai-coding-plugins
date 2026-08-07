---
description: Wire the current repo to a per-repo Roslyn MCP port for Codex, Claude Code, and Cursor, with preflight checks before project or global configuration changes.
argument-hint: [port, e.g. 5051]
allowed-tools: Bash(git:*), Bash(claude mcp:*), Read, Write, Grep, Glob
---

# Set up Roslyn MCP for this repo

Port to use: `$ARGUMENTS`

The full procedure lives in the `roslyn-setup-repo` skill — it is the single source of truth for this action.

Read `${CLAUDE_PLUGIN_ROOT}/skills/roslyn-setup-repo/SKILL.md` and follow it exactly with the port above, reporting each step.
