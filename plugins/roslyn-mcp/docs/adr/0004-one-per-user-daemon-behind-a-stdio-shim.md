# One per-user daemon behind a stdio shim

Agents reached Roslyn only through the Visual Studio extension: one HTTP server per solution, alive while Visual
Studio had that exact solution open. Background agents run without Visual Studio, and forge runs one `codex exec` per
turn. Codex 0.165 puts its MCP servers in a job object that kills everything in it when it closes and refuses breakaway
(limit flags `0x2000`, measured 2026-10-29), so whatever a server starts dies with the turn. A server that loaded the
solution itself would load it again on every turn: 2–3 s for this repository's solutions, 71 s for OrchardCore.

So the plugin declares a thin stdio MCP server, the **shim**, which forwards every tool to one **daemon** per Windows
user and plugin version. The daemon holds one MSBuildWorkspace per solution and keeps it loaded across sessions and
turns. Both are one executable, `roslyn-mcp.exe`, in two modes, `mcp` and `daemon`.

- The shim contacts the daemon on the first tool call, never on `initialize`, so `claude mcp list` and
  `codex mcp list` start nothing.
- When no daemon answers, the shim starts one with `CREATE_BREAKAWAY_FROM_JOB`, so that it outlives the host's job,
  and through a short-lived `start-daemon` process, so that it outlives a kill of the shim's process tree (ADR 0010).
  Where the job refuses breakaway (`ERROR_ACCESS_DENIED`), the shim starts it inside the job instead, and `roslyn_load`
  reports `daemon.exitsWithJob`, which the daemon reads from the limits of its own job.
- They meet on the pipe `roslyn-mcp-<version>-<user SID>`, created `PipeOptions.CurrentUserOnly`: a shim reaches only
  the daemon of its own version, and no other account can open the pipe. The first instance is created
  `FirstPipeInstance`, so of two daemons started at once the second exits.
- A connection is a session. `LoadAsync(solution)` binds the connection to a solution, and the eleven calls of the
  existing analysis contract answer from it unchanged. A path is loaded once however many sessions ask for it, and a
  call waits for a load about 50 seconds, under the 60 seconds Codex waits on a tool by default, then answers `loading`
  while the load goes on.
- The analysis services already took the base `Workspace`. They moved into `RoslynMcpExtension.Services`, a
  netstandard2.0 library that the net48 extension runs on Visual Studio's Roslyn and the daemon on Roslyn 5.9.

## Considered Options

- **A server per session that loads the solution itself** is the simplest, and under codex it reloads on every turn.
- **A process per solution, with or without a broker,** isolates crashes and memory at the price of a second protocol
  between processes. Worth revisiting if one daemon's heap proves too large for several big solutions.
- **A scheduled task or a logon-time daemon** runs when nobody uses it and must be installed; **forge starting the
  daemon** serves forge only.
- **A shim executable and a daemon executable** would keep Roslyn out of the shim's folder. But the shim loads an
  assembly only when it first uses it, and one executable gives one release asset and a shim that starts the daemon
  from its own path, so always of its own version.

## Consequences

- Under codex exec the daemon still exits with the turn and the next turn loads again; the agent is told so rather
  than left to guess why it waits.
- Roslyn's build host finds MSBuild from each project's own directory, so a `global.json` that pins an older SDK is
  honoured inside the one daemon.
- A daemon of an older version keeps its own pipe; a shim of a newer version starts a daemon of its own beside it.
- The shim holds the session's active solution. When its daemon has gone, the next call starts one and loads that
  solution there again.
- The daemon reads files from disk, never Visual Studio's unsaved editor buffers. A solution Visual Studio has open is
  answered by Visual Studio instead, buffers included (ADR 0007).
