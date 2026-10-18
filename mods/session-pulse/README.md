# Session Pulse 0.2.0

A Claude Code mod that keeps the session's cost in view above the prompt and keeps the prompt cache from
going cold while you are away.

```
где мы  Пользователь попросил README, агент написал его и ждёт отзыва.        [Скрыть]
кэш     остынет через 5 мин  следующий ход перечитает ~81K токенов  [Подогреть] [Сжать] [Скрыть]
кэш     ██░░░░░░ 18 мин  5ч ░░░░░░░░ 3% 3 ч 2 мин  нед ████░░░░ 44%  [Авто] [Где мы]
```

## The rows

- **кэш — the main row.** How much of the prompt cache's life is left, counted from the main
  conversation's last request (blue, orange once a quarter of it is left); the five-hour window's share
  and time to reset; the weekly window's share. The limits are the ones the status line gets from the
  API's rate-limit headers, so off a subscription there are none.
- **кэш — the warning.** Five minutes before the cache goes cold (two with a five-minute cache), a row
  says so and how many tokens the next turn will read again at full price once it has. It stays, saying
  how long ago the cache went cold, until the next request or **Скрыть**.
  - **Подогреть** reads the cached conversation once with a one-word fork of it (`$.model.fork`). The
    fork is served from the cache, which restarts the countdown, and it adds nothing to the conversation.
  - **Сжать** runs the session's compaction, so the next turn has less to read.
- **Авто** asks how long to keep the cache warm — `1 ч (1 подогрев)`, `2 ч (2 подогрева)`, `4 ч`,
  `8 ч`, or a number of hours of your own — then warms it two minutes before each cooling until the hours
  or the warms run out. The button counts the warms (`Авто 1/2`); pressing it again turns auto-warming
  off. A turn that is running refreshes the cache itself, so no warm is sent during one.
- **Где мы** has Haiku read the conversation and retell, in two or three sentences, what was asked,
  what is done and what comes next. **Скрыть** takes the row down.

`/session-pulse` hides or shows the band. Subagents cache prefixes of their own and do not move the
countdown.

## What warming costs

A warm reads the whole cached prefix from the cache, billed at the cache-read rate (a tenth of the input
price), plus a few dozen fresh tokens. Letting the cache go cold costs a write of the whole prefix at the
cache-write rate on the next turn. With an 80K-token conversation an hour's warm costs about 8K
input-token equivalents, against about 160K for the rewrite.

## The cache TTL

The API does not report how long a cache entry lives. With `cacheTtl` set to `auto` (the default) the mod
starts at an hour, as Claude Code's subscription cache lives, and learns from what the requests say:

- a request on the same model that reads almost nothing from the cache after five and a half minutes to
  an hour of quiet points to five minutes;
- one that still reads most of it after more than five and a half minutes proves an hour.

The learned value is kept across sessions. Set `cacheTtl` to `5m` or `1h` in `/config` to pin it
instead.

## Install

Load it from this folder for one session:

```bash
claude --plugin-dir /path/to/ai-coding-plugins/mods/session-pulse
```

Or load it in every session by naming the folder in `~/.claude/settings.json`:

```json
{
  "env": {
    "CLAUDE_CODE_PLUGIN_DIRS": "/path/to/ai-coding-plugins/mods/session-pulse"
  }
}
```

Mods are a Claude Code-only feature; this one is written and tested against Claude Code 2.1.302.

## Develop

```bash
claude plugin validate mods/session-pulse
claude plugin test mods/session-pulse
```

Once Claude Code has loaded the mod it lays the API's types in `.claude-plugin/types/` (ignored by git),
and `tsc -p mods/session-pulse` type-checks it.
