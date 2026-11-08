# Codex Session Pulse — domain language

The vocabulary the code, the diagnostics, the log and the docs must use. Collected when the P0 release was packaged,
from the names the spec (`docs/spec.md`) and the Core contract already carry.

| Term | Meaning |
|---|---|
| **Rollout** | One JSONL file Codex writes per thread under `%CODEX_HOME%\sessions\yyyy\MM\dd\`. The **foundation**: every number on the line comes from a rollout. Read-only, tailed incrementally. |
| **Thread** | One Codex conversation, identified by the id in its **rollout** file name, never by a hook's `session_id`. Its **kind** is main, fork or subagent; only main threads and forks are shown. |
| **Token usage** | The last `token_count` of a **thread**: cached input, input, and when it was written. A **snapshot** carries the latest one. |
| **Prompt cache** | OpenAI's server-side cache the **token usage** reports on. Not to be confused with the **release cache**. |
| **TTL estimate** | How long the **prompt cache** probably stays warm after the last request: a setting (30 min by default), never a measured fact. Shown as `~N min` and a bar. |
| **Cache state** | What the line says about the **prompt cache**: NoSession, WaitingForUsage, Warm, ExpiringSoon, EstimatedExpired, ObservedMiss, Stale, Error. |
| **Observed miss** | A request whose cached input fell far below the previous one: the only state that claims the cache was cold, because the data proves it. An expired **TTL estimate** is EstimatedExpired, not a miss. |
| **Stale** | The shown **thread** has been idle for max(6 h, **TTL estimate**). |
| **Selection** | Which **thread** the line shows, chosen by `ActiveThreadResolver` and always automatic (Auto). Its **confidence** is confirmed (a **hook** named it), inferred (newest **rollout** activity) or unknown; two or more Codex windows cap it at inferred. |
| **Hook** | A Codex lifecycle hook of this plugin: `SessionStart`, `UserPromptSubmit`, `SessionEnd`. An **accelerator** only: it confirms a **selection** sooner and starts the **overlay**; losing one costs nothing but speed. |
| **Review hooks** | Codex's trust step for a plugin's **hooks**. Needed after install and after every version bump; until then Codex skips the hooks silently. |
| **Overlay** | The long-lived `CodexSessionPulse.exe --overlay` process: the line, the tray icon, the **pipeline**. One per user, held by the **mutex**. |
| **Hook run** | The short-lived `CodexSessionPulse.exe --hook` process: reads stdin, sends one **hook message**, starts the **overlay** when none runs (never with `--no-start`), exits 0. Never loads WPF. |
| **Hook message** | What a **hook run** sends over the **pipe**: event, **thread** id, `SessionStart` source, time. No prompt, path or `cwd`; event and source are mapped to a fixed set. |
| **Channel** | The per-user **mutex** `Local\CodexSessionPulse.<SID>.<major>` and **pipe** `CodexSessionPulse.<SID>.<major>`; a **home** other than the default adds its hash to both. |
| **Pipeline** | One `RolloutTailer` feeding one `PulseEngine`; replaced as a whole when the rollouts root or the tracker settings change, and given the last hook-confirmed **selection** again. |
| **Anchor** | The foreground Codex Desktop window the **overlay** is placed over (`ChatGPT.exe` of package `OpenAI.Codex`, class `Chrome_WidgetWin_1`). NotFound means no Codex window; the **overlay** exits 3–4 s after that is confirmed. |
| **Tombstone** | What an ended or pruned **thread** leaves behind, so its old **rollout** cannot bring it back; a new `SessionStart` or later activity lifts it. |
| **Home** | `%USERPROFILE%\.codex-session-pulse\`: settings, logs and the **release cache**. `CODEX_SESSION_PULSE_HOME` moves it for tests. Never `%LOCALAPPDATA%`, which MSIX redirects for processes Codex starts. |
| **Launcher** | `bin\codex-session-pulse-launcher.cmd`, what `hooks.json` runs: finds the exe of the manifest version in the **release cache** (the fast path, `if exist` and nothing else) or has the **helper** download it. |
| **Helper** | `bin\codex-session-pulse-cache.ps1`, the launcher's slow path: downloads the **release asset** through a file of its own and an atomic rename. The plugin's only network request. |
| **Release asset** | `CodexSessionPulse.exe` attached to the GitHub release `codex-session-pulse-v<version>`. Never in git. |
| **Release cache** | `<home>\bin\<version>\`, where the **launcher** looks for the **release asset**. |

## Flagged ambiguities

- "Session" was used for both a Codex **thread** and a hook's `session_id`, which is not the rollout's thread id —
  resolved: the code says **thread**, and the thread id comes only from `transcript_path`.
- "Cache" was used for the **prompt cache** and for the exe's folder — resolved: the folder is the **release cache**,
  "cache" alone means the **prompt cache**.

## The JSONL is the truth; hooks only hurry it

A hook can be untrusted, skipped, late or lost, and the overlay must show the same numbers without it. So a hook never
carries a number: it only confirms which **thread** is active, and the **rollout** says what happened.

## The version is read from one place

The manifest's top-level `"version"` names the **release cache** folder, the exe's version and the release tag. The
**launcher** and `Directory.Build.props` read the same line, two spaces in; tests and `build/package.ps1` fail on any
drift, and a manifest that does not have that line is refused rather than guessed at.
