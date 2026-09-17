---
name: hunt
description: Hunt for races and lost updates in a .NET repository, collect deterministic evidence, compose validated narratives, and render the report. Use only when the user explicitly invokes the hunt.
disable-model-invocation: true
argument-hint: "[target]"
---

# Hunt for concurrency defects

Treat the repository as read-only. Never edit application code. Read [composing.md](composing.md)
before composing any narrative.

1. Resolve the target to an absolute path: use the command argument when supplied, otherwise use
   the workspace root. Call `run_start` with that path.
2. Handle the `run_start` answer with exactly one of these branches:
   - When there is no `run_id`, `candidatesTotal` is greater than zero, and the answer contains
     `candidates`, treat them as file names inside the directory passed to `run_start`. Ask the user
     once to choose. When `candidatesTotal` is greater than the number of names shown, also say that
     the user may provide any solution path instead. If the user chooses a shown name, join it to
     that directory and call `run_start` with the resulting absolute path; if the user provides a
     solution path, resolve it to an absolute path and use it directly. If there is no answer, stop
     and say that no run was started because the target is ambiguous, list the returned candidates,
     and do not claim a `run_id` or report.
   - On `runActive`, report the active run id and stop.
   - On `targetPathTooLong`, report the error and stop.
   - Otherwise retain the returned `run_id`, `deadline`, and `resolvedTarget` and continue.
3. Call `run_poll` after 5, 10 and 20 seconds, then every 30 seconds until `state` is not `running`.
   If the state is `failed`, call `render_report`, handle its answer as in step 8, and report the
   failed run. Otherwise continue with narrative work.
4. Page `get_groups` to the end. Each group digest carries `findingCount` and `occurrenceCount`, the
   group's findings and the ways they are reached, and each listed finding its own `occurrenceCount`.
   Dispatch group narratives in this order: High, then Medium, then
   Low while `remaining` allows. Use the host's subagents: Codex, Claude Code, and Cursor each have
   one when their host supports subagents; without subagents, compose in this session. Give each
   subagent at most three groups, run subagents in parallel as far as the host allows, and require
   every composer to follow [composing.md](composing.md).
5. Call `submit_narrative` for each completed group narrative. On `accepted`, continue. On
   `rejected`, fix the returned reasons and resubmit once; do not resubmit for any other answer and
   never resubmit more than once. Handle `notReady` or any other answer by reporting it and moving
   to rendering without another submission.
6. Only after all High and Medium group submissions have been handled, compose an executive summary
   from their accepted narratives and submit it with target `summary`. Handle that answer under the
   same accepted/rejected rule. Low narratives may then continue while `remaining` allows.
7. Apply this deadline branch before every other response branch only when `deadlineExceeded` is
   `true`, `error` equals `deadlineExceeded`, or `status` equals `late`. A
   `deadlineExceeded: false` flag continues through the normal response flow. When the deadline
   branch applies, stop starting AI work, cancel running subagents as far as the host allows, and
   make no further `get_groups` call or `submit_narrative` submission. Go straight to
   `render_report`.
8. Call `render_report`. On `renderFailed`, retry after 5 seconds; retry only after
   `renderFailed`, and retry at most twice. If it still fails, reply with the run id, the error
   message, and the sentence "no report was written". For every other answer, do not retry. On
   success, reply with the status, reasons, counts, and a link to `reportPath`.

Every server answer must enter exactly one branch above. Do not start a subagent, submit a
narrative, or call `get_groups` after an answer has `deadlineExceeded: true`, an `error` equal to
`deadlineExceeded`, or a `status` equal to `late`.
