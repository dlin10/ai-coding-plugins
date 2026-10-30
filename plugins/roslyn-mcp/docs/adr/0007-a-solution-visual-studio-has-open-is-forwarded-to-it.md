# A solution Visual Studio has open is forwarded to it

The daemon of ADR 0004 loads any solution itself. A Visual Studio that has the same solution open already holds a
workspace of it, loaded, restored and kept in step with the disk and the editor, and until 3.0 the extension served that
workspace through an HTTP server of its own on a port read from `.roslynmcp.json`. Loading the solution a second time
in the daemon costs the memory and the minutes of a second workspace, and answers from a copy that does not see what the
user is typing.

So the extension starts no server and reads no port: it serves the analysis contract from Visual Studio's workspace on
a pipe named for the open solution, and the daemon forwards that solution's calls to it (decision D20 of #207).

- **The pipe's name.** `roslyn-mcp-vs-<version>-<hash>`. `<version>` is `ExtensionVersion.Value`, the version the
  daemon's own pipe carries, so a daemon and an extension of different versions never meet and the daemon loads the
  solution itself. `<hash>` is the SHA-256, in hex, of the solution file's full path with backslashes, upper-cased
  invariantly. `VisualStudioPipe.Name`, in the netstandard2.0 shared library, computes it for the net48 extension and
  the net10 daemon alike; `string.GetHashCode` would differ from one process to the next on .NET.
- **Exact path only.** The daemon forwards a path to that path's pipe and to no other. The same solution in a linked
  worktree has another path and loads in the daemon, as ADR 0003 answers a worktree's file from the worktree.
- **The current user only.** The extension creates the pipe with one access rule, for the current user, and makes that
  user its owner: what `PipeOptions.CurrentUserOnly` sets on .NET, which .NET Framework lacks. The daemon connects with
  `CurrentUserOnly`, which refuses a pipe another account owns. An access rule is what restricts who opens a pipe; the
  user's SID in the name, as the daemon's pipe carries it, restricts nothing and only keeps two accounts from contending
  for one name.
- **When the pipe opens and closes.** The extension opens the pipe once Visual Studio's solution load has finished its
  IntelliSense stage, when the workspace holds every project, and closes it, dropping every connection, in
  `OnBeforeCloseSolution`, before the workspace empties. With no solution open, or a folder instead of one, there is no
  pipe. It accepts any number of connections, one after another or at once, and keeps an instance listening for the next.
- **At load.** Before it loads a solution, the daemon looks for the solution's pipe. When the pipe exists and accepts a
  connection, the registry's entry for the path forwards every call over that connection. It holds no workspace, runs no
  restore and no disk sync, and `roslyn_load` reports no `restore`, `projectCount` or `documentCount` for it. The unload
  and exit times of ADR 0009 apply to it unchanged; unloading it closes the connection.
- **Switching, the user's choice.** On every call to a solution it loaded itself, the daemon looks for the pipe again.
  The first call after Visual Studio has opened the solution goes to Visual Studio, and the daemon disposes its own
  workspace and disk sync at once, to free the memory. A load still under way finishes first; the next call switches.
- **When Visual Studio goes.** When Visual Studio closes the solution or exits, the connection drops and the entry with
  it. The next call loads the solution in the daemon, restore and disk sync included, and answers `loading` meanwhile,
  like any load; a call under way when the connection drops answers `workspace_not_ready`. Neither is an error.
- **A pipe that refuses.** A pipe that exists but does not let the daemon in, as another account's does, is left alone
  for a minute, and the solution loads, or stays, in the daemon.
- **The per-call check.** `WaitNamedPipe` on `\\.\pipe\<name>` with a 1 ms timeout reports whether the pipe exists
  without opening it, so it takes no connection from Visual Studio. Measured 2026-10-30, 100,000 calls from PowerShell 7:
  about 15 µs while Visual Studio listens, and 19 µs when there is no pipe (`ERROR_FILE_NOT_FOUND`). While every instance
  is busy it waits out the timer's resolution, 16 ms, and fails with `ERROR_SEM_TIMEOUT`, which still counts as open.
- **Who serves.** `roslyn_load` reports `servedBy`, `daemon` or `visualStudio`. The other tools do not.

## Considered Options

- **An HTTP server per solution, as in 2.x,** needs a port per solution and the client configuration that goes with it,
  and serves nothing to the daemon's sessions.
- **Looking for Visual Studio at load only,** with no switching, keeps the daemon's load as long as sessions use it,
  beside the Visual Studio that opened the same solution since: two workspaces of one solution, and answers that ignore
  what the user is editing.
- **Probing by opening the pipe** takes a connection and a server instance on every call, and `File.Exists` on the pipe
  opens it as well. **Listing `\\.\pipe\`** takes none, but cost 4.2 ms with 1,077 pipes on this machine.
- **Opening the pipe at `OnAfterOpenSolution`** serves a workspace that Visual Studio is still filling project by project
  while a large solution loads in the background, and calls about the projects not there yet answer `DocumentNotFound`.
- **`servedBy` in every result** tells a session at once that its answers moved, at the price of a field on every result,
  for a difference that only Visual Studio's view of the files makes. A session that wants to know calls `roslyn_load`
  again, which answers at once for a loaded solution.

## Consequences

- Mid-session, answers can move from the disk to Visual Studio's view and back: once the user opens the solution in
  Visual Studio, calls answer from its workspace, unsaved editor buffers included, and once the user closes it, the
  daemon loads it again. This amends ADR 0004's consequence that the daemon never reads Visual Studio's unsaved editor
  buffers.
- A solution Visual Studio serves follows the disk as Visual Studio does, not as ADR 0009 has the daemon do: a file open
  in the editor answers with the editor's text, and a call naming a file is not checked against the disk first. In the
  Experimental-instance check of 2026-10-30, an edit written to a file not open in the editor was missing from the
  answers 0.17 s after the write and present 0.33 s after it.
- A call under way in another session while the daemon switches to Visual Studio can fail, as one during an unload can
  (ADR 0009).
- With the same solution file open in the Visual Studio of two accounts on one machine, the second cannot create the
  pipe, and its daemon loads the solution itself.
- The 2.x extensions keep their HTTP servers; only this extension opens pipes. Until the 3.0.0 release replaces the
  bundled 2.0.0 VSIX, no installed Visual Studio serves a pipe.
