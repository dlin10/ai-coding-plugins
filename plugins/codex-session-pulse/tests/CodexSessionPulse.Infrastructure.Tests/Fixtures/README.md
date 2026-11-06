# Fixtures

Anonymized captures from a real Codex Desktop on Windows, taken for #250. What each shape means and
how it was observed is in [`docs/compatibility.md`](../../../docs/compatibility.md).

Anonymization keeps structure, numbers and timestamps:

- Ids are replaced by synthetic ones (`00000000-0000-7000-8000-0000000000NN`), consistently across
  every file here, so a hook sample points at the rollout fixture of the same thread.
- Paths become `C:\Users\user\…` or `C:\work\project`.
- Free text becomes `"redacted"`.
- Records #251 has no use for (tool calls, reasoning, world state, …) are reduced to a `{type}` stub
  that keeps the original `ordinal`.

## `rollouts/`

Byte-exact LF files; `.gitattributes` marks this folder `-text` so a checkout does not rewrite them.

| File (id suffix) | Shape |
|---|---|
| `…-000000000001.jsonl` | Desktop main thread, `cli_version` 0.167.0-alpha.3.2. Two turns: the first with a partial cache, the second warm. |
| `…-000000000001.jsonl.zst` | The same bytes, zstd-compressed, as Codex stores a cold rollout. |
| `…-000000000002.jsonl` | Desktop fork of thread 1 (`forked_from_id`, `history_base`; ordinals continue from 33). The parent prefix is not copied; `end_byte_offset` points at the end of the thread-1 fixture. Contains, in order: a cold first turn; a `/Compact` turn (a `token_usage_record` with no `token_count` partner, a `compacted` record, then an all-zero `token_count`); a warm turn; a turn that spawns subagent 3. |
| `…-000000000003.jsonl` | Desktop subagent of thread 2 (`thread_source: subagent`, `parent_thread_id`, `session_id` = thread 2). |
| `…-000000000048.jsonl` | `codex exec` 0.147.0 run whose only `token_count` has `info: null`. Older than `token_usage_record`. |
| `…-000000000053.jsonl` | `codex exec` 0.154.0 subagent with inherited history: its own `session_meta`, then the parent's `session_meta` and the copied parent prefix up to `subagent_history_start_ordinal` (10), then its own records. Both `token_count` events belong to thread 53, not to the parent. |

## `hooks/`

The JSON each hook received on stdin, one file per observed shape. `prompt` and
`last_assistant_message` are redacted.

| File | Event |
|---|---|
| `session-start-startup.json` | `SessionStart`, `source: startup` (thread 1) |
| `user-prompt-submit.json` | `UserPromptSubmit` (thread 1) |
| `session-start-fork.json` | `SessionStart`, `source: fork`, on the fork's first prompt (thread 2) |
| `session-start-compact.json` | `SessionStart`, `source: compact`, on the first prompt after `/Compact` (thread 2) |
| `subagent-start.json` | `SubagentStart`: parent's `session_id`, subagent's `transcript_path` |
| `subagent-stop.json` | `SubagentStop`: parent's `transcript_path`, subagent's `agent_transcript_path` |
| `session-start-fork-ephemeral-suggestion.json` | `SessionStart`, `source: fork`, `transcript_path: null`: Desktop's own next-message suggestion thread |
| `session-end-ephemeral-suggestion.json` | `SessionEnd` of that suggestion thread |
| `session-end-archive.json` | `SessionEnd`, `reason: other`, sent when a thread is archived |
