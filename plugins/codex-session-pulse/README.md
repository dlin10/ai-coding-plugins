# Codex Session Pulse 0.1.0

Codex Session Pulse puts one small line over the **Codex Desktop** window on Windows: the cached-input and input
tokens of the last request in the active thread and an estimate of how long the prompt cache stays warm. It reads the
rollout JSONL files Codex already writes; the plugin's lifecycle hooks only tell it sooner which thread is active.
Codex-only and Windows-only: it is not in the Claude Code or Cursor catalogs. For Claude Code, see the
[Session Pulse mod](../../mods/session-pulse).

## The line

```text
cache  ▮▮▮▮▮▯▯▯  ~18 min   40.4k/40.8k   Avg speed —   Auto · confirmed   JSONL   Auto Warm
```

- **State and TTL bar.** `cache` with the bar while the estimate runs, `Cache may have expired` after it, `Observed
  cache miss HH:mm` when a request read far less from the cache than the one before, `Last update HH:mm` after 6 hours
  without activity, `Waiting for token usage…` for a new thread and `Codex session not found` without one.
- **`~N min`** is an estimate counted from the last observed request with the configured TTL (30 minutes by default).
  OpenAI publishes no exact TTL, so it is never shown as a fact; a missing request is never shown as a miss.
- **cached/input** are the last request's `cached_input_tokens` and `input_tokens`.
- **`Avg speed —`**: output speed is not measured in this release.
- **`Auto · confirmed` / `inferred` / `unknown`**: the thread is chosen automatically, never by hand. A hook confirms
  it; without hooks, or with two or more Codex windows open, the choice is the newest JSONL activity and says
  `inferred`. Switching tabs in Codex without sending anything leaves it inferred: Codex reports no tab switch.
- **JSONL** is the source; its tooltip counts the hook events received since the overlay started.
- **Auto Warm** is disabled. It sends nothing; the plugin never calls a model.

The overlay never takes focus, is not in Alt+Tab, follows the foreground Codex window across monitors and DPI scales,
hides with it, and exits 3–4 seconds after the last Codex window closes. Tray icon: Show/Hide, Re-detect Codex
windows, Settings, Copy diagnostics, Exit.

## Requirements

- Windows 11 x64 with Codex Desktop (the MSIX app `OpenAI.Codex`).
- The [.NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0). Without it the launcher writes
  `CodexSessionPulse.exe needs the .NET 10 Desktop Runtime x64` to the hook's stderr and the hooks do nothing else;
  Codex keeps working.
- No administrator rights.
- A Windows user name without `&`, `(`, `)`, `@` or `^`: the hook command runs from the plugin folder under your
  profile, and these characters break it before the plugin starts. Start the overlay by hand instead (below).

## Install

In Codex Desktop: **Settings → Plugins**, add the marketplace `dlin10/ai-coding-plugins` if it is not there yet,
open **Codex Session Pulse** and install it. From a terminal the same is:

```text
codex plugin marketplace add dlin10/ai-coding-plugins
codex plugin add codex-session-pulse@dlin10-ai-coding-plugins
```

Then **Settings → Hooks**: the plugin's three hooks show as needing review. Open **Review hooks** and trust them.
Until then Codex skips them without a word, and the overlay does not start by itself.

The first hook of a version downloads `CodexSessionPulse.exe` (about 1 MB) from that version's GitHub release into
`%USERPROFILE%\.codex-session-pulse\bin\<version>\`; later hooks start it straight from there. The download runs a
small PowerShell script with `-ExecutionPolicy Bypass`; an `AllSigned` policy set by Group Policy overrides that and
blocks it. Then put the release's `CodexSessionPulse.exe` into that folder yourself.

### After every update

Every new plugin version needs **Review hooks** again: Codex keys the trust to the plugin's files, and an update
changes them. Codex shows a **Review hooks** button under the prompt box meanwhile. Until you review, the hooks do
not run; an overlay you start by hand still works from the JSONL files and says `Auto · inferred`, its tooltip
says `Hooks not observed: after a plugin update, open Review hooks in Codex`, and **Copy diagnostics** says
`hook events since start: none observed (after a plugin update, open Review hooks in Codex)`. After the review,
the hooks run in threads you start from then on.

## Start it by hand

A supported way, not only a diagnostic one: when hooks are untrusted or turned off by policy, start the overlay
yourself. Through the launcher (it downloads the exe if this version has none yet; `Start-Process` returns at once,
while the launcher waits for the overlay):

```powershell
Start-Process -WindowStyle Hidden "$env:USERPROFILE\.codex\plugins\cache\dlin10-ai-coding-plugins\codex-session-pulse\0.1.0\bin\codex-session-pulse-launcher.cmd" --overlay
```

or the exe directly, once it is there (PowerShell does not wait for it):

```powershell
& "$env:USERPROFILE\.codex-session-pulse\bin\0.1.0\CodexSessionPulse.exe" --overlay
```

A second start exits at once; one overlay runs per user. Started by hand, it waits for a Codex window rather than
exiting when none is open.

## Settings, logs and files

Everything lives under `%USERPROFILE%\.codex-session-pulse\`, not `%LOCALAPPDATA%`: Codex Desktop is an MSIX app,
and Windows redirects `%LOCALAPPDATA%` writes of the processes it starts into the package's private folder.

| Path | What |
|---|---|
| `settings.json` | Tray → Settings writes it: TTL estimate, minimum cached tokens, rollouts folder, theme, offsets, keep visible. A broken field falls back to its default. |
| `logs\overlay.log` | The overlay's log, plus `overlay.1.log`…`overlay.4.log`, 1 MB each. No prompts, no rollout text. |
| `logs\hook.log` | Hook outcomes that need a look (an overlay started, a pipe that did not answer). |
| `bin\<version>\CodexSessionPulse.exe` | The downloaded release, one folder per version. Nothing removes old ones: delete a version's folder once no overlay of that version runs (tray → Exit, then `Remove-Item -Recurse "$env:USERPROFILE\.codex-session-pulse\bin\<old version>"`). |

The rollouts are read from `%CODEX_HOME%\sessions` (`%USERPROFILE%\.codex\sessions` by default) and never written.

## What it will not do

- **No network**, except the launcher's one download of the release asset from github.com per plugin version. The
  overlay makes no connection at all; Auto Warm is off and sends nothing.
- **No exact TTL**: the time left is an estimate from a setting; the `cached_input_tokens` of the next request is the
  only proof that the cache was warm.
- **No output speed** in this release (`Avg speed —`).
- **No manual thread choice**, and no reading of the Codex window's content: a tab switch without a prompt is not
  seen until the next hook or JSONL write.
- **No `SessionEnd` on window close**: Codex sends it only when a thread is archived or ended, so closing Codex
  leaves the threads to age out; the overlay itself exits with the last window.

## Develop

Build, test, package and release steps are in [AGENTS.md](AGENTS.md); the design is in [docs/spec.md](docs/spec.md)
(Russian), the measured Codex Desktop behaviour in [docs/compatibility.md](docs/compatibility.md).
