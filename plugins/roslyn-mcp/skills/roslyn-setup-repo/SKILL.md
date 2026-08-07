---
name: roslyn-setup-repo
description: Configure one native-Windows repository to use an isolated Roslyn MCP port from Codex, Claude Code, and Cursor. Writes project-local client configuration and removes conflicting global entries only after explicit approval.
---

# Set up Roslyn MCP for this repository

Use the integer supplied by the user as the port. Work from the current Git repository root. The supported environment is native Windows; WSL is outside this workflow.

## Preflight — do not write anything yet

Complete every preflight check before changing files or client settings:

1. Require an integer port in `1024..65535`. If it is missing or invalid, ask for one.
2. Scan sibling repositories for `.roslynmcp.json` files claiming the same port. If the developer uses other repository roots, ask which additional roots to scan. Stop on a collision.
3. Locate the intended `*.sln` or `*.slnx`. Warn if it is outside or above the repository because the extension searches upward from the solution directory for `.roslynmcp.json`.
4. Check whether `.codex/config.toml` is tracked with `git ls-files --error-unmatch .codex/config.toml`. If tracked, stop before all mutations and ask how the user wants to handle the developer-specific port. Do not modify the tracked file automatically.
5. Inspect, without changing them, for global/user `roslyn` or `roslyn-mcp` entries in:
   - Codex user config (`codex mcp list` and `codex mcp get <name>`);
   - Claude user scope, when the `claude` CLI is available;
   - `%USERPROFILE%\.cursor\mcp.json`.
6. If any global entries exist, show their exact names and locations and ask permission to remove them. If permission is declined, stop without partial setup because global entries can defeat per-repository isolation.

## Apply the project configuration

After preflight succeeds:

1. Write `.roslynmcp.json` at the repository root as exactly `{ "port": <port> }`.
2. Add `.roslynmcp.json`, `.codex/config.toml`, and `.cursor/` to `.git/info/exclude` if absent. Do not edit the committed `.gitignore`.
3. Create or merge `.codex/config.toml`, preserving all unrelated TOML settings:

```toml
[mcp_servers.roslyn-mcp]
url = "http://localhost:<port>/mcp"
```

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

5. When the `claude` CLI is available, replace only the local-scope entry:
   - `claude mcp remove roslyn-mcp -s local` when it already exists;
   - `claude mcp add --transport http --scope local roslyn-mcp http://localhost:<port>/mcp`;
   - verify with `claude mcp get roslyn-mcp` and confirm Local scope.
6. Remove only the previously displayed global/user entries for which the user approved removal. Use `codex mcp remove <name>` for Codex user entries, `claude mcp remove <name> -s user` for Claude entries, and a structure-preserving JSON merge for Cursor.

## Verify and report

- Confirm the ports in `.roslynmcp.json`, `.codex/config.toml`, `.cursor/mcp.json`, and Claude local scope are identical.
- Report any unavailable client that was skipped.
- Remind the user to open this repository's solution in Visual Studio and start fresh Codex, Claude Code, and Cursor sessions so each client reloads its project MCP configuration.
- For simultaneous repositories, require a different port and Visual Studio instance for each repository.
