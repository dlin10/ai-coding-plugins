# Record a resumed attempt as the growth of its session's total, and keep the total

A Usage record is per attempt ([0020](0020-separate-worker-telemetry-from-the-run-log.md)), but two
Vendors report some counters for the whole persistent session instead. Codex's `turn.completed`
carries the thread's running token total, which `exec resume` restores from the rollout, and
claude's `total_cost_usd` and per-model `modelUsage` are the session's, which `--resume` restores
from the transcript's `cost-state`. So every resumed Builder and Scout turn recorded everything its
session had spent so far: a codex fix turn of 8.8M input tokens was recorded as 81.8M, and claude's
cost rose the same way. Critics are fresh every round and were never affected. `CONTEXT.md` has the
measurements.

Claude's tokens are read from `modelUsage`, summed over its models, rather than from the result
line's `usage`. `usage` is the turn's own but covers the main loop alone: a Scout that ran four
subagents held 0.4M input tokens there against 24.6M in `modelUsage`, and a turn that auto-compacted
lost the compaction's million. `modelUsage` counts both, agrees with `total_cost_usd`, and, being a
running total like codex's tokens, needs nothing beyond the rule below.

**A resumed attempt records what the running total grew by since its session's latest recorded
report, and keeps the reported total beside it as `sessionTotal`.** The Vendor decides which of its
counters are running totals, because that is a fact about its CLI; the shared telemetry layer
supplies the previous report, because the session object lives for one tool call and only the
Run's `telemetry.json` outlives it. The file is read for it under the same per-file gate as the
append, so the report and the record come from one read. The previous report is, counter by
counter, the latest record of the same Vendor and session: its `sessionTotal` where it kept one,
its own counter otherwise — which is the report itself for a fresh attempt and for a Vendor that
reports per attempt. A total below the previous report is a Malformed usage field, named at its
source path and omitted, and a claude result that names no model is no report at all.

Considered and rejected:

- **Summing the session's earlier growths instead of keeping the total.** No new field, and exact
  while every report is consistent. But one inconsistent report leaves the sum ahead of the Vendor
  for the rest of the session, so later growths come out negative or silently too small.
- **Reading the Vendor's own session files** — codex's rollout, claude's transcript — for the
  baseline. Exact even across a cancelled turn, but both locations and formats are private to each
  CLI and change without notice, and Plan Forge would have to find them under the user's profile.
- **Per-turn fields in the stream.** Codex's `--json` offers none. Claude's `usage` is per turn but
  leaves out subagents and compaction.

What this costs: a codex attempt cancelled before `turn.completed` has no counters, but its spend
stays in the thread's total, so the next reported attempt of that thread carries it — measured, a
1:15 retry carried the 40.4M of the hour-long turn cancelled before it. That keeps a session's
records adding up to what the Vendor reports it spent, at the price of one inflated attempt; a
cancelled turn that no reported one follows is not recorded at all. Cursor's counters on a resumed
chat are unmeasured and taken as reported until someone measures them.
