# Run worker acts as start/poll/fetch jobs on the Cursor host

Limits [0005](0005-code-review-through-the-orchestrator.md) on one path only: one round per call
becomes one round per job on the Cursor host. What 0005 is actually about — the orchestrator's
mandatory turn between rounds, the critic and the builder never talking directly — is untouched,
because a job still carries exactly one round.

Worker acts run for minutes. The Cursor host cancels a `tools/call` at a hard 60 seconds on the
CLI/ACP path, no Cursor schema has a timeout field, and progress cannot reset the clock — the CLI
path sends no `progressToken` at all, and the IDE path does not pass `resetTimeoutOnProgress` (all
measured 2026-08-18, recorded in `CONTEXT.md`). Codex takes `tool_timeout_sec: 3600` and Claude Code takes
`"timeout": 3600000`, so the problem belongs to Cursor alone, and the staff-endorsed pattern there
is a job id returned fast plus polling. Until now `skills/forge/SKILL.md` handled it by warning the
user and pointing at another host — a workaround that concedes a run started in the Cursor Agents
window cannot finish there.

So four tools sit beside the four one-call worker tools. `forge.work.start` takes the act name plus
that act's existing arguments — flat and optional, so the schema still tells an LLM orchestrator
what to send, and the server refuses a call missing what its act requires — spawns the vendor
session and returns a `jobId` at once. `forge.work.poll` long-polls server-side for a fixed 45 seconds, 15 short
of the host's clock and deliberately not configurable, then reports `running`, `succeeded` or
`failed`, together with the time of the last stdout line and a bounded description of the last
recognised vendor event. `forge.work.cancel` requests cancellation without waiting for the process
to exit; a terminal job is a successful no-op, while a running job is polled and fetched normally
afterward. The orchestrator calls it only on an explicit user request, or after showing the liveness
fields and obtaining confirmation. `forge.work.fetch` returns exactly what the one-call tool
returns. Everything that makes the result trustworthy stays where it was: the server alone composes
worker prompts, spawns vendor processes, validates schema output, appends `review-log.md` and
`flow_log.md`, and holds the builder's resume token. The one-call tools remain the surface for
Claude Code and Codex, and routing
is advisory per [0002](0002-mcp-server-surface-without-enforcement.md) — the job tools are
registered for every host, the skill routes on the `client` that `forge.begin` already returns, and
no code refuses a host that picks the other surface.

Three details are load-bearing rather than incidental. A completed job's result is persisted under
`.forge/<runId>/jobs/`, and `fetch` is idempotent: the round runs once, but reading it does not
consume it, so a result that reached a window which then died is not a round burned for nothing.
One job per run may be active, and `start` refuses while one is running by *returning* the active
act and `jobId` rather than failing — an orchestrator that lost the id, or a fresh one that never
had it, rejoins by polling instead of waiting out an act it cannot see. And `forge.status` carries
the active job and the same liveness fields beside the drift, because it is the tool the skill
already calls to answer "where are we".

Rejected: a triple per act, which is twelve tools for no added meaning; wiring `IVendorSession.Events`
to MCP progress, measured useless on Cursor — no token to attach to and no clock that progress
extends; and delegating the workers to Cursor's own subagents, which moves prompt composition and
result reporting into the orchestrator's hands, so the critique stops being tamper-evident. That
last one matters: the trust extended to the orchestrator in 0002, 0003 and 0005 deliberately stops
short of "the critique is what the critic said", and a subagent also loses the builder's persistent
session and per-call model selection.

The costs, named. The surface grows by four tools and the skill by a routing rule, and the two
surfaces must stay behaviorally identical with no test that spans hosts. The orchestrator can
abandon a job by not polling — the same abandonment 0002 already accepts, now with a running process
attached. A worker has no fixed wall-clock limit: every vendor attempt is instead reaped after 30
minutes without a stdout line, and every line resets that idle window whether or not its event is
recognised. Explicit cancellation and `ApplicationStopping` are the other two reapers while the
server lives. A server killed outright can still orphan a worker that is producing output — on
Windows a child does not die with its parent, and this codebase uses no job object. A critic orphaned
that way is harmless because every vendor keeps it read-only; an orphaned builder can keep editing a
workspace whose run is gone.
