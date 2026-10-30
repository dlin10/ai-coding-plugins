---
name: roslyn-setup-repo
description: Migrate a native-Windows repository from the Roslyn MCP 2.x per-solution ports to the plugin's roslyn-mcp server, for Codex, Claude Code, and Cursor. Removes the repository's port configuration, and global Roslyn entries only after explicit approval.
---

# Migrate a repository to the Roslyn MCP plugin server

From 3.0, agents reach Roslyn through one MCP server the plugin declares: `roslyn-mcp` in Codex and Cursor, `plugin:roslyn-mcp:roslyn-mcp` in Claude Code. It finds the solution from the agent's working directory (`docs/adr/0005`), so a repository needs no configuration of its own. A repository set up for 2.x still carries a port per solution and a URL server per solution in each client. This workflow removes them, so that the plugin server is the only Roslyn entry the agents see. Run it from the repository's main working tree, on native Windows.

A **Roslyn URL entry** is an MCP server whose name starts with `roslyn` and whose URL is `http://localhost:<port>/mcp` or `http://127.0.0.1:<port>/mcp`, which is what 2.x wrote. Report any other Roslyn-named entry, such as one that runs a command, and leave it as it is.

## Preflight — change nothing yet

1. **The plugin server is there.** For each client installed: `codex mcp list`, run from outside any repository, lists `roslyn-mcp` with the command `cmd.exe /d /c .\bin\roslyn-mcp-launcher.cmd`, and `claude mcp list` lists `plugin:roslyn-mcp:roslyn-mcp`. Inside a repository a project entry named `roslyn-mcp` takes the plugin server's place in Codex, which then lists the entry's URL under that name. When a client lacks the plugin server, stop: removing the per-solution servers first leaves it without Roslyn. A Codex that lacks it often runs a plugin installed from a checkout of this marketplace; see the last section.
2. **Working trees.** List them with `git worktree list --porcelain`. The first is the main working tree; the others are linked worktrees, which may carry copies of the developer-local files.
3. **Project entries.** In every working tree, at any depth, skipping `.git`, `bin`, `obj`, `node_modules` and the linked worktrees nested in it, find:
   - `.roslynmcp.json` files that hold a `port`;
   - `.codex/config.toml` files with a Roslyn URL entry, under `[mcp_servers.<name>]`;
   - `.cursor/mcp.json` files with a Roslyn URL entry, under `mcpServers`;
   - in the repository root's `.worktreeinclude`, the lines naming those client files, and the `.cursor/setup-worktree-windows.ps1` scripts that begin with `# Written by roslyn-setup-repo`, with the `.cursor/worktrees.json` beside each.

   Then Claude Code's local-scope Roslyn URL entries: run `claude mcp list` from the main working tree, and `claude mcp get <name>` for each name starting with `roslyn`, which reports the scope.
4. **Tracked files.** Check each file found with `git ls-files --error-unmatch <path>`, and check the repository root's `.mcp.json`, Claude Code's project scope, for Roslyn URL entries. When any of these is tracked, stop before changing anything and ask the user how to handle it: a tracked file is shared with everyone who clones the repository, and this workflow edits only developer-local files.
5. **Global entries.** Find Roslyn URL entries, without changing them, in the places below. The two files hold other servers' credentials, so read them for Roslyn entries only: parse each one and print the name and URL of each server whose name starts with `roslyn`, never the whole file.
   - Codex's user config, `%USERPROFILE%\.codex\config.toml` (`$env:CODEX_HOME\config.toml` when that is set), under `[mcp_servers.<name>]`;
   - Claude Code's user scope, which `claude mcp get <name>` reports as `User config`;
   - Cursor's `%USERPROFILE%\.cursor\mcp.json`.
6. **Show the plan.** List everything steps 3 to 5 found, as project entries and global entries, with what will happen to each. Ask the user to approve the removal of each global entry by name. The project entries are what this workflow migrates and go without further approval. A global entry reaches every repository on the machine, so it goes only with the user's explicit approval, and a declined one stays.

## Remove the project entries

In every working tree where step 3 found them:

1. **`.roslynmcp.json`.** Remove the `port` key, and delete the file when nothing is left. A file that names a `solution` is a discovery marker, which records the user's choice of solution (`docs/adr/0005`): it stays, with every key but `port`.
2. **`.codex/config.toml`.** Remove each Roslyn URL entry's `[mcp_servers.<name>]` table, its subtables such as `[mcp_servers.<name>.tools.<tool>]` with it, and keep every unrelated setting. The keys 2.x wrote beside the URL, `default_tools_approval_mode` and `omit_tools_from`, belong to that table. Delete the file when nothing else is left in it, and the `.codex` folder when that leaves it empty.
3. **`.cursor/mcp.json`.** Remove each Roslyn URL entry from `mcpServers`, keeping every other server and key. Delete the file when nothing else is left in it, and the `.cursor` folder when that leaves it empty.
4. **Worktree helpers.** Remove from `.worktreeinclude` each line naming a client file that steps 2 and 3 deleted, and delete `.worktreeinclude` when no line is left. A `.cursor/setup-worktree-windows.ps1` written by `roslyn-setup-repo` copies the client files of the folders in its `$owners` into new worktrees. Once none of those files is left in the main working tree, delete the script and remove the `setup-worktree-windows` key from the `.cursor/worktrees.json` beside it, deleting that file when it is left empty. While any of them is left, keep both.
5. **Claude Code.** From the main working tree, run `claude mcp remove <name> -s local` for each local-scope Roslyn URL entry. Claude Code keys local scope by the repository root, so this covers the linked worktrees as well.

`.git/info/exclude` stays as it is: markers are untracked, and the patterns 2.x added keep ignoring whatever else the user keeps under `.codex/` and `.cursor/`.

## Remove the approved global entries

Remove only the global entries the user approved by name:

- Codex: `codex mcp remove <name>`, run from outside any repository so that no project config is in play.
- Claude Code: `claude mcp remove <name> -s user`.
- Cursor: remove the entry from `%USERPROFILE%\.cursor\mcp.json` with a structure-preserving JSON edit, keeping every other server.

## Verify and report

- From the main working tree, `codex mcp list` and `claude mcp list` show the plugin server and no other Roslyn URL entry, apart from declined global entries. Run `codex mcp list` once more from each folder that held a `.codex/config.toml`, because Codex merges project configuration from the working directory upward: `roslyn-mcp` must show the launcher command there, not a URL.
- No working tree has an empty `.codex` or `.cursor` folder left by the removals.
- Report every file and entry the preflight found, with its path or name and what became of it: removed, edited with the keys that remain, or kept, and why.
- Report each declined global entry as still reaching the agents in every repository, with the command that removes it.
- When the preflight found nothing, say that the repository was ready already.
- Ask the user to start fresh Codex, Claude Code and Cursor sessions, since a running session keeps the servers it started with.
- A repository holding several solutions needs no setup either. The first `roslyn_load` from a folder above them answers `needsChoice`; the user picks one, and `remember: true` records the choice as a marker next to that solution.
- Visual Studio is optional. Once every repository whose solutions Visual Studio opens is migrated, the 3.0 extension (`roslyn-install-vsix`) lets the daemon forward a solution Visual Studio has open to it, unsaved editor buffers included. The 3.0 extension serves no port, so install it only then; until then a Visual Studio running a 2.x extension keeps serving its ports to the repositories not yet migrated.

## A checkout of this marketplace

The Codex desktop app can install the plugin from a checkout of the `ai-coding-plugins` repository instead of from GitHub. The checkout's `.agents/plugins/marketplace.json` carries the same marketplace name, `dlin10-ai-coding-plugins`, and on 2026-10-30 the only copy in `%USERPROFILE%\.codex\plugins\cache\dlin10-ai-coding-plugins\roslyn-mcp\` was that checkout's 2.0.0, file for file with the checkout's timestamps, while GitHub offered a 3.0 preview. Every Codex session then ran 2.0.0, the CLI's in any folder included, and none had the plugin server. A checkout whose version has no release yet installs a launcher with nothing to download.

So when `codex mcp list` lacks `roslyn-mcp`, read the version folder there. When it is not the version on GitHub, bring the checkout's branch up to date, or reinstall from GitHub with `codex plugin add roslyn-mcp@dlin10-ai-coding-plugins` once no Codex desktop session works in that checkout.
