# Roslyn MCP 0.7.1

Roslyn MCP packages a Visual Studio extension and agent guidance that expose the live Roslyn workspace to **Codex**, **Claude Code**, and **Cursor**. Each solution uses its own MCP port, so multiple Visual Studio instances can serve different solutions without cross-talk, whether those solutions live in one repository or in several.

This release supports clients running natively on Windows with Visual Studio 2022 or 2026. WSL and non-Windows hosts are not supported.

## Why

Grep matches text; Roslyn MCP resolves C# symbols using the compiler's understanding of types, namespaces, overloads, inheritance, and project references. It knows which declaration a particular identifier refers to, so methods, properties, fields, parameters, and local variables that happen to share a name produce far fewer false matches. It can also follow interface implementations and overrides, resolve callers and references across projects, and return diagnostics from the active compilation instead of inferring relationships from matching strings.

Because the server uses Visual Studio's live workspace, results include unsaved editor changes and the solution's actual target frameworks, references, preprocessor symbols, and language settings. The focused semantic results also reduce token usage: the agent receives the relevant definitions and references instead of reading and filtering large sets of textual matches. Grep remains useful for configuration files and broad text discovery, but Roslyn MCP is the reliable source for semantic claims such as who calls a method, what implements an interface, where a symbol is defined, or whether code may be unused.

## Install in Codex

```text
codex plugin marketplace add dlin10/ai-coding-plugins
codex plugin add roslyn-mcp@dlin10-ai-coding-plugins
```

Then ask Codex to use `$roslyn-install-vsix`. In each repository, ask it to use `$roslyn-setup-repo` with a distinct port, for example `5051`.

## Install in Claude Code

```text
/plugin marketplace add dlin10/ai-coding-plugins
/plugin install roslyn-mcp@dlin10-ai-coding-plugins
/roslyn-mcp:install
/roslyn-mcp:setup-repo 5051
```

## Install in Cursor

1. In the Cursor Dashboard, import `dlin10/ai-coding-plugins` as a team marketplace and enable it for the intended groups.
2. Install `roslyn-mcp` from Customize.
3. Invoke the `roslyn-install-vsix` skill.
4. Invoke `roslyn-setup-repo` with a distinct port, for example `5051`.

## Repository setup behavior

A port belongs to a solution rather than to a repository, because one Visual Studio instance serves one solution. A repository with several solutions therefore gets one port per solution.

`roslyn-setup-repo` performs a complete read-only preflight before it changes anything. It enumerates the solutions, validates a distinct port for each, scans for files already claiming those ports, rejects a tracked `.codex/config.toml`, and displays conflicting global MCP entries for approval before removal.

After preflight, it writes or merges the following in each solution's owning directory — the repository root when there is one solution, otherwise the component folder the extension reaches by searching upward from the solution:

- `.roslynmcp.json` for the Visual Studio extension;
- `.codex/config.toml` for Codex, carrying `default_tools_approval_mode = "approve"` and `omit_tools_from = ["deferred"]` beside the URL;
- `.cursor/mcp.json` for Cursor;
- a Claude Code local-scope MCP entry when the `claude` CLI is available.

Both extra Codex keys are load-bearing on Codex 0.149+. Without `omit_tools_from`, Codex parks MCP tools behind tool search and embedded `codex app-server` clients never see them; without `default_tools_approval_mode`, a session running with approval policy `never` in a read-only or workspace-write sandbox rejects every call.

Codex and Cursor read the port from the directory, so they follow the solution automatically. Claude Code keys local-scope servers by Git repository root instead, so a repository with several solutions gets one entry per solution under distinct names such as `roslyn-<solution-slug>`, and its tools are named `mcp__roslyn-<slug>__*`.

The three clients use `http://localhost:<port>/mcp`. Start fresh client sessions after setup so they reload the project configuration.

## Port resolution and server lifecycle

The extension walks upward from the solution directory and uses the nearest `.roslynmcp.json`. When it finds none, it falls back to the **Fallback Port** setting under **Tools ▸ Options ▸ Roslyn MCP Extension ▸ General**, which also carries **Server Name** and **Auto Start**. The setting applies only in that unconfigured case, which is what its name says.

Loading, closing, and reloading a solution — what a branch switch does — starts and stops the server through a single serialized queue, so a close-then-open burst keeps its order and a stale exit callback can never reach a newer run. Stopping asks the server to exit over its RPC channel so Kestrel releases the listening socket instead of being killed, which avoids the "port may already be in use" failures that used to follow a restart. Visual Studio shutdown keeps a fast path that terminates the child without waiting, because the UI thread must not block while the IDE exits.

## Behavioral guidance

- **Codex and Claude Code:** strict Roslyn-first routing for semantic C# questions. If tools are not loaded in Codex, use tool discovery before falling back.
- **Cursor:** semantic search may discover conceptual candidates, but symbol-level claims must be verified with Roslyn MCP.
- **Hooks:** Codex and Claude receive non-blocking pre-tool reminders; Cursor receives a non-blocking post-tool reminder.

## MCP tools

| Tool | Purpose |
|------|---------|
| `roslyn_validate_file` | Return compiler, nullable, warning, and optional analyzer diagnostics. |
| `roslyn_search_symbols` | Find source declarations by exact, substring, or camel-hump pattern. |
| `roslyn_find_references` | Find deduplicated references with containing symbols. |
| `roslyn_find_implementations` | Find implementations, derived types, and overrides. |
| `roslyn_find_callers` | Find direct and indirect callers. |
| `roslyn_go_to_definition` | Navigate to source definitions or metadata. |
| `roslyn_get_document_symbols` | List declarations in a C# file. |
| `roslyn_get_symbol_info` | Return symbol details and XML documentation. |
| `roslyn_find_dead_code` | Conservatively identify potentially unused declarations. |

## Contents

- `assets/RoslynMcpExtension.vsix` — bundled extension, **v1.7.0**.
- `.codex-plugin/`, `.claude-plugin/`, `.cursor-plugin/` — host manifests.
- `skills/` — installation, repository setup, and Roslyn-first routing.
- `commands/` — thin Claude Code command shims over the canonical skills.
- `hooks/` — host-specific hook manifests and the shared PowerShell hook.
- `extension/` — vendored, buildable extension source with upstream MIT license.

## Rebuild the VSIX

The extension targets .NET Framework 4.8 and the server targets .NET 10. Build with Visual Studio MSBuild:

```text
msbuild extension/src/RoslynMcpExtension.slnx /p:Configuration=Release /t:Rebuild /restore
```

When changing the extension, update its version consistently in `source.extension.vsixmanifest`, `RoslynMcpPackage.cs`, `Program.cs`, and the bundled-version statement above. Replace `assets/RoslynMcpExtension.vsix` with the Release output. Plugin manifest versions are a separate tri-host release version and must remain equal to one another.

## Validation

From the repository root:

```text
npm ci
npm run validate:plugins
npm run test:roslyn-hooks
```

CI validates all three marketplace surfaces, manifest and version consistency, hook contracts, the extension build, the standalone server tests, the bundled VSIX version, and standalone server package advisories. `RoslynMcpExtension.Tests`, which covers the server lifecycle inside the extension, needs the Visual Studio SDK and runs locally from the built solution rather than in CI.

## License

The repository is MIT licensed. The vendored extension retains its upstream MIT license in `extension/LICENSE`.
