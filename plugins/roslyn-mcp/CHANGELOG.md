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
  keeps it loaded across sessions. Every tool is annotated read-only. Not yet declared in the plugin's `.mcp.json`
  (`docs/adr/0004`).
