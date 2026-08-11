# find-files

Instant file and folder lookup by name on Windows, for Claude Code, Codex, and Cursor. Wraps voidtools [Everything](https://www.voidtools.com/) through its `es.exe` CLI and exposes it as a single MCP tool, `find_file_anywhere`.

The problem it solves is narrow on purpose: agents are poor at finding a file whose location they do not know. Inside a repository `Glob` and ripgrep are already good — they respect `.gitignore` and they are fast enough. Outside it, an agent either walks the disk for minutes or gives up and says the file does not exist. Everything answers the same question from a live index in milliseconds.

## Requirements

- Windows, x64.
- [Everything](https://www.voidtools.com/) installed and **running** (as a service or in the tray). If it is not running, the tool says so and tells the agent to fall back to `Glob`; it never starts Everything on your behalf.
- `es.exe`, which ships with Everything. Found via `ES_PATH`, then `PATH`, then `%ProgramFiles%\Everything\es.exe`.

No .NET runtime is required — the binary is self-contained.

## What the tool does

`find_file_anywhere` takes `query` plus optional `path`, `limit`, `recent`, `kind`, and `regex`. It returns absolute paths, one per line, and a summary line:

```
C:\Dev\ai-coding-plugins\plugins\find-files\skills\find-files\SKILL.md
C:\Dev\ai-coding-plugins\plugins\roslyn-mcp\skills\roslyn-first\SKILL.md
-- shown 2 of 1542 matches; narrow the query or raise the limit
```

Three behaviours are worth knowing, because each fixes a way that file search normally goes wrong:

- **Shallowest path first.** The shortest path is usually the canonical copy; caches, backups and build output sit deeper. `recent: true` ranks by modification date instead.
- **The true total is always reported.** Twenty paths with no total look like the complete answer. With `shown 20 of 1542`, a caller knows to narrow the query rather than pick from an arbitrary slice.
- **An empty result explains itself.** Everything sees only what its index covers, so "nothing found" is not proof of absence. The tool's own output says so and tells the caller to fall back to a filesystem walk, because tool output gets read every time while a skill file does not.

A stopped Everything service (`es.exe` exit code 8) is reported differently from a name that is genuinely missing from the index (exit code 9). Those two used to be indistinguishable, and conflating them is how an agent ends up reporting that a file does not exist.

## Command line

The same binary serves the MCP server and a CLI verb:

```powershell
& bin/win-x64/esfind.exe search "*.csproj" -path C:\Dev -limit 10
& bin/win-x64/esfind.exe search "settings.json" -recent
& bin/win-x64/esfind.exe mcp        # stdio MCP server; hosts launch this
& bin/win-x64/esfind.exe --version
```

Exit codes: `0` matches, `1` nothing in the index, `2` bad usage, `3` Everything or its CLI unavailable.

## Host wiring

Each host manifest declares the MCP server itself, so installing the plugin is all that is needed:

| Host | Manifest | Server declaration |
|---|---|---|
| Claude Code | `.claude-plugin/plugin.json` | inline, `${CLAUDE_PLUGIN_ROOT}` |
| Codex | `.codex-plugin/plugin.json` → `./.mcp.json` | relative command with `"cwd": "."` |
| Cursor | `.cursor-plugin/plugin.json` | inline, `${CURSOR_PLUGIN_ROOT}` |

Codex requires the explicit `"mcpServers": "./.mcp.json"` key; it does not discover the file on its own. All three manifests declare `"skills": "./skills/"` explicitly — Claude Code documents auto-discovery, and Cursor's own example plugins all declare the key rather than relying on it.

If a host fails to launch the server, the cause is almost always path resolution for the bundled executable. Check the host's MCP log, then set an absolute path in that host's manifest.

## Build

```powershell
./build/package.ps1
```

Publishes `bin/win-x64/esfind.exe` (self-contained, single file, trimmed), verifies the binary's `--version` against all three manifests, then runs the full test suite with `FINDFILES_REQUIRE_E2E=1` so the end-to-end transport test cannot silently skip.

Native AOT would produce a ~2 MB binary instead of ~13 MB and is a one-property change (`PublishAot`), but it needs the Visual Studio C++ workload for the MSVC linker. Since the server starts once per session, the size is the only real cost.

## What this is not for

- **Searching inside the current repository.** The index ignores `.gitignore`, so it returns hits from `bin/`, `obj/`, `node_modules/` and sibling clones. Use `Glob` or ripgrep there.
- **Searching file contents.** Everything matches names and paths. Use `Grep`. Everything's `content:` filter is not indexed and reads files on demand, which is slower than ripgrep and unbounded.
- **Non-Windows machines.** Everything is Windows-only.
