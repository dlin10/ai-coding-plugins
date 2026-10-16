---
status: accepted
---

# Record the Orchestrator per declared phase, from the host's telemetry export or its transcript

`telemetry.json` recorded Worker attempts only, and `CONTEXT.md` said the Orchestrator's usage was
not observable, so a run's cost had a hole the size of its most expensive participant. Measured on
2026-10-13 with recording stub servers under Claude Code 2.1.296, codex-cli 0.162.1 and
cursor-agent 2026.09.10 (`SCOUT.md` of run `20261013-192041-cd3c8b`):

- Claude Code gives a stdio MCP server `CLAUDE_CODE_SESSION_ID`, the name of its transcript
  `~/.claude/projects/<cwd>/<session>.jsonl`. Each model response is written as one line per
  content block, all carrying the same `usage` under one `message.id`, so summing lines counts it
  2.5–2.7 times; subagents write to `<session>/subagents/`.
- Codex passes the server no environment, but every `tools/call` carries `_meta.threadId`, which
  ends the name of its rollout file; the rollout's `token_count` events carry the thread's running
  total.
- Cursor gives the server neither, and its local transcripts carry no usage.
- The model itself sees no usage counter: the `<total_tokens>` reminder in Claude Code's context is
  a budget for the current prompt, reset by every user message.
- Both Claude Code and Codex export OpenTelemetry per API request — `claude_code.api_request` with
  tokens, `cost_usd`, `duration_ms` and `session.id`; `codex.sse_event` with token counts and
  `conversation.id`, the thread id — but only when the user's own profile turns it on. Project
  settings and plugins cannot, and an MCP server does not inherit the `OTEL_*` variables.
- A host launches only per-session stdio servers. A server shared by every session must be an HTTP
  server, and no host launches one; it only connects.

## Decision

**The Orchestrator declares its phases, and each phase is one record.** The phases are
`interview`, `planning`, `plan_revision`, `approval`, `build`, `code_review` and `wrap_up`, spelled
like the `act` values already in the file. `forge.begin` opens `interview`; the Orchestrator declares
each later phase through an optional `phase` argument of `forge.log.append`, and `done` closes the
last one. The server adds the round or task the run is at, keeps the open phase in a file of its own
in the run folder so that a later server process can close it — not in `state.json`, which a
background Job rewrites while a poll is running — and appends an `orchestrator.usage` record to the
same `telemetry.json` when the phase closes. An unknown phase name is refused.

**A record stands where it opened, not where it was written.** Every record's `at` is its opening:
a Worker attempt's process start, an Orchestrator phase's declaration. A phase is written when the
next is declared, so one that was open while a Critic round ran would otherwise follow that round's
record although it opened first. The file therefore places each record after the last element
whose `at` is no later, found by stepping back from the end; an element without a text `at` stops
the walk, and elements already in the file are never moved (0.47.1). A phase's `wallDuration`
still covers the Workers that ran inside it, which `toolDuration` takes out of `duration`; sum
`duration`, not `wallDuration`, across the two kinds.

**Orchestrator time is the phase's wall time less the union of Plan Forge's own tool calls in it.**
While a call runs the server works and the Orchestrator waits. What remains includes waits for the
user, which nothing here can separate.

**Tokens come from a Usage source chosen once per server process**, when its Host session first
becomes known, behind one interface with two implementations. The telemetry export is used when the host profile sends OTLP http/json logs
to a loopback endpoint and a Telemetry collector of the same protocol version answers there, or can
be started there. Otherwise the Host session's transcript is read: Claude Code's by
`CLAUDE_CODE_SESSION_ID`, de-duplicated by `message.id`, subagents included; Codex's rollout by
`_meta.threadId`, as the growth of its running total. A response counts in the phase in which it
began: the transcript places it at its earliest line, and the export places a Claude request at its
`event.timestamp` less its `duration_ms`, and a Codex response at the latest request-start event
(`codex.websocket_request`, or `codex.api_request` less its duration) of its thread. Both hosts stamp
a response's usage event when the stream ends but dispatch a tool call as soon as the call is ready,
so the response that declares a phase would otherwise land in the phase it opens. On Cursor no source exists and the record has
time only. A source that cannot answer leaves the tokens absent with a reason. It never changes a
tool call's outcome and never falls back to the other source mid-run.

**The Telemetry collector is `planforge.exe` started in a background mode**, by the first server
that needs it, one per endpoint. It holds in memory only the sessions a server registered and only
their usage fields — never prompts, e-mail addresses or account ids — listens on loopback only, and
lives while any server that registered with it is alive, ending a short grace period after the last
one exits. A phase whose window it can no longer cover is marked incomplete. The collector is
started outside the server's job object when that job object permits breakaway, and inside it
otherwise. Claude desktop and Codex desktop are MSIX packages whose job objects refuse breakaway, and
`codex exec` runs its servers in a job object that refuses it and also ends them when it closes
(both measured 2026-10-14). A collector started there ends with its host — the package, or the Codex
session — and the next phase opening of any server that chose the export starts it again.

## Considered and rejected

- **The Orchestrator measures itself.** It has no counter to read. It would have to parse the same
  transcript through a script and report the numbers, which the server cannot check — the hole
  ADR 0003 accepts for approval, here for no gain — and the reading costs the Orchestrator's tokens.
- **Hooks.** None of their payloads carries usage, and ADR 0002 removed them from the plugin.
- **A proxy through `ANTHROPIC_BASE_URL` or `openai_base_url`.** It sees the user's OAuth traffic
  and must pass SSE through untouched, and the Claude desktop app ignores the variable.
- **The transcript alone.** It carries no cost and no API time, and its format is private.
- **The telemetry export alone.** Without the user's own setup it records nothing.
- **The first server takes the endpoint's port.** Only one of several parallel sessions would get
  the export.
- **A shared spool folder.** Files, clean-up and races for what memory gives.
- **A separate binary, a logon task, or a shared MCP server.** Packaging ships one binary and the
  launcher downloads one; a logon task is an installation step outside the plugin that outlives
  upgrades; no host launches a shared server.
- **An idle timeout for the collector.** One `forge.build.next` can outlast it, and the events of
  its phase would go with the collector.
- **No collector from a job object that refuses breakaway, or from one that ends its processes on
  close.** The first two forms of this decision. The first left the export unused under both desktop
  hosts, which is where the user works; the second left it unused under Codex. Both were meant to
  keep a collector other servers use from ending with one host, which the restart now covers.
- **Starting the collector through a broker outside the job object** — WMI `Win32_Process.Create`,
  or a `ShellExecute` through explorer. It escapes every job object, but endpoint protection treats
  it as a suspicious technique, and a restart covers the same loss with less code.
- **Phases derived from the Worker acts.** No skill change, but labels such as "before plan review
  round 2", and an interview cut into pieces by every Scout call.
- **A separate file for Orchestrator records.** The user chose one telemetry file; records are told
  apart by `event`.

## Relation to earlier decisions

ADR 0002 deleted transcript reading together with hooks, to give up enforcement. Here a transcript
is read to observe, never to steer, and nothing in a run depends on what it says. ADR 0024 rejected
reading the Vendors' session files for Worker usage because each Worker's own result reports it;
nothing reports the Orchestrator's, so the private format is accepted here with a failure that only
leaves tokens absent. `AGENTS.md` describes the binary as having one entry point, the MCP
handshake; the collector mode is a second one, started only by the server itself.

## Consequences

`telemetry.json` now holds two kinds of record, and a script that sums every record without looking
at `event` changes its meaning. `at` is local time without an offset, so across a clock change an
hour repeats and a record written in its second pass may be placed among those of the first; files
written before 0.47.1 keep their append order. The Claude and Codex identities the transcript source relies on are
observed behaviour, not documented contracts; a host upgrade that drops them leaves tokens absent
with a reason rather than failing the run. A `/clear` or a resume that changes the Host session
under a running server is unmeasured. A run abandoned before `done` has no record for its open
phase. Closing a phase waits before it reads — under the telemetry export for the host's export
interval plus a second, so that the last response's event can arrive; under the transcript for two
seconds, because a host may write the last response's lines after the call that ends the phase, and
a line written later still is counted in neither phase. The time a server process spent in tool calls
of a phase it did not close is not carried over to the process that closes it. The collector is a background process that
outlives the session that started it, and whatever it held is lost when it ends. One started inside
a host's job object also ends with that host — a desktop package on an update, a Codex session when
it ends — and the events exported before the next phase opening starts it again are lost.
