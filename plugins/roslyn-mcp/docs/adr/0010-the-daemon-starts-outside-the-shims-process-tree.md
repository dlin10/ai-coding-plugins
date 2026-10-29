# The daemon starts outside the shim's process tree

ADR 0004 starts the daemon with `CREATE_BREAKAWAY_FROM_JOB`, so that it outlives the host's job and keeps its solutions
loaded for the next session. Under `claude -p` (Claude Code 2.1.312) the daemon reported `exitsWithJob: false` and was
still gone when the session ended, so every Claude Code session loaded its solution again. Claude Code ends a stdio
server it started by running `taskkill /PID <server> /T /F`: at the end of a session, measured 2026-10-29, `claude.exe`
ran it on the launcher's `cmd.exe`, and the launcher, the shim and the daemon exited in the same millisecond with exit
code 1. `taskkill /T` finds the tree by parent process ID, and the daemon was the shim's child. Jobs play no part in
that, so breaking away from one was no protection.

So the shim starts the daemon through a third mode of the same executable, `roslyn-mcp.exe start-daemon --pipe <pipe>`,
which starts the daemon and exits at once. The daemon's parent has then exited, and a kill of the shim's tree by parent
process ID does not reach it.

- The shim starts `start-daemon` inside whatever job the shim is in, with no handle inherited. `start-daemon` starts the
  daemon with `CREATE_BREAKAWAY_FROM_JOB` and, on `ERROR_ACCESS_DENIED`, inside the job, as the shim did. It sees the
  shim's jobs, so the daemon lands where it did before and `exitsWithJob` stays what it was: under `codex exec` the
  daemon is in the turn's job, reports `exitsWithJob: true` and ends with the turn.
- `start-daemon` exits with 0 once the daemon process exists, otherwise with the Win32 error, which the shim reports as
  it did before. The shim waits up to 30 s for it, then for the daemon's pipe as before. In the measured runs it lived
  100–140 ms.
- Measured with real `claude -p --plugin-dir` sessions on `PlanForgeFlow.sln`: before, each session started a daemon of
  its own, and `roslyn_load` took 4.2 s and 4.0 s. After, the first session's daemon (4.1 s) still served the pipe when
  `claude -p` had returned, and the second session's `roslyn_load` answered `loaded` in 0.3 s with the first session's
  `loadedAt` and daemon process.

## Considered Options

- **The daemon starting a copy of itself and exiting** does the same with one mode fewer. But a `daemon` that does not
  serve the pipe it was started for surprises anyone who starts it directly, as the tests do.
- **Creating the process through WMI (`Win32_Process.Create`) or the Task Scheduler** gives the daemon a parent outside
  the session as well, but it also takes the daemon out of every job the host put the shim in, codex exec's included,
  which refuses breakaway on purpose. Both depend on a service the user or a policy can turn off.
- **Naming another parent with `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS`** needs a handle with create-process rights to a
  process outside the tree, and parent spoofing is what endpoint protection looks for.
- **Leaving it to the host** is not an option: Claude Code kills the tree so that a server's helpers do not leak, and
  nothing in a server's configuration turns that off.

## Consequences

- A daemon started under `claude -p` keeps running after the session, as ADR 0004 intends, and the next session finds
  its solutions loaded. It runs until the user ends it or signs out, within the idle limits of #213.
- Where the host's job refuses breakaway and does not kill on close, such as the MSIX package job of the Claude desktop
  app (limit flags `0x0`), the daemon stays in that job, reports `exitsWithJob: false` and now outlives the session too.
  Whether it outlives the desktop app was not measured.
- A host that ends its servers by closing a job, as `codex exec` does, still ends the daemon with the turn.
- Starting a daemon costs one more start of the executable, about 0.1 s, once per daemon rather than per session.
