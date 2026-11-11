# Codex Session Pulse 0.2.0

Codex Session Pulse puts one small line over the **Codex Desktop** window on Windows: an estimate of how long the
prompt cache of the open thread stays warm. It reads the rollout JSONL files Codex already writes and, to know which
thread is open, one line of Codex Desktop's log; the plugin's lifecycle hooks only tell it sooner.
Codex-only and Windows-only: it is not in the Claude Code or Cursor catalogs. For Claude Code, see the
[Session Pulse mod](../../mods/session-pulse).

## The line

```text
cache  ▮▮▮▮▮▯▯▯  ~18 min · Avg speed 31 tok/s                                      Auto Warm
```

- **State and TTL bar.** `cache` with the bar while the estimate runs, `Cache may have expired` after it, `Observed
  cache miss HH:mm` when a request read far less from the cache than the one before, `Last update HH:mm` after 6 hours
  without activity, `Waiting for token usage…` for a new thread and `Codex session not found` without one.
- **`~N min`** is an estimate counted from the last observed request with the configured TTL (30 minutes by default).
  OpenAI publishes no exact TTL, so it is never shown as a fact; a missing request is never shown as a miss.
- **Which thread.** It is chosen automatically, never by hand. Codex Desktop's own log names the page on screen at
  every switch, a click in the sidebar included, and the line follows it. On New chat, or on a thread with no rollout
  on this computer (a remote host's thread, a ChatGPT project chat, a cloud task), the line hides instead of showing
  another thread's time. Until the log names a page, a hook confirms the thread; without either, the choice is the
  newest JSONL activity.
- **Hover the state** for the last request's time, the model, the source, how the thread was chosen (`Auto ·
  confirmed`, `inferred` or `unknown`; two or more Codex windows cap it at `inferred`) and the hook events received
  since the overlay started. Cached and input tokens are in Copy diagnostics.
- **`Avg speed N tok/s`**: output tokens per second over the thread's last 10 model requests, each from sending the
  request to the end of its answer. Tool time is left out, but the wait for the first token is in, since Codex does
  not record when it came, so the model itself generates faster. `—` until a request has been timed.
- **Auto Warm** is disabled. It sends nothing; the plugin never calls a model.

The overlay never takes focus, is not in Alt+Tab, follows the foreground Codex window across monitors and DPI scales,
hides with it, and exits 3–4 seconds after the last Codex window closes. Tray icon: Show/Hide, Re-detect Codex
windows, Settings, Copy diagnostics, Exit.

### Where it sits

Just above the Codex input box, centred on it and as wide as it; it hides while one of Codex's menus or panels, such as
the model menu or the "+" panel, opens upward over that spot. It follows the box when you open or collapse the side panels, when the
row of project chips appears, and when the prompt grows to several lines. UI Automation finds the box; see "What it
will not do". On pages without an input box, such as Codex's Settings, the line sits centred on the window, 170 DIP
above its bottom edge.

Settings → **Position** chooses between the two:

- `AboveCodexInput`, the default: above the input box, or by the window while there is none.
- `BottomOfCodexWindow`: always by the window, without UI Automation.

**Offset X** and **Offset Y** shift either position in DIP. After an update from 0.1.0 the offsets start at 0 again:
0.1.0 measured them from the window's bottom edge.

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
Start-Process -WindowStyle Hidden "$env:USERPROFILE\.codex\plugins\cache\dlin10-ai-coding-plugins\codex-session-pulse\0.2.0\bin\codex-session-pulse-launcher.cmd" --overlay
```

or the exe directly, once it is there (PowerShell does not wait for it):

```powershell
& "$env:USERPROFILE\.codex-session-pulse\bin\0.2.0\CodexSessionPulse.exe" --overlay
```

A second start exits at once; one overlay runs per user. Started by hand, it waits for a Codex window rather than
exiting when none is open.

## Settings, logs and files

Everything lives under `%USERPROFILE%\.codex-session-pulse\`, not `%LOCALAPPDATA%`: Codex Desktop is an MSIX app,
and Windows redirects `%LOCALAPPDATA%` writes of the processes it starts into the package's private folder.

| Path | What |
|---|---|
| `settings.json` | Tray → Settings writes it: TTL estimate, minimum cached tokens, rollouts folder, theme, position, offsets, keep visible. A broken field falls back to its default. |
| `logs\overlay.log` | The overlay's log, plus `overlay.1.log`…`overlay.4.log`, 1 MB each. No prompts, no rollout text. |
| `logs\hook.log` | Hook outcomes that need a look (an overlay started, a pipe that did not answer). |
| `bin\<version>\CodexSessionPulse.exe` | The downloaded release, one folder per version. Nothing removes old ones: delete a version's folder once no overlay of that version runs (tray → Exit, then `Remove-Item -Recurse "$env:USERPROFILE\.codex-session-pulse\bin\<old version>"`). |

The rollouts are read from `%CODEX_HOME%\sessions` (`%USERPROFILE%\.codex\sessions` by default) and never written.

## What it will not do

- **No network**, except the launcher's one download of the release asset from github.com per plugin version. The
  overlay makes no connection at all; Auto Warm is off and sends nothing.
- **No exact TTL**: the time left is an estimate from a setting; the `cached_input_tokens` of the next request is the
  only proof that the cache was warm.
- **No pure generation speed**: `Avg speed` includes the wait for the first token, which Codex does not record.
- **Not under 80 MB of working set yet**: measured at rest with a Codex window open, the overlay's working set is
  113–118 MB, of which 56 MB is its own memory and the rest shared WPF and .NET images (`docs/compatibility.md`
  §13.1). CPU at rest stays under 0.5 % of one core.
- **No manual thread choice**: the open thread comes from Codex Desktop's log, whose format is undocumented. If a Codex
  update drops the line, the overlay falls back to hooks and JSONL, and a tab switch without a prompt is not seen until
  the next hook or JSONL write.
- **One line of Codex Desktop's log.** To know the open thread, the overlay reads the log of Codex's main process in its
  package folder. It decodes only the route line Codex writes at each page switch and keeps only its time and route
  (`/local/<thread id>`, `/` for New chat); other lines are never turned into text, and the overlay's own log records
  the page kind, never the id (`docs/compatibility.md` §6.3).
- **No reading of the Codex window's text.** To sit above the input box, the overlay asks UI Automation where Codex's
  input card is. It reads element types, classes and rectangles, never names, values or text, and only while the line
  is shown. As with a screen reader, this turns on Chromium's accessibility tree inside Codex. Each search of the tree
  costs Codex 16–80 ms of CPU. An A/B test with two Codex restarts could not separate the tree's own cost from noise
  (`docs/compatibility.md` §6.2). Settings → Position → `BottomOfCodexWindow` turns it off.
- **No `SessionEnd` on window close**: Codex sends it only when a thread is archived or ended, so closing Codex
  leaves the threads to age out; the overlay itself exits with the last window.

## Develop

Build, test, package and release steps are in [AGENTS.md](AGENTS.md); the design is in [docs/spec.md](docs/spec.md)
(Russian), the measured Codex Desktop behaviour in [docs/compatibility.md](docs/compatibility.md).
