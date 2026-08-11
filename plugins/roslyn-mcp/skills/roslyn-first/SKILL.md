---
name: roslyn-first
description: Use first for semantic questions about C#/.NET symbols, compiler diagnostics, references, callers, definitions, implementations, or dead code. Codex and Claude Code use a strict Roslyn-first gate; Cursor may use semantic search for conceptual discovery but must verify symbol claims with Roslyn MCP.
---

# Roslyn MCP first (C#/.NET)

Roslyn MCP is backed by Visual Studio's live `VisualStudioWorkspace`, including unsaved editor changes, live diagnostics, and the active compilation. Text search sees characters; Roslyn sees the program.

## Choose the host route

- **Codex and Claude Code:** use the strict gate.
- **Cursor:** use the two-lane router.

## Codex and Claude Code — strict gate

Before `Grep`, `rg`, `Select-String`, manual C# reading, or `dotnet build` is used to answer a semantic question, use Roslyn MCP first. This includes symbol usage, references, callers, definitions, implementations, signatures, diagnostics, and dead-code questions.

In Codex, if the `mcp__roslyn-mcp__*` tools are not loaded, use tool discovery first. A tool not being immediately visible is not a reason to skip the gate.

Fall back to text search only when Roslyn MCP is unavailable, inconclusive, or outside its scope, and state why.

## Cursor — two-lane router

- **Lane 1, precise symbol question:** use Roslyn MCP first, exactly as with the strict gate.
- **Lane 2, conceptual discovery:** Cursor semantic search or grep may find candidates, but verify every symbol-level claim with Roslyn MCP before asserting it.

Rule: search finds candidates; Roslyn states facts.

## Availability checklist

1. Confirm the Roslyn MCP tools are available. In Codex, use tool discovery when necessary.
2. Confirm Visual Studio has the relevant solution loaded. Roslyn MCP exposes only the live solution attached to the configured port.
3. If Visual Studio is closed, the wrong solution is loaded, or the server is unreachable, say so before using text search or build output as a fallback.

## Use Roslyn MCP first for

- Compiler, nullable, warning, and analyzer diagnostics.
- Symbol lookup, signatures, types, documentation, base types, interfaces, and parameters.
- References, implementations, callers, and definitions.
- Document symbol listings and declaration searches.
- Conservative dead-code discovery.

## Reading the surrounding code

`roslyn_find_references` and `roslyn_find_callers` return `enclosingStartLine` and `enclosingEndLine` for every location: the declaration span of the member containing it. When the surrounding logic matters, read exactly that line range instead of calling `roslyn_get_document_symbols` to find where the member begins and ends.

## Continue to use shell and build tools for

- Locating files before a Roslyn request needs an absolute path.
- Non-C# files, SQL, configuration, generated assets, and repository-wide text patterns.
- MSBuild/CI, restore, packaging, runtime behavior, and test execution.
- Fallback when Roslyn MCP is unavailable or inconclusive.

Never delete code solely from a dead-code report. Review reflection, dependency injection, serialization, framework activation, and external API usage first.
