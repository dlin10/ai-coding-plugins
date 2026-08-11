---
name: find-files
description: "Locate files and folders by name anywhere on a Windows machine using the Everything index, instantly and without walking directories. Use when the user asks where a file is, where something is installed, or to find a file whose location is unknown — 'where is X', 'where does X live', 'find the file X', 'locate X', 'which folder holds X', 'где лежит X', 'где находится X', 'найди файл X' — or when a path outside the current project is in play (another repository, %APPDATA%, Program Files, a drive letter). Not for searching inside the current repository, and not for file contents."
---

# Find files anywhere

One MCP tool, `find_file_anywhere`, backed by voidtools [Everything](https://www.voidtools.com/) through its `es.exe` CLI. It queries a live index, so a whole-machine search by name returns in milliseconds with no directory walk.

In Claude Code the tool's full name is `mcp__find-files__find_file_anywhere`. If MCP tool schemas are deferred in this session, load it first with `ToolSearch select:mcp__find-files__find_file_anywhere`. Other hosts prefix MCP tool names differently — read the host's tool list rather than assuming that spelling.

## Use it for what is outside the project

This tool earns its place on one job: finding things whose location you do not know, **outside** the working tree. Another repository on disk, an installed program, a config under `%APPDATA%`, a file the user named without a path.

**Do not use it to search inside the current repository.** `Glob` and ripgrep respect `.gitignore`; the Everything index does not. Inside a repo it will hand you copies from `bin/`, `obj/`, `node_modules/`, and from sibling clones of the same project, and you will spend context sorting real hits from noise. Inside the repo, `Glob` is the better tool — not the slower one.

It matches **names and paths, never contents**. For contents use `Grep`. For semantic questions about C# symbols use the Roslyn MCP tools per the `roslyn-first` skill.

## Reading the result

Results come back as absolute paths, one per line, shallowest path first — the shortest path is usually the canonical copy, while backups, caches and build output sit deeper. Pass `recent: true` to rank by modification date instead, which is what you want for "the config I edited yesterday".

The last line is the part that matters most:

```
-- shown 20 of 1542 matches; narrow the query or raise the limit
```

**Never treat a truncated list as the whole answer.** If the total exceeds what was shown, the file you want may not be on screen: narrow the query, add `path`, or raise `limit`. When the totals match, the list is complete and you can choose from it with confidence.

## Scope with `path`, not with a query fragment

Pass `path` to restrict the search to a directory tree. It maps onto Everything's own `-path` filter and genuinely scopes the search, including the match total.

Matching is **substring-based on the file name**, so `SKILL.md` also matches `SKILL.md.diff` and `old_SKILL.md`. When you need the exact name, check the returned paths before acting on one.

## An empty result does not prove the file is absent

Everything only sees what its index covers. Folders excluded from indexing, unindexed network shares, and online-only OneDrive or Dropbox placeholders are invisible to it. The tool says so in its own output when it finds nothing, and the rule is absolute:

**Before telling the user a file does not exist, fall back to a filesystem walk (`Get-ChildItem -Recurse`) — and say which method you used.**

If Everything is not running, the tool reports that plainly instead of returning an empty list. That is not a failure to work around: use `Glob` or ripgrep for that search. Do not start Everything yourself.

## Complex queries

The `query` argument is Everything search syntax, not a literal string — wildcards, boolean operators, and filters such as `ext:`, `size:` and `dm:` all work, and they are the right way to express a complex condition. Read [references/everything-syntax.md](references/everything-syntax.md) before composing anything beyond a plain name.

## Running it by hand

The same binary has a CLI verb, useful for a quick check or when the MCP server is not connected:

```powershell
& "<plugin root>/bin/win-x64/esfind.exe" search "*.csproj" -path C:\Dev -limit 10
```

Exit codes: `0` matches, `1` nothing in the index, `2` bad usage, `3` Everything or its CLI unavailable.
