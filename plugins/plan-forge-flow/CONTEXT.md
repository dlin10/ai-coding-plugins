# Plan Forge Flow — domain language

The vocabulary the code and prompts must use. The old vocabulary conflated "host" and "provider";
these terms replace it.

| Term | Meaning |
|---|---|
| **Vendor** | A model supplier that can do work in a separate process: Claude Code CLI, Codex App Server, Cursor Agent, later Grok. Not "provider" — that word was overloaded. |
| **Orchestrator** | The host agent: runs the interview, **revises the plan in response to critique**, calls the tools. Always an LLM, never a C# class. Strong model. |
| **Act** | A major stage of a run. The four delegated acts are classes: `PlanReview`, `Build`, `CodeReview`, `ReviewFix`. The interview is not an act class; it lives in the orchestrator. |
| **Critic** | The vendor role that **judges**: reviews the plan, reviews diffs. A fresh process each round, fed the review log as input. |
| **Builder** | The vendor role that **implements**: writes code against plan tasks and fixes code-review findings. Never revises the plan. Persistent session. Cheap model. |
| **Run** | One pass, keyed by `runId`, isolated under `.forge/<runId>/`. |
| **Flow log** | The user-facing timeline of a run, `flow_log.md`: every critique, build result and fix round, appended by the server and never fed back to a worker. Distinct from the review log, which is critic input. |
| **Interview mode** | The orchestrator's choice between an interview without documentation and one that maintains the domain model as it goes. |
| **Capability profile** | What a given host can actually do. Two profiles were designed, `canvas` and `text`; only `text` is built — see below. |

## Tier asymmetry is a design-wide constraint

The roles are **not interchangeable in model strength**, and the old code did not express this — its
`ForgeRole.Reviewer` and `ForgeRole.Builder` chose models the same way. Here the tier is part of the
role's definition:

- **Orchestrator (strong)** holds the interview context, which is why it, and not a worker, revises
  the plan. A fresh reviser process does not have that context, so the revision cannot be delegated
  to a vendor.
- **Builder (cheap)** works against an already-hardened plan, where the decisions were made for it.
- **Critic** judges; the user picks the tier, defaulting nearer the strong end.

The direct consequence for the tool surface: **neither review loop can live inside a single call**,
because a turn by the host LLM is mandatory between rounds. For plan review the orchestrator revises
the draft; for code review it filters the critique against the approved plan before the builder sees
it, because only it knows what the plan deliberately left out. The code-review loop used to be
sealed inside one call on the belief that the orchestrator was not needed there; running the flow on
this repository disproved it — see `docs/adr/0005`. A finding the orchestrator defers is recorded in
the review log with its reason, so the next round's fresh critic reads it as settled.

## The critic is fresh each round, but reads the review log

A persistent critic defends its own earlier assessment and normalises what it has already read,
degrading the exact capability it was hired for. A naively fresh critic oscillates: round 3 reopens
what round 1 accepted. But that oscillation comes from missing **information**, not missing memory —
so the fresh process receives the review log as input data ("here is what was raised, here is how it
was closed") and converges without inheriting the anchoring. Judging someone else's prior findings
and defending your own are different acts. The cost is nil: the plan is a few kilobytes and the
system prompt is identical, so caching applies.

Interface consequence: `CanResume` is needed only by the Builder; the Critic is always stateless.

## The App Server spells its sandbox two different ways

Measured against `codex` 0.147.0 on 2026-08-15, by sending a deliberately invalid value and reading
the variants back out of the error:

- `thread/start` takes `sandbox` as a **kebab-case string**: `read-only`, `workspace-write`,
  `danger-full-access`.
- `turn/start` takes `sandboxPolicy` as an **object whose `type` is camelCase**: `readOnly`,
  `workspaceWrite`, `externalSandbox`, `dangerFullAccess`.

The old client sent `readOnly` to `thread/start`, which today's server rejects outright. Nothing
warns about this: the two spellings are close enough to look like a typo rather than a protocol fact.

Two more properties of the same surface, both measured rather than documented: `effort` is *not*
validated by the App Server — a bad level is accepted, forwarded, and fails the turn upstream, so it
surfaces as a failed turn rather than a rejected request. And `thread/resume` needs the thread to
have a recorded rollout, which only exists after a turn has completed; a thread that was opened but
never used cannot be resumed. That is why a Builder's resume token is only worth storing once its
first task is done.

## Each vendor keeps the critic read-only by a different mechanism

The critic judges and must not edit what it is judging — including through any subagent it spawns.
Nothing in this codebase enforces that; all three guarantees are the vendor's, and they are not the
same guarantee:

- **Codex** — the thread opens with `sandbox: read-only` and each turn repeats it as
  `sandboxPolicy: readOnly`. A real sandbox.
- **Claude** — `--permission-mode acceptEdits` is passed only for a Builder, so a critic's edit
  tools are simply never pre-approved.
- **Cursor** — `--mode plan`, and nothing else. Measured on 2026-08-15 rather than taken from the
  help text: the same prompt asking for a file writes it without the flag and writes nothing with
  it, at the same latency. Before that flag was added, `--force` went to every role and a Cursor
  critic could edit freely.

## cursor-agent rejects an unknown model fast, and its ids carry the effort

Measured against cursor-agent 2026.08.11-e8db854 on 2026-08-18:

- A model id the CLI does not recognise fails in about ten seconds: exit 1, stderr
  `Cannot use this model: <id>. Available models: <the full line-up>`. The existing
  `StreamingProcess` nonzero-exit path surfaces that stderr, so a rejected model already fails the
  act fast — it never crawls toward a timeout. The CLI drains stdin before validating the model
  (measured with a 204 KB prompt), so the prompt-size pipe race one might suspect does not exist.
- The live line-up is full ids with the effort baked in as a suffix — `gpt-5.6-sol-xhigh`,
  `claude-opus-5-thinking-max`, `gpt-5.3-codex-high-fast` — so a string that looks like an
  orchestrator-invented model-plus-effort join can be a real id. The payload issue #19 suspected,
  `model: "gpt-5.6-sol-xhigh", effort: null`, is valid and runs: the id resolves to "GPT-5.6 Sol
  272K Extra High".
- The immediate MCP-layer timeout in run `20260818-123941-05cfa8` was therefore the **host's**
  tool-call timeout on a long-running review, not a vendor rejection: the identical critic
  invocation (`--mode plan`, same model) completes standalone, with ~35–40 s of CLI spin-up before
  the API call even starts. Codex is configured around exactly this — `.mcp.json` sets
  `tool_timeout_sec: 3600` — while Cursor's manifest has no such knob, and none exists to add: see
  "No progress notification can rescue a Cursor-hosted call".

## cursor-agent has no system-prompt channel

The `--help` of 2026.08.11-e8db854 lists no flag resembling `--system-prompt` — nothing like
Claude's `--append-system-prompt` or the App Server's `developerInstructions`. Role instructions
can reach a Cursor worker only inside the prompt itself, so `CursorAgentSession` puts them at its
head, ahead of the task and the schema contract. Before 0.12.1 the loaded role prompt was silently
dropped and Cursor critics and builders ran without their instructions.

## No progress notification can rescue a Cursor-hosted call

Measured on 2026-08-18 with a probe MCP stdio server that logs every request's `_meta`, driven by
cursor-agent 2026.08.11-e8db854 in print mode:

- cursor-agent's own MCP client sends **no `progressToken`** with `tools/call` — `_meta` is absent
  outright — so on this path a server has no token to attach progress to, and the SDK-injected
  `IProgress<>` would be its documented no-op.
- The call is cancelled at a hard **60 seconds**: `notifications/cancelled` arrived 60.0 s after
  `tools/call` while the tool was still working, and the agent reported `MCP error -32001: Request
  timed out`. That is the error signature of run `20260818-123941-05cfa8`, whose review died from
  the Agents window while the identical critic invocation completes standalone.
- Documented rather than measured (Cursor staff on the forum, May–July 2026): no Cursor schema —
  `mcp.json`, plugin `mcp.json` blocks, or the published plugin MCP schema — has any timeout
  field; the IDE path does send a token but `resetTimeoutOnProgress` is not passed to the SDK, so
  progress never extends any Cursor clock; the IDE ceiling is around 60 minutes against the
  CLI/ACP path's 60 seconds, none of it configurable; and progress rendering in the chat and
  Agents UI is a regression open since 3.8. The staff-endorsed pattern for long tools is a job id
  returned fast plus polling.

The consequence: neither lever the Codex host gets — `tool_timeout_sec` or progress keep-alive —
exists for Cursor, so a worker call orchestrated from Cursor dies at the host layer whenever it
outlives the host's clock. Wiring `IVendorSession.Events` to MCP progress was considered on these
measurements and rejected; the channel stays deliberately unread. The only shape a Cursor host
would honor is splitting each worker tool into start/poll/fetch calls that return in seconds — a
surface redesign that collides with one-round-per-call (docs/adr/0005) and needs an ADR of its own
if it is ever taken.

## Claude Code aborts a silent call, and the manifest timeout feeds both of its clocks

Measured on 2026-08-18 against Claude Code CLI 2.1.234, headless, with the same probe server:

- `tools/call` carries `_meta: { "claudecode/toolUseId": …, "progressToken": … }` — the token is
  sent, so a server-side `IProgress<>` would reach the wire.
- A silent tool call is aborted client-side: "sent no response or progress for 30s; aborting", with
  `CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT=15000` set (the abort came at 30 s, so treat the configured
  value as approximate). The abort message itself names the knobs: a per-server `"timeout"` in
  milliseconds in the server entry, or that global idle variable, `0` to disable.
- `notifications/progress` feeds the idle timer: the same 45-second tool emitting progress every
  5 s under the same idle setting ran to completion.
- The per-server `"timeout"` field is honored and is a hard wall clock that progress does not
  extend: with `"timeout": 20000`, the call died at 20 s despite progress every 5 s.

Hence `.claude-plugin/plugin.json` sets `"timeout": 3600000` on the server entry — the hour the
Codex host grants through `tool_timeout_sec` — raising the wall clock and lifting the idle floor
for this server alone. The documented stdio defaults (≈28 h wall clock, 30 min idle) would
otherwise abort a worker call whose two 20-minute vendor attempts run back to back. The field was
measured through `--mcp-config`; the plugin manifest declares its server with the same entry
schema, which is the one assumption not yet measured end to end.

## Only the `text` profile exists

Measured on 2026-08-15 against a spike server built on the MCP C# SDK 2.2.0: Claude Code 2.1.233 and
Cursor 1.0.0 both negotiate protocol `2025-11-25` and report `extensions: null` with no UI
capability. Neither the MCP Apps extension (the `canvas` profile) nor the Tasks extension (streamed
progress) is negotiated by any available host.

So the plan is delivered as markdown in the tool result, and progress is observable only at the
granularity of one tool call per unit of work, plus `forge.status` on demand. The `canvas` branch is
not written until a host negotiates the capability; `McpApps.GetUiCapability(...)` from the SDK is
the check that would enable it.

That spike is dated: run `20260818-123941-05cfa8`, orchestrated from Cursor 3.15, recorded
`profile: "Canvas"` in its state — the detector only says that when `McpApps.GetUiCapability`
returns non-null, so current Cursor **does** negotiate the UI capability. The `canvas` branch is
still unwritten; the profile now merely has a potential customer.

## Surfacing the flow log is the orchestrator's act, and each host differs

No MCP mechanism lets this server make a host display a file — resources and notifications can
carry one, but nothing renders unasked — so showing `flow_log.md` belongs to the orchestrator,
and the skill instructs it per host.

Measured on 2026-08-17 against Cursor 3.15.19 on Windows: `cursor <path>` opens a markdown file
under `.forge/` in the Agents window rendered as Preview, with the Preview | Source toggle — the
exclusion that hides that toggle for `.cursor/`, `.claude/` and `.codex/` paths does not cover
`.forge/`. The rendering is a snapshot, not a watch: an external append changed nothing within
ten seconds or on window focus, and re-running the same `cursor <path>` is what refreshed it.
The Claude Code desktop panel is documented to behave the same way — render on send, no disk
watch — so the skill's rule is uniform: surface once, refresh after every worker call.

## A declared elicitation capability is not a rendered one

Measured on 2026-08-15 against the Claude Code desktop surface, running the 0.7.0 server. Three
things are established by which code path executed, rather than by inspecting the wire:

- The client **declared** `elicitation`. The capability guard passed instead of refusing, and a
  refusal would have surfaced as an error rather than a result.
- The client **answered**. The answer-carrying branch is reachable only when `InputResponses`
  already holds the approval key, and that branch is what returned.
- The answer carried no accepted content, so it read as a refusal.
- The user was shown nothing at all.

What the host put in `action` is *not* known: the 0.7.0 code never read that field, and the version
that does was never run on that surface. It would not have helped — a host answering for the user
can send `decline` as easily as anything else.

The earlier measurement, that elicitation works including the full multi-round tool response cycle,
was taken on Claude Code 2.1.233, the terminal CLI. Both findings are true of the surface each was
measured on, and that is the whole problem: the guarantee is per-surface, it degrades silently, and
a server cannot tell which surface it is talking to. Approval by elicitation was removed in 0.8.0 —
see [docs/adr/0003](docs/adr/0003-approval-through-the-orchestrator.md).

## Documentation is outside the review boundary

The baseline, drift report, code-review diff and sensitive-path guard share one pathspec. It hides
every `CONTEXT.md` and every path under `docs/adr/` at any depth. The guard takes the same pathspec
deliberately: it covers exactly the set of paths whose contents are sent, so a sensitive *name* under
an excluded path — an ADR called `0005-token-rotation.md` — is not a leak and must not abort the run.

Two limits come with that boundary, both recorded rather than fixed. A third party's edit to
`CONTEXT.md` or an ADR is now invisible to drift and to code review as well. And a vendor worker runs
in the workspace, so nothing here stops it reading an excluded file it was not sent; the pathspec
governs what is handed over, not what is reachable.
