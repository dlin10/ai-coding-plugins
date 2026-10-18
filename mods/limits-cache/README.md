# Limits & Cache 0.1.0

A Claude Code mod that keeps two rows above the prompt:

```
Лимиты 5ч ████░░░░░░ 42% ↻ 2ч 14м   неделя █████████░ 88% ↻ 4д 3ч
Кэш    ● тёплый 3:41 (TTL 5м, изучен) · попадание 97% · в кэше 182k
```

- **Лимиты** — the subscription's rate-limit windows (`five_hour`, `seven_day`, and any others the API
  reports): how much of each is used, colored green under 50%, yellow under 80%, red above, and how long
  until it resets. The figures are the ones the status line gets from the API's rate-limit headers; off a
  subscription there are none, and the row says so.
- **Кэш** — the prompt cache of the main conversation. Every request refreshes the cache entry it reads,
  so the countdown runs from the last request the main thread sent. While it is warm the row shows the
  time left, the share of the last request's input the cache served, and how large the cached prefix is.
  Once it is cold the row says how long ago it expired and how many tokens the next request will have to
  write to the cache again. Subagents cache prefixes of their own and do not move the countdown.

The band redraws every second while the cache is warm and every 30 seconds otherwise. `/limits-cache`
hides or shows it.

## The cache TTL

The API does not report how long a cache entry lives, so the mod learns it. With `cacheTtl` set to
`auto` (the default) it starts at 5 minutes, and:

- a request that still reads most of the previous prefix from the cache after more than 5½ minutes of
  quiet proves a 1-hour TTL;
- a request on the same model that reads almost nothing after 5½ minutes to an hour of quiet points
  back to 5 minutes.

The learned value is kept across sessions and shown as `TTL 1ч, изучен`. Set `cacheTtl` to `5m` or `1h`
in `/config` to pin it instead.

## Install

Load it from this folder for one session:

```bash
claude --plugin-dir /path/to/ai-coding-plugins/mods/limits-cache
```

Or load it in every session by naming the folder in `~/.claude/settings.json`:

```json
{
  "env": {
    "CLAUDE_CODE_PLUGIN_DIRS": "/path/to/ai-coding-plugins/mods/limits-cache"
  }
}
```

Mods are a Claude Code-only feature; this one is written and tested against Claude Code 2.1.302.

## Develop

```bash
claude plugin validate mods/limits-cache
claude plugin test mods/limits-cache
```

Once Claude Code has loaded the mod it lays the API's types in `.claude-plugin/types/` (ignored by git),
and `tsc -p mods/limits-cache` type-checks it.
