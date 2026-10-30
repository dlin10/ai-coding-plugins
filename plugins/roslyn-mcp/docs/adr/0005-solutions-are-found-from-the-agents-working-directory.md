# Solutions are found from the agent's working directory

Only the agent knows its working directory. Claude declares MCP roots but Codex answers an empty list, and the shim's
own current directory is wherever the host started it. So `roslyn_load` takes the working directory as `directory`, and
the shim finds the solution from it, in this order:

1. the nearest `.roslynmcp.json` at or above the directory that names a `solution`; a marker that holds only a 2.x
   `port` is skipped;
2. the only solution in the nearest folder at or above the directory that holds any; two or more there are a choice;
3. the only marker below the directory that names a solution;
4. otherwise `needsChoice`, with every solution below the directory as a candidate, even a single one.

`foundBy` reports `marker`, `upward` or `downward`, or `explicit` when the client named `solution`, which wins over
`directory`. Discovery never picks a candidate. A wrong guess answers about another tree, and nothing in the answers
shows it: a worktree path answered from a same-named file of the main checkout is how this project started. An agent
asks the user; a session no user is watching reports Roslyn unavailable.

- **Markers.** `{ "solution": "X.slnx" }` names a file relative to the marker's folder; a rooted name is ignored.
  `roslyn_load(solution, remember: true)` writes one next to the chosen solution, never at the repository root, and
  keeps whatever else the marker holds. Markers are untracked, so a linked worktree reads the main working tree's at the
  same relative path, the mapping 2.x used for ports, and resolves their solution in the worktree. A choice made in a
  worktree is written to the main working tree's copy of the folder when it exists, so that the main working tree and
  every later worktree inherit it.
- **Bounds.** Upward searches stop at the root of the repository holding the directory, the first folder with a `.git`,
  so a stray solution above a repository never answers for it. Downward searches skip hidden and dot-named folders
  (`.git`, `.vs`, the worktrees under `.claude`), `bin`, `obj`, `node_modules`, junctions and nested repositories.
- **Switching.** A session has one active solution. When the daemon answers `DocumentNotFound` for a file a call names,
  the shim runs steps 1 and 2 from the file's folder, loads the solution found and repeats the call; the result's
  `solutionSwitch` names both solutions. If that solution does not hold the file either, the session returns to the one
  it had and the first error stands.

## Considered Options

- **MCP roots** are declared by Claude and returned empty by Codex.
- **Loading the only candidate below the directory** is a guess: the user may want a solution elsewhere, or none.
- **A marker at the repository root**, as 2.x wrote for a single-solution repository, resolves every directory of a
  multi-solution repository to one solution.
- **An absolute path in the marker** leads a worktree back to the main checkout's copy of the solution.
- **Markers and solutions checked folder by folder in one upward pass**, so that a marker counts only up to the nearest
  folder holding a solution, would keep a remembered choice from changing answers where discovery had none to make.
  Not chosen: the order above was fixed by the research, markers first.
- **Switching by the file's location before asking the active solution** would leave a solution that holds the file for
  one nearer to it; a file can belong to two solutions, so only a miss in the active one starts a switch.
- **The daemon switching** would put discovery on both sides of the pipe; the shim already holds the active solution.

## Consequences

- A marker above a folder holding another solution answers for that folder's subtree too. A choice of
  `CacheDetective.slnx` remembered in `plugins/cache-detective/src` also answers for its
  `Fixtures/WarningsAsErrors` folder, which `WarningsAsErrors.slnx` answered before.
- Two remembered solutions below one directory make that directory a choice again, and remembering one of them does not
  settle it: the agent names `solution`, or the user deletes the other marker.
- `roslyn_load` stays annotated read-only although `remember` writes a marker. The write records the user's own choice,
  and annotating the tool otherwise would make Codex ask for approval on every load.
- A marker that holds a `port` keeps it until `roslyn-setup-repo` migrates the repository.
- A switch to a solution that is still loading answers `workspace_not_ready` with `solutionSwitch` set, and the session
  stays on that solution.
