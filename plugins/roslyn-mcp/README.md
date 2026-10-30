# Roslyn MCP 3.0.0

Roslyn MCP gives **Codex**, **Claude Code**, and **Cursor** the compiler's view of a C# solution. The plugin declares one MCP server, `roslyn-mcp`, which forwards every tool to a per-user daemon that loads each solution with MSBuild and keeps it loaded across sessions. No repository needs configuring: the server finds the solution from the agent's working directory. Visual Studio is optional; with the bundled extension installed, a solution Visual Studio has open is answered by Visual Studio, unsaved editor buffers included.

It runs natively on Windows x64 and needs the .NET 10 SDK. WSL and non-Windows hosts are not supported. The optional extension supports Visual Studio 2022 and 2026.

## Why

Grep matches text; Roslyn MCP resolves C# symbols using the compiler's understanding of types, namespaces, overloads, inheritance, and project references. It knows which declaration a particular identifier refers to, so methods, properties, fields, parameters, and local variables that happen to share a name produce far fewer false matches. It can also follow interface implementations and overrides, resolve callers and references across projects, and return diagnostics from the active compilation instead of inferring relationships from matching strings.

Answers come from a full load of the solution: its actual target frameworks, references, preprocessor symbols, and language settings, with packages restored when a project needs it. The focused semantic results also reduce token usage: the agent receives the relevant definitions and references instead of reading and filtering large sets of textual matches. Grep remains useful for configuration files and broad text discovery, but Roslyn MCP is the reliable source for semantic claims such as who calls a method, what implements an interface, where a symbol is defined, or whether code may be unused.

## Install in Codex

```text
codex plugin marketplace add dlin10/ai-coding-plugins
codex plugin add roslyn-mcp@dlin10-ai-coding-plugins
```

## Install in Claude Code

```text
/plugin marketplace add dlin10/ai-coding-plugins
/plugin install roslyn-mcp@dlin10-ai-coding-plugins
```

## Install in Cursor

1. In the Cursor Dashboard, import `dlin10/ai-coding-plugins` as a team marketplace and enable it for the intended groups.
2. Install `roslyn-mcp` from Customize.

A repository configured for 2.x needs migrating once, as described below. For the optional Visual Studio extension, use `$roslyn-install-vsix` in Codex, `/roslyn-mcp:install` in Claude Code, or the `roslyn-install-vsix` skill in Cursor.

## How it works

The server is listed as `roslyn-mcp` in Codex and Cursor and as `plugin:roslyn-mcp:roslyn-mcp` in Claude Code. It is a stdio shim that forwards the tools to one daemon per Windows user and plugin version (`docs/adr/0004`). An agent calls `roslyn_load` first, with its working directory: the shim finds the solution from there and names it with how it found it, or lists the candidates when the choice is the user's, and `remember: true` records that choice next to the solution (`docs/adr/0005`). A file is found by its exact path only, so a Git worktree is answered from the worktree's files (`docs/adr/0003`).

The daemon restores packages for the projects that need it before it loads a solution (`docs/adr/0006`), follows edits on disk, unloads a solution after 2 hours without calls or when an agent calls `roslyn_unload`, and exits 30 minutes after it last held one (`docs/adr/0009`). It starts outside the shim's process tree, so a Claude Code session ending does not take it along (`docs/adr/0010`); under `codex exec` it ends with the turn.

On first start, `bin/roslyn-mcp-launcher.cmd` downloads the `roslyn-mcp.exe` of the plugin's version from the GitHub release `roslyn-mcp-v<version>` into `%LOCALAPPDATA%\roslyn-mcp\bin\<version>`, where the executable also unpacks itself. On every start the launcher retires the versions nobody has started for a week, except the two most recently used and any still running (`docs/adr/0008`).

## Visual Studio, optional

The bundled extension, `assets/RoslynMcpExtension.vsix`, starts no server and reads no port. Once Visual Studio has loaded a solution, it serves it on a named pipe to the current Windows user, and the daemon forwards the calls for exactly that solution file to it: Visual Studio answers from its workspace, unsaved editor buffers included, and `roslyn_load` reports `servedBy: visualStudio`. The same solution in a linked worktree, or any solution Visual Studio does not have open, the daemon loads itself. When Visual Studio closes the solution, the daemon loads it again (`docs/adr/0007`). The extension and the plugin must be the same version.

## Migrating from 2.x

Roslyn MCP 2.x served each solution from Visual Studio on a port of its own, which `roslyn-setup-repo` wrote into every client: `.roslynmcp.json` for the extension, `.codex/config.toml`, `.cursor/mcp.json`, a Claude Code local-scope entry, and helpers that copied the client files into new worktrees. In 3.0 those servers have nothing behind them once Visual Studio runs the 3.0 extension, and in Codex a project entry named `roslyn-mcp` takes the plugin server's place.

In each repository configured for 2.x, run `$roslyn-setup-repo` in Codex, `/roslyn-mcp:setup-repo` in Claude Code, or the `roslyn-setup-repo` skill in Cursor. It first checks that each installed client lists the plugin server, then shows everything it found and removes the repository's per-solution entries, in the main working tree and in every linked worktree. A `.roslynmcp.json` that names a `solution` is a discovery marker and keeps everything but its `port`. Roslyn entries in the global configuration (`~/.codex/config.toml`, Claude Code's user scope, `~/.cursor/mcp.json`) reach every repository, so it removes each one only after the user approves it by name. Start fresh sessions afterwards. Install the 3.0 extension last, once every repository whose solutions Visual Studio opens is migrated: until then a Visual Studio running a 2.x extension keeps serving its ports to the repositories not yet migrated.

A checkout of this repository carries the marketplace name `dlin10-ai-coding-plugins` in `.agents/plugins/marketplace.json`, and the Codex desktop app working in such a checkout has installed the plugin from it in place of the GitHub release: on 2026-10-30 the only installed `roslyn-mcp` was a checkout's 2.0.0, so no Codex session in any folder had the plugin server. When `codex mcp list` lacks `roslyn-mcp`, check the version under `%USERPROFILE%\.codex\plugins\cache\dlin10-ai-coding-plugins\roslyn-mcp\`, and reinstall from GitHub with `codex plugin add roslyn-mcp@dlin10-ai-coding-plugins` once the checkout's branch carries 3.0.

## Behavioral guidance

- **Codex and Claude Code:** strict Roslyn-first routing for semantic C# questions. If tools are not loaded in Codex, use tool discovery before falling back.
- **Cursor:** semantic search may discover conceptual candidates, but symbol-level claims must be verified with Roslyn MCP.
- **Every host:** call `roslyn_load` first, name files by their exact absolute paths, and call `roslyn_unload` at the end of a forge run or any other unattended run.
- **Hooks:** Codex and Claude receive non-blocking pre-tool reminders; Cursor receives a non-blocking post-tool reminder.

## MCP tools

| Tool | Purpose |
|------|---------|
| `roslyn_load` | Find the solution from the working directory, or take the one named, and make it the session's active solution; reports how it was found, the restore, and who serves it. |
| `roslyn_unload` | Unload a solution from the daemon at once. |
| `roslyn_validate_file` | Return compiler, nullable, warning, and optional analyzer diagnostics for one file. |
| `roslyn_get_diagnostics` | Return errors and warnings across the projects a change can have broken, or a project, or the solution, under a time budget that names what it skipped. |
| `roslyn_search_symbols` | Find source declarations by exact, substring, or camel-hump pattern; optionally referenced types and members by exact name. |
| `roslyn_find_references` | Find deduplicated references with containing symbols. |
| `roslyn_find_implementations` | Find implementations, derived types, and overrides. |
| `roslyn_find_callers` | Find direct and indirect callers. |
| `roslyn_go_to_definition` | Navigate to source definitions or metadata. |
| `roslyn_get_document_symbols` | List declarations in a C# file. |
| `roslyn_get_symbol_info` | Return symbol details and XML documentation. |
| `roslyn_describe_type` | Describe a type's API as the project sees it, NuGet and framework types included, with the extension methods in reach. |
| `roslyn_find_dead_code` | Conservatively identify potentially unused declarations. |

Every result that reports a symbol carries its `symbolId`, the documentation comment ID such as `M:Ns.Type.Method(System.Int32)`. The references, implementations, callers, symbol-info, and describe-type tools accept it in place of a file, line, and column, so a client can follow a chain of calls without looking positions up, and the ID survives edits that move the code. The vocabulary is in `CONTEXT.md` and the decisions behind it in `docs/adr/`.

## Contents

- `assets/RoslynMcpExtension.vsix` — bundled extension, the same version as the plugin, or for a preview the last release's.
- `.codex-plugin/`, `.claude-plugin/`, `.cursor-plugin/` — host manifests; `.mcp.json` — the plugin MCP server for Codex, which the Claude Code and Cursor manifests declare inline.
- `bin/` — the launcher that fetches `roslyn-mcp.exe` and its cache helper.
- `skills/` — Roslyn-first routing, migration from 2.x, and the extension's installation.
- `commands/` — thin Claude Code command shims over the canonical skills.
- `hooks/` — host-specific hook manifests and the shared PowerShell hook.
- `extension/` — vendored, buildable source of the extension, the shim and the daemon, with upstream MIT license.

## Rebuild the VSIX

The extension targets .NET Framework 4.8 and `roslyn-mcp.exe` targets .NET 10. Build with Visual Studio MSBuild:

```text
msbuild extension/src/RoslynMcpExtension.slnx /p:Configuration=Release /t:Rebuild /restore
```

The plugin and the extension share one version, written once as `<Version>` in `extension/src/Directory.Build.props`; the VSIX manifest, the package registration, the shim's `serverInfo` and the pipe names all take it from there. To release, change it there, in the three host manifests and in the title above, then replace `assets/RoslynMcpExtension.vsix` with the Release output. `npm run validate:plugins` fails while any of them differ. A `-preview.N` version keeps the VSIX of the last release, and the VSIX manifest takes the version without the suffix. Merging a new version to main releases `roslyn-mcp.exe` through `.github/workflows/release-roslyn-mcp.yml`.

## Validation

From the repository root:

```text
npm ci
npm run validate:plugins
npm run test:roslyn-hooks
npm run test:roslyn-launcher
```

To check a published `roslyn-mcp.exe` end to end, through the launcher on a one-project solution, run `pwsh plugins/roslyn-mcp/scripts/test-published-shim.ps1 -Executable <path to roslyn-mcp.exe>`.

CI validates all three marketplace surfaces, manifest and version consistency, hook contracts, the launcher, the extension build, the daemon's tests, the bundled VSIX version, the shim's package advisories, and a published `roslyn-mcp.exe` loading a solution through the launcher. `RoslynMcpExtension.Tests`, which covers the solution pipe inside the extension, needs the Visual Studio SDK and runs locally from the built solution rather than in CI.

## License

The repository is MIT licensed. The vendored extension retains its upstream MIT license in `extension/LICENSE`.

## Source-generator diagnostics

Visual Studio can retain stale output from source generators in Balanced mode. `roslyn_validate_file` reports the baseline IDE compilation and adds `sourceGeneratedDocumentCount`: zero means a completed observation found no generated documents in the validated file's project; absent or null means the observation failed or exceeded its 10-second budget. This count is not a freshness signal. Requesting it can execute generators, but does not replace the diagnostics already captured for this response.

An IDE build may help refresh the IDE cache, but does not guarantee immediate freshness: the experiment observed stale diagnostics even after a successful IDE build. `dotnet build` checks disk code and does not refresh that cache. Allow the workspace to update and validate again; persistence of the diagnostic still needs investigation. Automatic generator execution is a user-controlled VS option. Do not suppress genuine errors or assume every missing generated member is stale.

The issue-70 experiment completed three scored live Balanced A-to-B trials without reproducing stale diagnostics in that direction. Stale MemberA diagnostics were observed during B-to-A preparation after successful IDE builds. Those preparations were excluded from the scored trials, and the candidate sequence was not measured against their stale cache. The measured `GetSourceGeneratedDocumentsAsync` then `GetCompilationAsync` sequence on the same captured project therefore did not demonstrate a refresh fix. The not-reproduced label applies only to the scored direction. It does not establish whether the candidate can repair the observed preparation failure or the reported branch-switch issue.
