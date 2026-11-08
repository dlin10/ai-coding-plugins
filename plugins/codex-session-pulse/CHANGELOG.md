# Changelog

## 0.1.0

Initial release.

- **A non-activating overlay over Codex Desktop** shows the active thread's last cached-input and input tokens, a TTL
  estimate with a bar, the selection confidence and the source. It follows the foreground Codex window across
  monitors and DPI scales, never takes focus, is not in Alt+Tab, and exits 3–4 s after the last Codex window closes.
- **Rollout JSONL files are the foundation, hooks an accelerator.** Every main thread and fork keeps its own cache
  state and TTL; the thread is chosen automatically (`Auto · confirmed` by a hook, `inferred` from JSONL, never above
  `inferred` with two or more Codex windows). Without trusted hooks the overlay keeps working from JSONL.
- **One framework-dependent win-x64 exe for both modes.** `--hook` reads the payload from stdin without loading WPF,
  delivers it over a per-user named pipe and starts the overlay when none runs; warm, through cmd and the launcher,
  it takes about 112 ms. `SessionEnd` uses `--no-start` and never starts anything. A hook always exits 0.
- **The launcher downloads the release asset of its version** into `%USERPROFILE%\.codex-session-pulse\bin\<version>\`
  on the first hook, through a file of its own and a rename, safe under parallel hooks. Without the .NET 10 Desktop
  Runtime it prints how to install it instead of letting the .NET host open a dialog inside a hook.
- **Tray**: Show/Hide, Re-detect Codex windows, Settings, Copy diagnostics (no user name, profile path or prompt
  text), Exit. Settings in `%USERPROFILE%\.codex-session-pulse\settings.json`, logs beside it.
- Output speed (`Avg speed —`) and Auto Warm (disabled, sends nothing) are not part of this release.
