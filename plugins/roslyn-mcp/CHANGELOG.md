# Changelog

## 3.0.0 - unreleased

- **A file is found by its exact path only.** A path that no document of the solution has fails with
  `DocumentNotFound`, which names the solution searched. Lookup used to fall back to a same-named file,
  so a path from a git worktree was answered with `requestSucceeded: true` and the symbols of the main
  checkout's file, on whatever branch that checkout was. The fallback also set the file scope of
  `roslyn_get_diagnostics`. Letter case and slash direction still do not matter; a relative path or a
  bare file name no longer resolves (`docs/adr/0003`).
- **Roslyn without Visual Studio.** `roslyn-mcp.exe mcp` is a stdio MCP server offering `roslyn_load(solution)` and
  the eleven tools, which it forwards to a per-user daemon. The daemon loads each solution once with MSBuildWorkspace and
  keeps it loaded across sessions. Every tool is annotated read-only (`docs/adr/0004`).
- **The solution is found from the agent's working directory.** `roslyn_load(directory)` uses the nearest
  `.roslynmcp.json` naming a `solution`, else the only solution in the nearest folder above, else the only marker below,
  and reports which in `foundBy`; anything else answers `needsChoice` with the candidates and loads nothing.
  `remember: true` saves the user's choice in a marker next to the solution, and a linked worktree reads the main
  tree's markers. A call naming a file of another solution switches the session to it and says so in `solutionSwitch`
  (`docs/adr/0005`).
- **Packages are restored when a project needs it.** Before the daemon loads a solution, it checks each C# and Visual
  Basic project the way the Roslyn language server does: the assets file named by the project's evaluation must exist and
  must resolve every package the project references. When a project fails the check, the daemon runs `dotnet restore` on
  the solution. `roslyn_load` reports the projects restored, the projects that failed with the reason, and the projects
  that name no assets file, which never trigger a restore. It sets `partial` while a restore has failed
  (`docs/adr/0006`).
- **Loaded solutions follow the disk and unload when unused.** Every call sees the files as they are on disk: a watcher
  queues the changes, each call applies them and checks the files it names directly, and an edit to a project file or
  a file it imports restores and reloads the projects it affects. The daemon unloads a solution after 2 hours without
  calls, `roslyn_unload` unloads one at once, and the daemon exits 30 minutes after it last held a solution
  (`docs/adr/0009`).
- **Installed from the marketplace.** The plugin declares the shim as the MCP server `roslyn-mcp`, which Codex lists
  under that name and Claude Code as `plugin:roslyn-mcp:roslyn-mcp`. On first start its launcher downloads the
  `roslyn-mcp.exe` of the plugin's version from the GitHub release into `%LOCALAPPDATA%\roslyn-mcp\bin`, and it
  retires versions nobody has started for a week. `3.0.0-preview.N` versions release the shim as a GitHub pre-release
  before the cutover, and the bundled VSIX stays 2.0.0 until then (`docs/adr/0008`).
- **The daemon outlives Claude Code sessions.** Claude Code ends an MCP server by killing its process tree, so the shim
  starts the daemon through `roslyn-mcp.exe start-daemon`, which exits at once and leaves the daemon outside that tree:
  the next session finds its solution loaded (`docs/adr/0010`).
