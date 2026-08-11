# find-files

This plugin exposes one MCP tool, `find_file_anywhere`, backed by voidtools Everything.

Use it to locate files and folders **outside** the current project — other repositories, installed programs, `%APPDATA%`, any path you do not know. Do not use it inside the current repository: the Everything index ignores `.gitignore`, so it returns hits from `bin/`, `obj/`, `node_modules/` and sibling clones. Use `Glob` or ripgrep there. It matches names and paths, never file contents.

Read `skills/find-files/SKILL.md` for the full routing rules, and `skills/find-files/references/everything-syntax.md` before composing a query more complex than a plain name.

Two rules that matter regardless of which host you are running in:

- **A truncated result set is not the whole answer.** The last output line reports the true match total. If it exceeds what was shown, narrow the query or raise the limit before choosing a result.
- **An empty result does not prove the file is absent.** Excluded folders, unindexed network shares, and online-only cloud placeholders are invisible to the index. Fall back to a filesystem walk before telling the user a file does not exist, and say which method you used.

If Everything is not running, the tool reports that instead of returning nothing. Fall back to `Glob` or ripgrep; do not start Everything yourself.
