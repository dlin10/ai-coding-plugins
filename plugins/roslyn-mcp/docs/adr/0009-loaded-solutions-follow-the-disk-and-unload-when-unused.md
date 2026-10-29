# Loaded solutions follow the disk and unload when unused

The daemon (ADR 0004) loaded a solution once and answered from that snapshot for as long as it ran: an edit made after
the load was invisible to every later call, and every solution any session had loaded stayed in memory until the
process ended. Agents edit files between calls, and one daemon serves every session of a plugin version.

So a loaded solution follows its files on disk, the daemon unloads what nobody calls, and it exits once it holds
nothing.

- **A workspace of the daemon's own.** MSBuildWorkspace writes every change applied to it back to disk and cannot load
  one project again. The daemon loads with MSBuild's project loader, as MSBuildWorkspace does, into a `Workspace` of its
  own that takes changes in memory only.
- **The watcher queues, the call applies.** A FileSystemWatcher on the repository holding the solution (the solution's
  folder outside one), and on any project folder outside that, queues the paths that change. It skips `bin`, `obj`,
  `node_modules` and dot-named folders. Every call first applies the queue, one call at a time per solution: each path
  is compared with the modification time and size recorded when the daemon last read it. An edited document is read
  again when next asked for, a deleted one leaves its project, and a folder deleted or renamed away takes its documents
  along.
- **The named file is checked directly.** The files a call names are compared whether or not the watcher has reported
  them, so the call that names an edited file sees the edit.
- **An overflow means a rescan.** After a watcher overflow or error, or with more than 10,000 paths waiting, the next
  call compares every file of the solution and looks for new files in every project folder.
- **Project files and their imports.** The restore check (ADR 0006) records the files each project's evaluation
  imported, keeping those inside the followed tree. An edit to a project file or to one of those files runs the restore
  check on the affected projects, the project and every project that depends on it, and loads them again. The loader
  resolves references to every other project without loading it again, but gives the projects it loads new IDs; the
  projects that referenced them are pointed at the new ones. A project that fails to load, typically a project file
  saved halfway through an edit, keeps its last state until its file changes again.
- **New files.** A file that appears in a project's folder with the extension of one of its documents loads that project
  again, whose globs may take the file in. A file the project still leaves out triggers no reload again until the
  project reloads for another reason.
- **The solution file.** An edit to it loads the whole solution again. When it is gone, the solution is unloaded at once
  on the watcher's event, which also releases the watch on a removed worktree, or else by the next call, which answers
  `load_failed`.
- **Catching up waits like a load.** A call waits for the catch-up as for a load, about 50 seconds, then answers
  `workspace_not_ready` while the catch-up goes on.
- **Lifetime.** A solution no call has asked for in 2 hours is unloaded. The daemon exits once it has held no solution
  for 30 minutes, counted from its start or its last unload. A sweep every minute decides both. `roslyn_unload` unloads
  a solution at once; every session whose active solution it was, the caller's included, loads it again on its next
  call. The hidden `daemon` options `--unload-after` and `--exit-after` shorten the times for tests.
- **roslyn_unload is annotated read-only**, by the reasoning that keeps roslyn_load read-only (ADR 0005): it changes no
  file, only what the daemon holds in memory, and any other annotation makes Codex ask for approval on every call, which
  an unattended run cannot give.

## Considered Options

- **MSBuildWorkspace, loaded afresh on every project edit,** keeps the old loading path, but one edited project costs a
  whole load: 71 s for OrchardCore.
- **Comparing every file on every call** needs no watcher, but every call then reads the times of thousands of files on a
  large solution.
- **A watcher per project folder** misses the `Directory.Build.props` and `.editorconfig` files above the solution's
  folder. Watching the repository sees them, at the price of events from the rest of it, which the queue drops by path or
  ignores as files the solution does not hold.
- **A props file affecting the projects below its folder,** as `Directory.Build.props` does, misses a file imported from a
  sibling folder, and in a repository of several solutions reloads this one for another's `Directory.Build.props`.
- **Reloading only the edited project** leaves the projects depending on it without the packages they reach through it.
- **Counting the exit time from the first unload only** keeps a daemon that never loaded anything alive forever.

## Consequences

- A file outside the followed tree, such as a source package's content file, is seen only when a call names it or at a
  rescan, and an import from outside the tree is not followed.
- A restore made outside the daemon, as `dotnet build` makes one, is seen at the next edit of a project file or of an
  import, or when the solution loads again.
- Every project load starts MSBuild's build host again, about 1.3 s for a small project (measured 2026-10-29). An edit to
  a project that most of a large solution depends on reloads the dependents one by one, which can take longer than
  loading the solution afresh.
- A modification time and size that an edit leaves both unchanged hide it, as they do from every build tool.
- A call that arrives while its solution is being unloaded, or the daemon is exiting, can fail; the next call loads the
  solution again or starts a daemon.
- After a plugin update the older version's daemon gets no more calls, unloads its solutions after 2 hours and exits 30
  minutes later, beside the newer version's daemon on its own pipe.
- A solution that Visual Studio serves (#214) holds no workspace in the daemon: the times apply to it, the disk sync
  does not.
