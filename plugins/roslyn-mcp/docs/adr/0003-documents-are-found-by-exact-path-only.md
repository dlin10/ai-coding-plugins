# Documents are found by exact path only

A tool that takes a file path answers about the document at exactly that path, compared after
full-path normalization and ignoring letter case and slash direction. When no document of the loaded
solution has that path, the request fails with `DocumentNotFound`, and the error names the solution
that was searched.

Lookup used to fall back to the file name when the exact path missed: the only document with that name
was returned as it was, and among several the one sharing the most trailing path segments won. A git
worktree holds the same relative paths as the checkout it was made from. So a path from a worktree,
asked of the Visual Studio instance that had the main checkout's solution open on another branch, was
answered with `requestSucceeded: true` and the symbols of the main checkout's file. Nothing in the
response told the client that it described a different file.

## Considered Options

- **Keep the fallback for a unique file name only** changes nothing for a worktree: its file is never in
  the solution, so the main checkout's copy is always the unique match. That was the case observed.
- **Keep the fallback and report the path that matched** leaves a success that every client must
  remember to compare against its request, and an agent reading symbols does not.
- **Match by the path relative to the solution's directory** answers every worktree from the main
  checkout by design.

## Consequences

- A relative path or a bare file name no longer resolves. The tool schemas already ask for an
  absolute path.
- A path through a junction or a symbolic link is not the path the solution loaded the file under, and
  is not found.
- The same rule decides the file scope of `roslyn_get_diagnostics`: a path outside the solution fails
  rather than checking the project of a same-named file.
- A client that asks the wrong server is told which solution it searched, the same signal that already
  separates a wrong server from a solution Visual Studio has not loaded.
- The headless daemon planned for 3.0.0 shares this lookup, so the rule holds without Visual Studio
  too; there is no file-name fallback in either mode.
