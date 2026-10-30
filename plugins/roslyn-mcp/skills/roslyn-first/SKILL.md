---
name: roslyn-first
description: Use first for semantic questions about C#/.NET symbols, compiler diagnostics, references, callers, definitions, implementations, type APIs, or dead code. Codex and Claude Code use a strict Roslyn-first gate; Cursor may use semantic search for conceptual discovery but must verify symbol claims with Roslyn MCP.
---

# Roslyn MCP first (C#/.NET)

Roslyn MCP answers from the compiler's view of a whole C# solution: its projects, references, target frameworks and diagnostics. The plugin's `roslyn-mcp` server forwards every call to a per-user daemon that keeps the solution loaded and follows its files on disk, or to the Visual Studio that has the solution open. Text search sees characters; Roslyn sees the program.

## Choose the host route

- **Codex and Claude Code:** use the strict gate.
- **Cursor:** use the two-lane router.

## Codex and Claude Code — strict gate

Before `Grep`, `rg`, `Select-String`, manual C# reading, or `dotnet build` is used to answer a semantic question, use Roslyn MCP first. This includes symbol usage, references, callers, definitions, implementations, signatures, diagnostics, and dead-code questions.

The tools are the plugin server's: `mcp__roslyn-mcp__*` in Codex, `mcp__plugin_roslyn-mcp_roslyn-mcp__*` in Claude Code. In Codex, if they are not loaded, use tool discovery first. A tool not being immediately visible is not a reason to skip the gate.

Fall back to text search only when Roslyn MCP is unavailable, inconclusive, or outside its scope, and state why.

## Cursor — two-lane router

- **Lane 1, precise symbol question:** use Roslyn MCP first, exactly as with the strict gate.
- **Lane 2, conceptual discovery:** Cursor semantic search or grep may find candidates, but verify every symbol-level claim with Roslyn MCP before asserting it.

Rule: search finds candidates; Roslyn states facts.

## Load the solution first

Call `roslyn_load` before any other Roslyn tool, with `directory` set to your working directory as an absolute path. Its `solution` and `foundBy` name the tree every later answer comes from. When the user names a solution, pass its absolute path as `solution` instead; it wins over `directory`. `status: loading` means a first load is still running in the background: call `roslyn_load` again shortly.

`status: needsChoice` loads nothing and lists `candidates`:

- **A user is present:** show the candidates, ask which solution to use, then call `roslyn_load` with that `solution` and `remember: true`. The choice is saved next to the solution, so the next session in this directory loads it without asking.
- **Unattended** (a forge worker, `claude -p`, `codex exec`, any session where nobody answers): report Roslyn unavailable, naming the directory and the candidates, and take the text-search fallback. A candidate you pick yourself analyses a tree nobody chose, and nothing in the answers shows it.

A result carrying `solutionSwitch` named a file of another solution: it was answered from `solutionSwitch.to`, and the session now answers from that solution.

Every call sees the files as they are on disk, so an edit needs no reload. When `roslyn_load` reports `servedBy: visualStudio`, the Visual Studio that has the solution open answers instead: a file open in its editor answers with the editor's unsaved text, and an edit you just wrote reaches the answers a fraction of a second later.

## Paths are exact

Name every file by its absolute path in the tree you work in. A file is found by its exact path only: letter case and slash direction do not matter, but a relative path or a bare file name does not resolve. In a Git worktree, use the worktree's path; a path into the main checkout answers from the main checkout's files, on whatever branch that checkout is. `DocumentNotFound` names the solution that was searched, and means that solution holds no file at that path — a wrong or relative path, or a file of a solution no discovery reaches — not that the load failed.

## Unload when you are done

At the end of a forge run, or of any other unattended run, call `roslyn_unload` for the solution the run loaded, before its worktree is removed. Do the same before removing any worktree whose solution you loaded. Between the turns of one run, leave the solution loaded: the next turn would only load it again. Left alone, the daemon unloads a solution after 2 hours without calls.

## Availability checklist

1. Confirm the Roslyn MCP tools are available. In Codex, use tool discovery when necessary.
2. Confirm `roslyn_load` answered `loaded` for the solution that owns the code in question, and read `partial`: a failed package restore leaves the failed projects without their packages' types, so errors such as CS0246 there may be the restore's, not the code's.
3. If the server is unavailable, the load fails, or the file belongs to no solution discovery finds, say so before using text search or build output as a fallback.

## Use Roslyn MCP first for

- Compiler, nullable, warning, and analyzer diagnostics.
- Symbol lookup, signatures, types, documentation, base types, interfaces, and parameters.
- References, implementations, callers, and definitions.
- Document symbol listings and declaration searches.
- Conservative dead-code discovery.

## Reading the surrounding code

`roslyn_find_references` and `roslyn_find_callers` return `enclosingStartLine` and `enclosingEndLine` for every location: the declaration span of the member containing it. When the surrounding logic matters, read exactly that line range instead of calling `roslyn_get_document_symbols` to find where the member begins and ends.

## After an edit, and before using an unfamiliar type

- After changing C# code, pass the changed files to `roslyn_get_diagnostics`. It also checks every project depending on them, where a changed signature breaks callers that `roslyn_validate_file` never sees.
- Before writing code against a type from a package or the framework, read its API with `roslyn_describe_type`, not from memory. It answers for the version the project references, with the extension methods in reach and the namespace each one needs.

## Continue to use shell and build tools for

- Locating files before a Roslyn request needs an absolute path.
- Non-C# files, SQL, configuration, generated assets, and repository-wide text patterns.
- MSBuild/CI, restore, packaging, runtime behavior, and test execution.
- Fallback when Roslyn MCP is unavailable or inconclusive.

Never delete code solely from a dead-code report. Review reflection, dependency injection, serialization, framework activation, and external API usage first.

## Source-generator diagnostics

Visual Studio can retain stale output from source generators in Balanced mode. `roslyn_validate_file` reports the baseline IDE compilation and adds `sourceGeneratedDocumentCount`: zero means a completed observation found no generated documents in the validated file's project; absent or null means the observation failed or exceeded its 10-second budget. This count is not a freshness signal. Requesting it can execute generators, but does not replace the diagnostics already captured for this response.

An IDE build may not refresh the IDE cache: stale diagnostics have been observed after a successful one. `dotnet build` checks code on disk and does not refresh that cache either. Allow the workspace to update and validate again. Automatic generator execution is a user-controlled VS option. Do not suppress genuine errors or assume every missing generated member is stale.
