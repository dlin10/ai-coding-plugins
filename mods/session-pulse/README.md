# Session Pulse 0.3.1

A Claude Code mod that keeps the session's cost in view above the prompt and keeps the prompt cache from
going cold while you are away.

```
cache   ██░░░░░░ 18 min  5h ░░░░░░░░ 3% resets in 3h 2m  week ████░░░░ 44%               [Auto]
```

## The row

- **The countdown and the limits.** How much of the prompt cache's life is left, counted from the main
  conversation's last request (blue, orange once a quarter of it is left, `cold` once it has gone); the
  five-hour window's share and time to reset; the weekly window's share. The limits are the ones the
  status line gets from the API's rate-limit headers, so off a subscription there are none. A resumed or
  reloaded session keeps the windows it last showed until its first request brings new ones.
- **Auto** opens a small dialog of the mod's own asking how long to keep the cache warm —
  `1 h (1 warm-up)`, `2 h (2 warm-ups)`, `4 h`, `8 h`, or a number of hours typed under **Other** (no
  field on mobile); **Cancel** or Esc closes it — then warms the cache two minutes before each cooling
  until the hours or the warm-ups run out. The button counts the warm-ups (`Auto 1/2`); pressing it again turns auto-warming
  off. A turn that is running refreshes the cache itself, so no warm-up is sent during one.

`/session-pulse` hides or shows the band. Subagents cache prefixes of their own and do not move the
countdown.

## What warming costs

A warm-up is a one-word fork of the conversation (`$.model.fork`): it is served from the cache, which
restarts the countdown, and it adds nothing to the conversation. It reads the whole cached prefix from the cache, billed at the cache-read rate (a tenth of the
input price), plus a few dozen fresh tokens. Letting the cache go cold costs a write of the whole prefix
at the cache-write rate on the next turn. With an 80K-token conversation an hour's warm-up costs about 8K
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

Install it from the marketplace:

```
/plugin install session-pulse --marketplace dlin10/ai-coding-plugins
```

Or load it from this folder for one session:

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
