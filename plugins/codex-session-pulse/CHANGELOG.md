# Changelog

## 0.2.0

**After updating, open Review hooks in Codex Desktop again** (Settings → Hooks): Codex trusts hooks per plugin
content, so a new version needs its hooks reviewed even though `hooks.json` did not change. Until then the hooks do
not run, and an overlay started by hand says `Auto · inferred`.

- **The line sits above the Codex input box** (#392). It is centred over the input card with its bottom edge 8 DIP above
  it, wherever the left and right panels put the chat column, and it moves with the card when the row of project
  chips appears or the prompt grows to several lines. In 0.1.0 the line was centred on the whole window: with the side
  panel open it sat 170 DIP left of the input box, over the Recents list.
- **UI Automation finds the input card**, reading only element types, classes and rectangles, never names, values or
  text. As with a screen reader, this turns on Chromium's accessibility tree inside Codex. Each search of the tree
  costs Codex 16–80 ms of CPU. An A/B test with two Codex restarts could not separate the tree's own cost from noise. On
  pages without an input box, such as Settings, the line sits by the window as in 0.1.0.
- **Settings → Position**: `AboveCodexInput` (default) or `BottomOfCodexWindow`. `BottomOfCodexWindow` reads nothing
  of the window and centres the line 170 DIP above its bottom edge. The offsets now shift either position.
- **0.1.0 settings**: a 0.1.0 `settings.json` gets the default position and drops its offsets, which were measured from
  the window's bottom edge (`schemaVersion` 2). Copy diagnostics reports `settings-v1-offsets-dropped` when they were
  not the defaults.

## 0.1.0

Initial release.

- **A non-activating overlay over Codex Desktop** shows the active thread's last cached-input and input tokens, a TTL
  estimate with a bar, the selection confidence and the source. It follows the foreground Codex window across
  monitors and DPI scales, never takes focus, is not in Alt+Tab, and exits 3–4 s after the last Codex window closes.
  It puts itself back within a second when another process minimizes it, and stays above a Codex window pinned on
  top without ever covering a window that Codex does not.
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
