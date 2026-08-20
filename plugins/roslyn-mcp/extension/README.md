# Roslyn MCP Extension — Visual Studio Extension

A Visual Studio extension that exposes **semantic C# code analysis** via the [Model Context Protocol (MCP)](https://modelcontextprotocol.io/), powered by the **live Roslyn workspace** inside Visual Studio.

This extension was inspired by [Roslyn MCP Extension](https://github.com/sailro/RoslynMcpExtension) by Sebastien Lebreton.

Unlike standalone Roslyn MCP servers that create their own `MSBuildWorkspace`, this extension uses Visual Studio's actual `VisualStudioWorkspace` — giving you access to unsaved changes, live diagnostics, and the full compilation state that VS already maintains.

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
- .NET 10 ASP.NET Core Runtime for the bundled server process
- .NET 10 SDK when building the extension from source

## Building

The solution file is at `src/RoslynMcpExtension.slnx`. Since the VSIX project requires MSBuild, build via Visual Studio or `msbuild`:

```bash
# Full solution (requires Visual Studio / MSBuild)
msbuild src\RoslynMcpExtension.slnx /p:Configuration=Release /t:Rebuild /restore

# Server and Shared projects only (dotnet CLI)
dotnet build src\RoslynMcpExtension.Server\RoslynMcpExtension.Server.csproj
```

The VSIX project automatically publishes the MCP server process to its output directory.

## Installation

Marketplace users should follow the plugin-level [installation instructions](../README.md), which install the bundled VSIX.

For a source build:

1. Build the solution in Release mode.
2. Install `src/RoslynMcpExtension/bin/Release/net48/RoslynMcpExtension.vsix`.
3. Restart Visual Studio.

## Usage

### Starting the Server

The server auto-starts when a solution is loaded when **Auto Start** is enabled in **Tools > Options > Roslyn MCP Extension**. The extension resolves the port again whenever a solution opens or closes, so switching solutions also switches to the appropriate repository configuration.

You can also manually start/stop via **Tools > Start/Stop Roslyn MCP Server**.

### Output Pane

The extension logs all activity to a dedicated **"Roslyn MCP Extension"** pane in the Visual Studio Output window (**View > Output**, then select "Roslyn MCP Extension" from the dropdown). This includes:

- **Server lifecycle**: start, stop, process exit, connection status
- **Tool invocations**: each MCP tool call with name and elapsed time
- **RPC events**: client connect/disconnect, pipe errors
- **Exceptions**: any errors during server startup, command execution, or VS interaction
- **MCP Server messages**: the HTTP server process logs back to the Output pane via RPC

### Per-solution Port Configuration

When a solution opens, the extension walks upward from the solution directory and uses the nearest `.roslynmcp.json` file:

```json
{ "port": 5051 }
```

This allows several Visual Studio instances to expose different solutions simultaneously, each on its own port. Because the nearest file wins, a repository holding one solution can keep `.roslynmcp.json` at its root, while a repository holding several places one beside each solution to give each its own port. Keep `.roslynmcp.json` developer-local and configure every MCP client that works on a solution to use that solution's `http://localhost:<port>/mcp` endpoint.

If no `.roslynmcp.json` is found, the extension falls back to the **Port** configured under **Tools > Options > Roslyn MCP Extension**, whose default is `5050`. The same options page also controls the server name and automatic startup.

### Transport

The HTTP MCP endpoint runs in **stateless** mode (no `Mcp-Session-Id`). Clients can keep calling tools after a server restart (solution switch / Start-Stop) without session recovery.

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

## How It Differs from Other Roslyn MCP Servers

| Feature | This Extension | Others roslyn/mcp servers |
|---------|---------------|---------------------------------------|
| Workspace | Live VS `VisualStudioWorkspace` | Standalone `MSBuildWorkspace` |
| Unsaved changes | ✅ Sees current editor state | ❌ Only saved files |
| Find References | ✅ Semantic `SymbolFinder` | ❌ Text search or separate workspace |
| Diagnostics | ✅ Live from VS compiler | ⚠️ Re-compiled separately |
| Build integration | ✅ Uses VS compilation state | ❌ Separate compilation |
| Multi-instance support | Per-solution ports resolved from `.roslynmcp.json` | Commonly one global server configuration |
| Setup | Install the VSIX and align each repository's client port | Configure and load a separate workspace |
| Logging | ✅ Dedicated Output pane | ❌ Console/file logs |
| Session recovery | ✅ Transparent migration | ❌ Client must reconnect |

## License

MIT
