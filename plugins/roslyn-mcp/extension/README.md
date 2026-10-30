# Roslyn MCP Extension — Visual Studio Extension

A Visual Studio extension that serves **semantic C# code analysis** from the **live Roslyn workspace** inside Visual Studio to the Roslyn MCP plugin's daemon, which offers it to agents over the [Model Context Protocol (MCP)](https://modelcontextprotocol.io/). It is optional: without it, the daemon loads every solution itself.

This extension was inspired by [Roslyn MCP Extension](https://github.com/sailro/RoslynMcpExtension) by Sebastien Lebreton.

The daemon loads a solution with MSBuild and answers from the files on disk. When Visual Studio has the same solution file open, the daemon forwards its calls to this extension instead, which answers from Visual Studio's own `VisualStudioWorkspace` — unsaved changes, live diagnostics, and the full compilation state that VS already maintains — without loading the solution a second time.

## MCP Tools

| Tool | Description |
|------|-------------|
| `roslyn_validate_file` | Compiler errors, warnings, and optional analyzer diagnostics for a C# file |
| `roslyn_find_references` | Find deduplicated, ordered references with fully-qualified containing symbols |
| `roslyn_find_implementations` | Find implementations, derived types/interfaces, and overrides |
| `roslyn_find_callers` | Find direct and indirect calling members and their call sites |
| `roslyn_go_to_definition` | Navigate to a symbol's definition |
| `roslyn_get_document_symbols` | List all symbols in a file with types, modifiers, and line spans |
| `roslyn_search_symbols` | Search source declarations by exact, substring, or camel-hump pattern |
| `roslyn_find_dead_code` | Find potentially unused types, methods, and fields |
| `roslyn_get_symbol_info` | Get detailed information and documentation for a symbol |

Position-based responses also identify the active compilation: project/target, assembly, C# language version, preprocessor defines, and document-scoped error count.

Reference and caller locations additionally carry `enclosingStartLine` and `enclosingEndLine`, the declaration span of the member containing them, so the surrounding source can be read directly without a separate symbol listing.

## Prerequisites

- Native Windows with Visual Studio 2022 or later
- The Roslyn MCP plugin, whose `roslyn-mcp.exe` daemon the agents talk to
- .NET 10 SDK when building the extension from source

## Building

The solution file is at `src/RoslynMcpExtension.slnx`. Since the VSIX project requires MSBuild, build via Visual Studio or `msbuild`:

```bash
# Full solution (requires Visual Studio / MSBuild)
msbuild src\RoslynMcpExtension.slnx /p:Configuration=Release /t:Rebuild /restore

# The daemon and its shim only (dotnet CLI)
dotnet build src\RoslynMcpExtension.Cli\RoslynMcpExtension.Cli.csproj
```

## Installation

Marketplace users should follow the plugin-level [installation instructions](../README.md), which install the bundled VSIX.

For a source build:

1. Build the solution in Release mode.
2. Install `src/RoslynMcpExtension/bin/Release/net48/RoslynMcpExtension.vsix`.
3. Restart Visual Studio.

## Usage

### Serving the Open Solution

The extension starts no server and needs no port or settings. Once Visual Studio has loaded a solution, the extension serves it over the named pipe `roslyn-mcp-vs-<version>-<hash of the solution path>`, open to the current Windows user only, and closes the pipe when the solution closes. With no solution open it serves nothing.

Agents reach it through the Roslyn MCP plugin's `roslyn-mcp` server. Its daemon forwards the calls for exactly that solution file to Visual Studio, and `roslyn_load` answers `servedBy: visualStudio`. Any other path, such as the same solution in a Git worktree, the daemon loads itself. A solution the daemon loaded goes to Visual Studio as soon as Visual Studio opens it, and back to the daemon when Visual Studio closes it. The daemon and the extension of different versions do not meet; the daemon then loads the solution itself.

### Output Pane

The extension logs all activity to a dedicated **"Roslyn MCP Extension"** pane in the Visual Studio Output window (**View > Output**, then select "Roslyn MCP Extension" from the dropdown). This includes:

- **Serving**: when the extension starts and stops serving a solution to the daemon
- **Tool invocations**: each call the daemon forwards, with name and elapsed time
- **Exceptions**: any errors while serving, or during VS interaction

### Dead Code Analysis

`roslyn_find_dead_code` reports **potentially** unused methods, fields, and types from the live Visual Studio workspace. It uses Roslyn semantic references plus additional heuristics for framework-driven and runtime-driven code paths that do not always appear as normal source references.

The analysis is intentionally conservative and already filters several common false-positive patterns:

- **Test code**: xUnit, NUnit, and MSTest attributes such as `Fact`, `Theory`, `Test`, `TestCase`, `TestMethod`, `DataTestMethod`, setup/cleanup attributes, and types that contain or inherit test methods
- **Interface contracts**: both explicit and implicit interface implementations
- **XAML usage**: event handlers, code-behind types, attached dependency properties, and parameterless constructors for controls and windows instantiated from `.xaml` — including base classes whose derived types are XAML-activated
- **Visual Studio / MEF composition**: `Export`, `Import`, `ImportingConstructor`, and Visual Studio package types decorated with `PackageRegistrationAttribute`
- **MCP tool entry points**: methods and types decorated with `McpServerToolAttribute` or `McpServerToolTypeAttribute`, which are invoked dynamically by the MCP framework
- **Generated and interop code**: common generated files, compiler-generated members, and marshaling / `StructLayout` fields
- **Extension patterns**: static extension containers, classic `this` extension methods, and newer C# `extension(...) { }` blocks
- **Inherited test base classes**: abstract base types whose derived classes are test containers

Dead-code detection can never be perfect, especially for reflection-heavy or externally activated code, so results should still be reviewed before deletion.

## Example Prompts

```
Validate the file C:\MyProject\src\UserService.cs for errors and warnings
```

```
Find all references to the method ProcessOrder in C:\MyProject\src\OrderService.cs at line 42, column 20
```

```
Go to the definition of the symbol at line 15, column 10 in C:\MyProject\src\OrderService.cs
```

```
List all symbols in C:\MyProject\src\UserService.cs
```

```
Search for all symbols named "Repository" in the current solution
```

```
What is the symbol at line 25, column 8 in C:\MyProject\src\OrderService.cs?
```

```
Find dead code in the active workspace
```

```
Find dead code including public members
```

## How It Differs from the Daemon's Own Load

| Feature | Served by this extension | Loaded by the daemon |
|---------|--------------------------|----------------------|
| Workspace | Live VS `VisualStudioWorkspace` | A workspace of its own, loaded with MSBuild |
| Unsaved changes | ✅ Sees current editor state | ❌ Only saved files |
| Diagnostics | ✅ Live from VS compiler | Compiled by the daemon |
| Memory | No second load of the solution | One workspace per solution in the daemon |
| Which solutions | Exactly the solution file VS has open | Any path, a Git worktree's included |
| Setup | Install the VSIX of the plugin's version | None |
| When VS closes the solution | The daemon loads it itself | — |

## License

MIT

## Source-generator diagnostics

Visual Studio can retain stale output from source generators in Balanced mode. `roslyn_validate_file` reports the baseline IDE compilation and adds `sourceGeneratedDocumentCount`: zero means a completed observation found no generated documents in the validated file's project; absent or null means the observation failed or exceeded its 10-second budget. This count is not a freshness signal. Requesting it can execute generators, but does not replace the diagnostics already captured for this response.

An IDE build may help refresh the IDE cache, but does not guarantee immediate freshness: the experiment observed stale diagnostics even after a successful IDE build. `dotnet build` checks disk code and does not refresh that cache. Allow the workspace to update and validate again; persistence of the diagnostic still needs investigation. Automatic generator execution is a user-controlled VS option. Do not suppress genuine errors or assume every missing generated member is stale.

The issue-70 experiment completed three scored live Balanced A-to-B trials without reproducing stale diagnostics in that direction. Stale MemberA diagnostics were observed during B-to-A preparation after successful IDE builds. Those preparations were excluded from the scored trials, and the candidate sequence was not measured against their stale cache. The measured `GetSourceGeneratedDocumentsAsync` then `GetCompilationAsync` sequence on the same captured project therefore did not demonstrate a refresh fix. The not-reproduced label applies only to the scored direction. It does not establish whether the candidate can repair the observed preparation failure or the reported branch-switch issue.
