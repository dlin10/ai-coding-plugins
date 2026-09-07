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

In Codex, if the Roslyn MCP tools are not loaded, use tool discovery first. A tool not being immediately visible is not a reason to skip the gate. The server name comes from this repository's configuration: usually `roslyn-mcp`, giving `mcp__roslyn-mcp__*`, but a repository holding several solutions registers one server per solution, giving names such as `mcp__roslyn-<slug>__*`.

Fall back to text search only when Roslyn MCP is unavailable, inconclusive, or outside its scope, and state why.

## Cursor — two-lane router

- **Lane 1, precise symbol question:** use Roslyn MCP first, exactly as with the strict gate.
- **Lane 2, conceptual discovery:** Cursor semantic search or grep may find candidates, but verify every symbol-level claim with Roslyn MCP before asserting it.

Rule: search finds candidates; Roslyn states facts.

## Availability checklist

1. Confirm the Roslyn MCP tools are available. In Codex, use tool discovery when necessary.
2. Confirm Visual Studio has the relevant solution loaded. Roslyn MCP exposes only the live solution attached to the configured port. When the repository registers several Roslyn servers, pick the one belonging to the solution that owns the code in question; the others answer for different solutions and report nothing useful about it.
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

## Source-generator diagnostics

Visual Studio can retain stale output from source generators in Balanced mode. `roslyn_validate_file` reports the baseline IDE compilation and adds `sourceGeneratedDocumentCount`: zero means a completed observation found no generated documents in the validated file's project; absent or null means the observation failed or exceeded its 10-second budget. This count is not a freshness signal. Requesting it can execute generators, but does not replace the diagnostics already captured for this response.

An IDE build may help refresh the IDE cache, but does not guarantee immediate freshness: the experiment observed stale diagnostics even after a successful IDE build. `dotnet build` checks disk code and does not refresh that cache. Allow the workspace to update and validate again; persistence of the diagnostic still needs investigation. Automatic generator execution is a user-controlled VS option. Do not suppress genuine errors or assume every missing generated member is stale.

The issue-70 experiment completed three scored live Balanced A-to-B trials without reproducing stale diagnostics in that direction. Stale MemberA diagnostics were observed during B-to-A preparation after successful IDE builds. Those preparations were excluded from the scored trials, and the candidate sequence was not measured against their stale cache. The measured `GetSourceGeneratedDocumentsAsync` then `GetCompilationAsync` sequence on the same captured project therefore did not demonstrate a refresh fix. The not-reproduced label applies only to the scored direction. It does not establish whether the candidate can repair the observed preparation failure or the reported branch-switch issue.
