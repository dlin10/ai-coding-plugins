# AGENTS.md

Codex-only Windows plugin: a small WPF overlay over Codex Desktop with the last observed cache-read
tokens and a TTL estimate. The source of truth is `docs/spec.md` (Russian); the product view is
`docs/prd.md`.

## Layout

- `CodexSessionPulse.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `global.json` at the plugin root.
- `src/CodexSessionPulse.App` – the only WinExe, `CodexSessionPulse.exe`: `--hook`, `--overlay`, `--no-start`.
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

## Rules

- **No WPF in the `--hook` path.** Hook warm start must stay within 150 ms. `Program` and `HookRunner` must not
  touch a WPF type; WPF is reached only through `OverlayHost`, called with `NoInlining`. The test
  `Hook_WithJsonOnStdin_ExitsZeroWithoutLoadingWpf` enforces it (set `CODEX_SESSION_PULSE_DEBUG_ASSEMBLIES=1`
  to make `--hook` print the loaded assemblies).
- **The spec §4.3 contract in Core is frozen.** Do not rename, reorder or add members to the public types in
  `CodexSessionPulse.Core`; changes go through the orchestrator.
- The hook never writes stdin anywhere, reads nothing from the current directory, and always exits 0.
- Every public type and member in Core carries a complete XML doc comment; the build fails without one.

## Logs

None are written yet. Build and test logs go to `scratch/` (ignored); the overlay's own log location is decided with the overlay.
