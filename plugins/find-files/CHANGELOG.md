# Changelog

## 0.1.0

Initial release.

- Single MCP tool `find_file_anywhere` over voidtools Everything, served by a self-contained `esfind.exe` (win-x64) that also exposes a `search` CLI verb.
- Results ranked shallowest path first, `recent` switches to modification date, capped at 20 by default with the true match total always reported.
- `path` scoping uses Everything's native `-path` filter, so the scope applies to the match total as well as to the listed rows.
- A stopped Everything service (exit code 8) is reported separately from a name absent from the index (exit code 9), and the empty-result message states that it does not prove absence.
- Hand-written JSON-RPC over stdio: `initialize`, `tools/list`, `tools/call`, `ping`, with a proper "method not found" for anything else and nothing but protocol on stdout.
- Skill `find-files` plus an Everything syntax reference, and MCP server declarations for Claude Code, Codex, and Cursor.
