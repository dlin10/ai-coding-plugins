---
description: Wire each solution in the current repo to its own Roslyn MCP port for Codex, Claude Code, and Cursor, with preflight checks before project or global configuration changes.
argument-hint: [port per solution, e.g. 5051 or 5051,5052]
allowed-tools: Bash(git:*), Bash(claude mcp:*), Bash(codex mcp:*), Read, Write, Grep, Glob
---

# Set up Roslyn MCP for this repo

Ports to use: `$ARGUMENTS`

The full procedure lives in the `roslyn-setup-repo` skill — it is the single source of truth for this action.

Read `${CLAUDE_PLUGIN_ROOT}/skills/roslyn-setup-repo/SKILL.md` and follow it exactly with the ports above, reporting each step.

A port belongs to a solution, not to the repository. Enumerate the solutions first, as the skill directs, and ask for the missing ports when the repository holds more solutions than the arguments supply.
