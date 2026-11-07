# AGENTS.md

Codex-only Windows plugin: a small WPF overlay over Codex Desktop with the last observed cache-read
tokens and a TTL estimate. The source of truth is `docs/spec.md` (Russian); the product view is
`docs/prd.md`.

## Layout

- `CodexSessionPulse.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `global.json` at the plugin root.
- `src/CodexSessionPulse.App` – the only WinExe, `CodexSessionPulse.exe`: `--hook [--no-start]`, `--overlay`.
  `--overlay --stub` replaces the rollout pipeline with a scripted snapshot loop over every `CacheState` (development only).
- `hooks/hooks.json` and `bin/codex-session-pulse-launcher.cmd` – the Codex hooks; the launcher runs the exe from
  `%USERPROFILE%\.codex-session-pulse\bin\<manifest version>\` (or `%CODEX_SESSION_PULSE_HOME%\bin\…`).
- `src/CodexSessionPulse.Core` – domain contract, trackers, state. No WPF, no I/O, no formatted strings.
- `src/CodexSessionPulse.Infrastructure` – JSONL tailer, settings, filesystem. References Core.
- `src/CodexSessionPulse.Win32` – HWND, DPI, monitor geometry.
- `tests/<Project>.Tests` – xUnit, one test project per source project.

## Build and test

Run from this folder. Redirect to a log under an ignored path (e.g. the repo's `scratch/`) and read the tail.

```powershell
dotnet build CodexSessionPulse.slnx --disable-build-servers -nodeReuse:false > ../../scratch/csp-build.log 2>&1; Get-Content ../../scratch/csp-build.log -Tail 40
dotnet test CodexSessionPulse.slnx --no-build > ../../scratch/csp-test.log 2>&1; Get-Content ../../scratch/csp-test.log -Tail 40
```

One project: `dotnet test tests/CodexSessionPulse.Core.Tests`. One test: add `--filter "FullyQualifiedName~Name"`.
The Win32 tests start the real exe from `src/CodexSessionPulse.App/bin/<Configuration>/net10.0-windows/`,
so build the solution first. Before a PR also run `npm run validate:plugins` at the repo root.
`CodexDesktopIntegrationTests` run only with `CODEX_SESSION_PULSE_INTEGRATION=1` and an open Codex Desktop window.

To watch the overlay on the real Codex window without touching your own settings, point it at a scratch home:
`$env:CODEX_SESSION_PULSE_HOME = "<repo>\scratch\csp-home"`, then start
`src/CodexSessionPulse.App/bin/Debug/net10.0-windows/CodexSessionPulse.exe --overlay --stub`. Exit from the tray menu,
or stop the exact PID.

## Rules

- **No WPF in the `--hook` path.** Hook warm start must stay within 150 ms. `Program` and `HookRunner` must not
  touch a WPF type; WPF is reached only through `OverlayHost`, called with `NoInlining`. The test
  `Hook_WithJsonOnStdin_ExitsZeroWithoutLoadingWpf` enforces it (set `CODEX_SESSION_PULSE_DEBUG_ASSEMBLIES=1`
  to make `--hook` print the loaded assemblies).
- **The spec §4.3 public records in Core are frozen** (`TokenUsageSnapshot`, `CacheSnapshot`, `AnchorSnapshot`, the
  interfaces and enums): do not rename, reorder or add members; changes go through the orchestrator. Additive types
  are allowed and listed below.
- The hook never writes stdin anywhere, reads nothing from the current directory, and always exits 0.
- Every public type and member in Core carries a complete XML doc comment; the build fails without one.

## Additive contract (#251)

New public types beside the frozen ones; adding members to them is allowed. Their XML docs hold the rules.

- `PulseEngine` – the one entry point: `Apply`/`Observe` from the reader, `OnUserActivity`/`OnSessionStart`/
  `OnSessionEnd` from hooks (thread id from `RolloutFileName.TryGetThreadId(transcript_path)`, never the hook
  `session_id`), `OnCodexWindows`, `OnSourceStatus`, `Tick`. It publishes every `CacheSnapshot` to `Snapshots`.
- Behind it: `CacheTracker` with `CacheTrackerOptions` (per-thread TTL and miss state), `ThreadRegistry` with
  `ThreadPulseState` and `ThreadObservation` (independent per-thread state, tombstones), `ActiveThreadResolver` with
  `ResolvedThread` (Auto selection), `SystemClock`.
- `SnapshotPublisher<T>` – hand-rolled latest-value `IObservable<T>`; no Rx.
- `SelectionReasons` and `DiagnosticCodes` – the only values of `ThreadSelection.Reason`, `CacheSnapshot.Diagnostics`
  and the reader's counters. Core and the reader never emit free text; add a constant rather than a string.
- `HookActivityRecorder`/`HookActivitySnapshot` – hook counts for diagnostics; `InMemorySessionEventSource` – the stub
  source for tests and for the overlay before integration.
- `Infrastructure/Rollouts/` – `RolloutTailer` (the live `ISessionEventSource`, raises `ThreadObserved`),
  `RolloutTail` (its synchronous core, used by tests), `RolloutTailerOptions`, `RolloutDiagnostics`.

## Additive contract (#252)

- `PulseEngine.Prune`, `IsEnded`, `StaleAfter` (6 h), `PruneAfter` (24 h), `MaxLiveThreads` (64) and
  `ThreadRegistry.Prune`: threads whose `SessionEnd` never came are dropped with a tombstone; the shown thread idle
  for `StaleAfter` is `Stale`. `ThreadRegistry.End` ignores a thread known as a subagent.
- Win32: `JobProcess` (`StartDetached` with breakaway and fallback, `Current` for `exitsWithJob`) and
  `WindowEnumerator.CountCodexWindowsIncludingCloaked` for the exit recheck.

## Lifecycle (#252)

The rules live in the XML docs of `HookRunner`, `OverlayBootstrap`, `HookPipeServer`, `LastWindowExit` and
`OverlayController`; measurements in `docs/compatibility.md` §12. In short: the overlay takes the mutex and opens the
pipe before WPF, a second overlay exits; a hook starts the overlay only when no mutex exists and never with
`--no-start`; the overlay exits only 3–4 s after the last Codex window is confirmed gone, never on `SessionEnd`.
Only `SessionStart`, `UserPromptSubmit` and `SessionEnd` with a thread id reach the engine; other events are counted.

Tests that start the exe set `CODEX_SESSION_PULSE_HOME` and `CODEX_HOME` to scratch folders (`LifecycleHome`), which
also moves the mutex and pipe names, so they never reach or start your own overlay. They share the `exe` xUnit
collection and run one at a time; `Overlay_ExitsOnlyAfterTheLastCodexWindow` briefly shows two plain windows.

## Logs

The overlay writes `%USERPROFILE%\.codex-session-pulse\logs\overlay.log` (plus `overlay.1.log`…`overlay.4.log`, 1 MB
each); settings are `%USERPROFILE%\.codex-session-pulse\settings.json`. Not `%LOCALAPPDATA%`: MSIX redirects it for
processes started from Codex (`docs/compatibility.md` §3.2). `CODEX_SESSION_PULSE_HOME` moves the root for tests.
Build and test logs go to `scratch/` (ignored).
