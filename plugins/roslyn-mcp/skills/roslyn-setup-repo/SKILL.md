---
name: roslyn-setup-repo
description: Configure a native-Windows repository to reach Roslyn MCP from Codex, Claude Code, and Cursor on an isolated port per solution. Writes solution-local client configuration and removes conflicting global entries only after explicit approval.
---

# Set up Roslyn MCP for this repository

The unit of isolation is a **solution**, not a repository, because one Visual Studio instance serves one solution on one port. A repository holding a single solution needs one port; a repository holding several needs one port per solution. Work from the current Git repository root. The supported environment is native Windows; WSL is outside this workflow.

## Preflight — do not write anything yet

Complete every preflight check before changing files or client settings:

1. Enumerate every `*.sln` and `*.slnx` in the repository, ignoring the generated copies under `.vs\`. Show the list and confirm which solutions are in scope.
2. Require an integer port in `1024..65535` for each in-scope solution, and require the ports to differ from one another. If the user supplied fewer ports than solutions, or none, ask for the rest.
3. Scan for `.roslynmcp.json` files claiming any requested port. Search at every depth rather than only at repository roots, because a multi-solution repository keeps them in subdirectories. Ask which additional repository roots to scan if the developer uses others. Stop on a collision and name the file that already claims the port.
4. Warn about any solution that sits outside or above the repository, because the extension searches upward from the solution directory for `.roslynmcp.json`.
5. For each directory that will receive configuration, check whether `.codex/config.toml` is tracked with `git ls-files --error-unmatch <dir>/.codex/config.toml`. If tracked, stop before all mutations and ask how the user wants to handle the developer-specific port. Do not modify the tracked file automatically.
6. Inspect, without changing them, for global/user `roslyn` or `roslyn-mcp` entries in:
   - Codex user config (`codex mcp list` and `codex mcp get <name>`) — run it from outside the repository, or discount the project entries, because Codex merges configuration upward from the working directory;
   - Claude user scope, when the `claude` CLI is available;
   - `%USERPROFILE%\.cursor\mcp.json`.
7. If any global entries exist, show their exact names and locations and ask permission to remove them. If permission is declined, stop without partial setup because global entries can defeat per-solution isolation.

## Choose an owning directory for each solution

The extension walks upward from the solution directory and uses the nearest `.roslynmcp.json`, so every solution needs a configuration directory of its own:

- **One solution in the repository:** the repository root owns it.
- **Several solutions:** the owning directory is the nearest ancestor of the solution that is not also an ancestor of another solution — usually the component folder, such as `plugins\<name>\` for `plugins\<name>\src\X.sln`. Never use the repository root in a multi-solution repository, because it resolves every solution to one port.

Place all three files — `.roslynmcp.json`, `.codex/config.toml`, and `.cursor/mcp.json` — in that single owning directory. Codex merges project configuration upward from the working directory, so it resolves correctly whether the session starts in the solution folder or the component folder, and Cursor reads `.cursor/mcp.json` from the folder opened as the workspace, which is normally the component folder.

When per-solution directories are being written and a repository-root configuration already exists, remove the root `.roslynmcp.json`, `.codex/config.toml`, and `.cursor/mcp.json`. A surviving root entry competes with the solution-local one during the upward merge Codex performs.

## Apply the project configuration

After preflight succeeds, write the following for each solution, in its owning directory:

1. Write `.roslynmcp.json` as exactly `{ "port": <port> }`.
2. Ensure `.git/info/exclude` carries the unanchored patterns `.roslynmcp.json`, `.codex/`, `.cursor/`, and `.worktreeinclude`. A pattern containing a slash, such as `.codex/config.toml`, is anchored to the repository root and silently fails to cover nested directories, so replace it with `.codex/`. Before broadening it, confirm that no tracked files live under a directory named `.codex`; a sibling such as `.codex-plugin/` does not match the pattern and stays tracked. Do not edit the committed `.gitignore`. Confirm each written file with `git check-ignore`.
3. Create or merge `.codex/config.toml`, preserving all unrelated TOML settings:

```toml
[mcp_servers.roslyn-mcp]
url = "http://localhost:<port>/mcp"
default_tools_approval_mode = "approve"
omit_tools_from = ["deferred"]
```

   Both extra keys are load-bearing on Codex 0.149+ (verified against 0.149.0). `omit_tools_from = ["deferred"]` keeps the Roslyn tools in the model's direct toolset; without it Codex parks MCP tools behind tool search, and embedded non-interactive sessions — `codex app-server` clients such as Plan Forge Flow reviewers — never see them at all. `default_tools_approval_mode = "approve"` pre-approves the tools; without it a session running with approval policy `never` in a read-only or workspace-write sandbox rejects every call with "MCP tool call requires approval, but approval policy is never". Interactive sessions under `danger-full-access` work without either key, which is why the gap only shows up in embedded reviewers.

4. Create or merge `.cursor/mcp.json`, preserving all unrelated servers:

```json
{
  "mcpServers": {
    "roslyn-mcp": {
      "url": "http://localhost:<port>/mcp"
    }
  }
}
```

5. Create or merge `.worktreeinclude` in the **repository root** — this one file is per repository rather than per solution, because the Codex app reads it from the root when it creates a managed worktree. List the two client files of every owning directory as paths relative to the root, one per line, and append only the lines that are missing:

```
.codex/config.toml
.cursor/mcp.json
plugins/<name>/.codex/config.toml
plugins/<name>/.cursor/mcp.json
```

   The Codex app copies only the ignored files that match these patterns into the worktrees it creates. Do not list `.roslynmcp.json`: the extension already resolves it from the main working tree, and a copy inside a worktree would collide with the port scan in preflight step 3.

6. Create or merge `.cursor/worktrees.json` in each owning directory, preserving unrelated keys, and write `.cursor/setup-worktree-windows.ps1` beside it. Cursor looks for `worktrees.json` in the folder opened as the workspace when it creates a worktree, first inside the worktree and then in the main working tree, so an ignored file is found. The script runs inside the new worktree and copies the client files of every owning directory, so the whole worktree is configured whichever workspace created it:

```json
{
  "setup-worktree-windows": "setup-worktree-windows.ps1"
}
```

```powershell
# Written by roslyn-setup-repo. Cursor runs it inside a new worktree; it copies the
# developer-local MCP client configuration a worktree never receives, because every
# such file is untracked in the main working tree.
$ErrorActionPreference = 'Stop'
$owners = @('.', 'plugins\<name>')
$files = @('.codex\config.toml', '.cursor\mcp.json')

$worktree = [IO.Path]::GetFullPath((git rev-parse --show-toplevel).Trim())
$main = git worktree list --porcelain | Select-Object -First 1
$main = [IO.Path]::GetFullPath(($main -replace '^worktree ', '').Trim())
if ($main -eq $worktree) { Write-Host 'main working tree, nothing to copy'; exit 0 }

foreach ($owner in $owners) {
    foreach ($file in $files) {
        $source = Join-Path $main (Join-Path $owner $file)
        $target = Join-Path $worktree (Join-Path $owner $file)
        if (-not (Test-Path $source)) { Write-Host "missing: $source"; continue }
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        Copy-Item $source $target -Force
        Write-Host "copied: $target"
    }
}
```

   Fill `$owners` with the relative path of every owning directory; a single-solution repository has only `'.'`. The script takes both paths from git rather than from Cursor's `$ROOT_WORKTREE_PATH`, so it works whatever the working directory is, and it is a no-op when run in the main working tree. Overwrite an existing copy of the script, since it is generated. The exclude pattern `.cursor/` from step 2 already covers both files.

7. Configure Claude Code as described in the next section.
8. Remove only the previously displayed global/user entries for which the user approved removal. Use `codex mcp remove <name>` for Codex user entries, `claude mcp remove <name> -s user` for Claude entries, and a structure-preserving JSON merge for Cursor.

## Claude Code is keyed by repository, not by directory

Claude Code stores local-scope MCP servers under the **Git repository root** rather than the working directory. An entry added from a subdirectory lands under the repository key and remains visible from every directory in the repository, so a single server name cannot carry two ports. Confirm this on the machine at hand instead of assuming it: add a throwaway entry from a subdirectory, find which key it landed under in `%USERPROFILE%\.claude.json`, then remove it.

When the `claude` CLI is available:

- **One solution:** replace only the local-scope entry — `claude mcp remove roslyn-mcp -s local` when it already exists, then `claude mcp add --transport http --scope local roslyn-mcp http://localhost:<port>/mcp`. Verify with `claude mcp get roslyn-mcp` and confirm Local scope.
- **Several solutions:** register one entry per solution under distinct names such as `roslyn-<solution-slug>`, each pointing at its own port, and remove any shared `roslyn-mcp` entry left over from an earlier single-solution setup. The distinct names are required rather than cosmetic: a shared name serves only one solution, and two concurrent sessions in the same repository would read the same entry.

Explain what the multi-entry arrangement changes:

- Tool names become `mcp__roslyn-<slug>__*`, so the `mcp__roslyn-mcp__*` naming quoted in the `roslyn-first` skill no longer matches this repository. The grep-nudge hooks are unaffected because they match on prose rather than server names.
- Every entry is visible from every directory in the repository, so state which server belongs to which solution.
- Entries whose Visual Studio instance is not running report a connection failure when a session starts. That is expected rather than a fault.

## Git worktrees

Run this workflow in the repository's main working tree. Every file it writes is developer-local and untracked, so a linked worktree starts out with none of them, and `.git/info/exclude` is shared across worktrees, which means anything copied into one is already ignored there.

- **Visual Studio** needs nothing. When a worktree carries no `.roslynmcp.json`, the extension resolves the port from the same relative folder in the main working tree — bundled extension v1.8.0 and later.
- **Claude Code** needs nothing. Its local-scope entries are keyed by repository and resolve from a worktree as well; confirm on the machine at hand by listing the servers from inside a worktree.
- **Codex and Cursor** read configuration from the directory tree, so a worktree carries neither `.codex/config.toml` nor `.cursor/mcp.json`. Codex stops its upward config walk at the first `.git`, and a linked worktree has one at its root, so the main working tree's `.codex/config.toml` is never consulted from a worktree. Two files written in the apply steps cover the worktrees the clients create themselves:
  - Worktrees created by the **Codex app** receive both client files through `.worktreeinclude`. The app applies it only to its own managed worktrees, not to `git worktree add` and not to worktrees another client created.
  - Worktrees created by **Cursor** receive them through `.cursor/worktrees.json` and its setup script.
  - A worktree created any other way — `git worktree add`, or another client's worktree feature in which Codex or Cursor is then used — still needs the copy. Run `.cursor\setup-worktree-windows.ps1` from inside the worktree, or copy each file by hand, then verify with `codex mcp list` run from inside the worktree rather than assuming the copy took effect.

One port serves one Visual Studio instance, so a solution can be open in the main working tree or in one worktree, not both at once. When both are needed, give the worktree its own `.roslynmcp.json` on a port no other solution claims, and point that worktree's Codex, Cursor, and Claude configuration at the same port.

## Verify and report

- Report one row per solution: the solution path, the port, and the port recorded in `.roslynmcp.json`, `.codex/config.toml`, `.cursor/mcp.json`, and the Claude entry. All five must agree.
- Confirm the per-solution resolution rather than assuming it: run `codex mcp list` from each solution directory and check the URL it reports.
- Report any unavailable client that was skipped.
- Confirm with `git check-ignore` that `.worktreeinclude`, `.cursor/worktrees.json`, and `.cursor/setup-worktree-windows.ps1` are ignored, and state that the Codex app and Cursor worktree paths were verified against documentation only: creating a worktree from either client is the user's check.
- Remind the user to reopen each solution in Visual Studio so the extension reloads its port, and to start fresh Codex, Claude Code, and Cursor sessions so each client reloads its project MCP configuration.
- Serving several solutions at once requires a separate port and a separate Visual Studio instance for each, whether those solutions live in one repository or in several.
