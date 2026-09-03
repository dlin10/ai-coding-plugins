# Plan Forge Flow — domain language

The vocabulary the code and prompts must use. The old vocabulary conflated "host" and "provider";
these terms replace it.

| Term | Meaning |
|---|---|
| **Vendor** | A model supplier that can do work in a separate process: Claude Code CLI, Codex App Server, Cursor Agent, later Grok. Not "provider" — that word was overloaded. |
| **Orchestrator** | The host agent: runs the interview, **revises the plan in response to critique**, calls the tools. Always an LLM, never a C# class. Strong model. |
| **Act** | A major stage of a run. The four delegated acts are classes: `PlanReview`, `Build`, `CodeReview`, `ReviewFix`. The interview is not an act class; it lives in the orchestrator. |
| **Job** | One delegated act running in the background, keyed by `jobId` and started, watched and collected through `forge.work.start` / `poll` / `fetch`. The shape a worker act takes on a host whose clock cannot hold a worker call; the one-call tools stay the shape everywhere else. |
| **Critic** | The vendor role that **judges**: reviews the plan, reviews diffs. A fresh process each round, fed the review log as input. |
| **Builder** | The vendor role that **implements**: writes code against plan tasks and fixes code-review findings. Never revises the plan. Persistent session. Cheap model. |
| **Run** | One pass, keyed by `runId`, isolated under `.forge/<runId>/`. |
| **Flow log** | The user-facing timeline of a run, `flow_log.md`: every critique, build result and fix round, plus the orchestrator's revision between plan-review rounds, appended by the server and never fed back to a worker. Distinct from the review log, which is critic input. |
| **Plan file** | The run's plan as it currently stands, `PLAN.md`: rewritten by every plan-review round before the critic starts, and again by `forge.plan.confirm` with the text the user approved. Not evidence of approval — that is `approved` in the run state, and a round run after it takes the flag back. |
| **Revision** | The orchestrator's answer to a plan-review round: what it changed in the draft, and optionally what it deferred and why. The change goes to the flow log alone; the deferral also goes to the review log, where the next round's critic reads it as settled. |
| **Granted round** | The user's answer to a reached cap: `userGrantedRound` on the next round call raises that cap by exactly one and runs the round past it. Spent by the call that carries it, so the round after needs a fresh answer, and counted in the run state beside the cap it moved — `grantedReviewRounds` next to `reviewRoundCap`, `grantedCodeReviewRounds` next to `codeReviewRoundCap`. |
| **Run log** | The operational record of a run, `forge.log`: JSONL, append-only, written by the server for every tool call, vendor process and vendor event, and by the orchestrator through `forge.log.append`. Distinct from the flow log, which is the user-facing timeline of results; this one exists for the runs that produced none. |
| **Interview mode** | The orchestrator's choice between an interview without documentation and one that maintains the domain model as it goes. |
| **Catalogue** | The models and effort levels a vendor advertises, served to the interview by `forge.models`. **Live** when the vendor reported the list itself (codex, cursor); **resolved** when the list of aliases is one this repo remembers but the vendor turned each alias into the concrete model it stands for at probe time (claude). An alias the vendor did not resolve is not offered. Advisory for validation either way: the vendor CLI decides. |
| **Model family** | A cursor catalogue entry: the base id its raw list spells out once per effort and speed variant (`gpt-5.3-codex` behind `gpt-5.3-codex-high-fast`), offered with exactly the variants observed. The chosen variant joins back onto the family id; `default` names the bare id and joins to nothing. |
| **Probe** | A vendor's readiness check, which for a live-catalogue vendor also fetches the catalogue. Started for every vendor in the background by `forge.begin`; a vendor whose probe failed is unavailable and the interview does not offer it. |
| **Requirement** | A numbered statement under the plan's `## Requirements` heading of what must be true when the run is done — `R1`…`Rn`, with the run's exclusions beside them. The interview's output, so it names no file and no symbol; every task cites the requirements it serves. |
| **Gate** | The check that would catch a requirement's violation: a command, or a condition someone can observe. A task's own gate ends the task and the builder runs it; a `## Gates` entry — `G1`…`Gn` — belongs to no single task and the orchestrator runs it after the last one. |
| **Verification** | The builder's own account of whether it **proved** the work, separate from whether it did the work: `passed`, `failed`, or `unavailable`, always with evidence. Self-reported; never re-checked by the server. |
| **Capability profile** | What a given host can actually do. Two profiles were designed, `canvas` and `text`; only `text` is built — see below. |

## Tier asymmetry is a design-wide constraint

The roles are **not interchangeable in model strength**, and the old code did not express this — its
`ForgeRole.Reviewer` and `ForgeRole.Builder` chose models the same way. Here the tier is part of the
role's definition:

- **Orchestrator (strong)** holds the interview context, which is why it, and not a worker, revises
  the plan. A fresh reviser process does not have that context, so the revision cannot be delegated
  to a vendor.
- **Builder (cheap)** works against an already-hardened plan, where the decisions were made for it.
  The dependency runs both ways: the cheaper the builder, the deeper the plan — the orchestrator
  chooses the builder before drafting and calibrates task granularity to its model and effort.
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

## The plan states its own intent, and the critic judges that too

The plan used to be the only statement of what a run was for, and the critic's bar was completeness
for implementation: `approve` when nothing is left for the implementer to guess at. A plan that is
detailed, internally consistent and aimed at the wrong thing clears that bar without a finding,
because nothing independent of it says what right would have been. The interview knew — and it lived
only in the orchestrator's context, where no worker and no later session can read it.

So the plan carries `## Requirements` above `## Approach`: numbered `R1`…`Rn`, what must become true
and what must not change, plus what the run deliberately excludes. Tasks cite the requirements they
serve, and `prompts/requirements-contract.md` — appended for plan review exactly as
`scope-contract.md` is for code review — puts the requirements themselves under review and asks for
coverage in both directions.

Handing a critic a fixed yardstick is the move `scope-contract.md` already makes, and there it is
meant to narrow: work the plan does not ask for is at most `minor`. Repeating that at plan stage
would turn the critic into a conformance checker and make a wrong requirement unfalsifiable, which
is worse than having no requirements at all. Hence the asymmetry — the requirements are open to
attack and only the **exclusions** are settled. Without that second half a critic invents
requirements the user ruled out in an interview it never saw, and every round is spent defending it.

The cost is that the plan-review loop is no longer purely between orchestrator and critic. A finding
that a requirement is missing can be a question only the user can answer, so it goes back to them
mid-loop rather than waiting for approval, where its answer would invalidate every round since.

Gates apply the same idea to verification. A task already ended with how it is verified; the `Gate`
label only makes that mandatory and findable, which is what lets the critic treat its absence as a
finding and the orchestrator run the exact command when a builder reports `unavailable`. What had no
home at all is a check no single task owns — a test suite, a warnings-clean build, an invariant
spanning the change — so those go under `## Gates`, and the orchestrator runs them after the last
task. Not the builder, whose session is per-task; and not the critic, because a build writes `bin/`
and `obj/` into the tree it is judging, and each vendor's read-only guarantee covers the agent's own
edits, not the side effects of a command it ran. More gates also mean more self-reported claims, so
the rule below stands unchanged: anything but `passed` is the orchestrator's to run itself.

None of this adds an artifact. The requirements live in the plan file, `PlanTasks` walks only what
is under `## Approach`, and both review acts already send the whole plan — `PlanReview` the draft,
`CodeReview` the approved copy — so requirements and gates reach both critics with no plumbing.

## A cap is a checkpoint, not a stop

Review rounds and code-review rounds are both capped for the same reason: an orchestrator left to
its own budget will keep spending it, since nothing about a `revise` verdict forces the loop to end.
The cap exists to catch that runaway, not to overrule a user who has read the critique and decided
the remaining risk is one they will accept — so it is the orchestrator that cannot raise it on its
own. Only the user's answer can: `userGrantedRound: true` on the next round call raises the cap by
exactly one and lets that round run past it. It is spent by the very round it unlocks, which is what
makes "ask every time" fall out of the mechanism instead of depending on the skill to remember —
there is no way to carry a grant forward, only to ask again.

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

Two more measurements from 2026-08-19, taken while wiring the catalogue into the interview:

- The bracket-override syntax the foot of `--list-models` itself advertises — "Parameterized models
  also accept quoted overrides, e.g. `--model 'claude-opus-4-8[context=1m,effort=high,fast=false]'`"
  — is rejected: both a live family with a bracket (`gpt-5.6-sol[effort=high]`) and the tip's own
  example fail with the same ten-second `Cannot use this model`. The suffix join stays; the
  catalogue's families only ever advertise variants whose joined ids appeared in the list.
- A bare family id absent from the list (`gpt-5.6-sol`, listed only as `-high`/`-xhigh`) is
  nevertheless accepted and runs, resolving to some default variant. Measured but not relied on:
  the catalogue offers a `default` variant only where the bare id itself is listed.

## cursor-agent has no system-prompt channel

The `--help` of 2026.08.11-e8db854 lists no flag resembling `--system-prompt` — nothing like
Claude's `--append-system-prompt` or the App Server's `developerInstructions`. Role instructions
can reach a Cursor worker only inside the prompt itself, so `CursorAgentSession` puts them at its
head, ahead of the task and the schema contract. Before 0.12.1 the loaded role prompt was silently
dropped and Cursor critics and builders ran without their instructions.

## cursor-agent reports every tool in one message, and the exit code in it is a shell's

Measured on 2026-08-29 against 2026.08.25-3e8eec8 in print mode with `--output-format stream-json`,
after a builder run whose every shell call failed without leaving a trace in `forge.log`:

- A tool produces two `"type": "tool_call"` messages — `"subtype"` `started`, then `completed`. The
  `tool_call` object holds exactly one `<name>ToolCall` member (`shellToolCall`, `readToolCall`)
  beside `toolCallId` and the timestamps, so that member's name is the tool.
- `result` is a one-of. A call that worked carries `success`; a failure carries a member named
  after itself — `error` with `errorMessage` for a path the read tool could not find, `spawnError`
  for a shell backend answering "the shell command returned no exit status". Everything that is not
  `success` is therefore read as the failure, whatever it is called, so an unseen failure shape
  cannot pass for a clean call.
- `success.exitCode` is not the command's own. `cmd /c exit 3` came back as `exitCode: 0` with
  cmd's banner in `stdout`, so the tool hands the command to a shell it keeps rather than running
  it. The code is logged as reported and nothing is decided on it — see "Verification is
  self-reported" below.
- A host that runs cursor-agent for us also hands the worker whatever the user installed there, and
  this plugin was among it: the builder's prompt listed the `forge` skill, and cursor-agent started
  a plan-forge-flow MCP server of its own per task — four `planforge.exe` processes that outlived
  the runs that spawned them, each holding a console window, because a server launched from one
  that has no console gets a fresh one. What the host offers a worker is a property of the host,
  not of the role, so `prompts/orchestration-contract.md` is appended to both — it answers the half
  of this we control, and the rest is the host's.

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
would honor is splitting each worker tool into start/poll/fetch calls that return in seconds. That
redesign was taken — see [docs/adr/0006](docs/adr/0006-worker-acts-as-jobs-on-the-cursor-host.md).
One round per call becomes one round per job on this path; the orchestrator's mandatory turn between
rounds, which is what docs/adr/0005 is about, is untouched.

## A backgrounded vendor process has only two things left that can end it

Read out of `StreamingProcess.RunAsync` rather than measured: the kill-tree lives in the enumerator's
`finally`, so it fires only while the server process is alive and something has cancelled the token.
Under the one-call tools the host's own timeout was that something — Cursor cancelling at 60 seconds
took the vendor process down with it, which is why a run that died there left no orphan.

A job is deliberately detached from its `tools/call` token; that detachment is the whole point, and
it removes the reaper. What remains is the vendor's own `RunTimeout` — 20 minutes per attempt — and
the server's shutdown, so `ApplicationStopping` cancels every active job and a graceful exit still
reaps the children. A server killed outright orphans them: nothing here uses a Windows job object,
and on Windows a child does not die with its parent. For a critic that is harmless, since every
vendor keeps it read-only. For a builder it means edits landing in a workspace whose run is already
gone.

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

## Verification is self-reported, and the run log is its audit

A builder that changed files but could not execute anything used to have no honest answer: `status`
was `done` or `blocked`, so it answered `done` and put the caveat in prose, which only a careful
orchestrator noticed (issue #24 — a Codex sandbox that could spawn no process at all). "Did the
work" and "proved the work" are orthogonal, so the contract carries them on separate axes:
`status` stays `done | blocked`, and a required `verification` reports `passed | failed |
unavailable` with evidence.

The report is the builder's word, deliberately. The server does not re-check it: the only signal it
could check against — command exit codes in the vendor's event stream — exists reliably for Codex
alone, and a guarantee that varies by vendor is worse than none. Cursor reports an exit code too,
but it is its own shell's rather than the command's, which is the shape of the problem: a check
built on it would pass a build that never ran. The audit trail is the run log, which records each
tool's outcome — command, exit code, output tail — for all three vendors. Reacting to `unavailable` or
`failed` belongs to the orchestrator — the skill directs it to run the task's verification step
itself and record the outcome — because the server has no environment of its own to verify in, and
blocking the flow would kill a run that can degrade gracefully.

## A failed act used to leave no trace, so the run log is the server's own record

Both older run files record the **results of acts that succeeded** — `review-log.md` the critiques,
`flow_log.md` the timeline — so an act that threw wrote nothing at all. The run behind #19 left a
folder holding `state.json` and no record of whether `cursor-agent` was spawned, with what
arguments, or how it died. Vendor sessions did emit `Started`/`Finished`/`Failed`, but only into an
unbounded channel that production never reads.

`forge.log` closes that. It is JSONL rather than prose because its interesting fields are
themselves multi-line — a command line, a stack trace, a tail of stderr — and one object per line
keeps them greppable without an escaping convention of our own. Long fields are cut, not dropped:
the head of a plan draft still says which draft it was.

Three things route into it, all through `RunLog.Current`, an ambient the tool wrapper sets for the
duration of a call:

- the tool surface — every call with its arguments, and its result, exception or cancellation;
- `StreamingProcess` — the executable, the full argument list, the working directory, the pid, the
  exit code, a killed process's reason (cancelled, timeout, output cap) and a bounded stderr tail;
- `Microsoft.Extensions.Logging`, bridged by `RunFileLoggerProvider`, which is how the MCP SDK's
  own dispatch and transport entries survive a call that dies before any act writes anything.
  `ClearProviders()` used to discard them; stdout carries the protocol, so the run folder is the
  only sink available.

The ambient falls back to the last run this process served, because transport-level entries can
arrive on a context that never flowed through a tool handler and cannot carry a run id — which is
precisely the entry a timeout would otherwise drop.

The orchestrator writes through `forge.log.append` rather than by hand. A tool rather than a
documented licence to edit the file: the run id keeps passing the same containment check as every
other write, the format stays one thing rather than one per agent, and the skill's "do not
hand-edit anything under `.forge/`" rule survives intact.

## A failure explains itself only if it is an exception of ours

The run log records why an act died, but the caller was told nothing: the SDK replaces the message
of any exception that is not an `McpException` with a generic one, so that a server cannot leak
whatever a stray exception happens to carry. Measured against SDK 2.2.0 on 2026-08-29: the run in
#44 died on a prompt file that was never deployed, `forge.log` named the exact path, and what the
orchestrator received was `An error occurred invoking 'forge.plan.review'.` It retried, gave up, and
found the reason only by reading the log afterwards.

The eleven exception types declared in this assembly are all written for that reader — they name the
argument, the round, the cap, the path. `Mcp/ToolErrors.cs` answers those as a tool error carrying
their message and lets everything else keep the SDK's blank. It is a `CallToolFilters` filter rather
than a `try`/`catch` inside the tools because `RunDirectory.Open` runs before the run's log exists
and so before anything a tool could wrap: a non-absolute `workspaceRoot` or a lost `runId` fails
earlier than every other failure, and a wrapper inside the acts would have left exactly that class
mute.

## Required and nullable is a contract with no encoding that works

A nullable parameter with no default is published as `required` with `"type": ["string", "null"]`,
which reads as "send it, null is fine" and is answered by the SDK's marshaller refusing the call
when the key is absent: `The arguments dictionary is missing a value for the required parameter
'revision'.` Measured on Claude Code 2.1.247 on 2026-08-29, the other half was uncallable too — the
host's tool-call serializer dropped the `null` literal and emitted `"revision": ,` on four separate
attempts, so the request never parsed. The only encoding that reached the server was a non-null
string, which pollutes the meaning of a field whose absence is the point.

So every nullable parameter on the tool surface carries `= null`, which is what keeps it out of
`required`; `ToolSurfaceTests` pins the rule by reflection and `build/package.ps1` pins it against
the published schema. Domain rules that depend on a value being present — a second review round must
carry a `revision` — stay where they always were, in the act, where they can say what is missing and
why.

## The `canvas` profile has a host; the Tasks extension still has none

Measured on 2026-08-15 against a spike server built on the MCP C# SDK 2.2.0: Claude Code 2.1.233 and
Cursor 1.0.0 both negotiate protocol `2025-11-25` and report `extensions: null` with no UI
capability. Neither the MCP Apps extension (the `canvas` profile) nor the Tasks extension (streamed
progress) is negotiated by any available host.

Half of that has expired. Run `20260818-123941-05cfa8`, orchestrated from Cursor 3.15, recorded
`profile: "Canvas"` in its state, and run `20260822-190108-fcccbd` did the same from Cursor 3.17.8 —
which identifies itself as `cursor-vscode` and advertises
`{"extensions":{"io.modelcontextprotocol/ui":{"mimeTypes":["text/html;profile=mcp-app"]}}}`. The
detector only says `Canvas` when `McpApps.GetUiCapability(...)` returns non-null, so current Cursor
**does** negotiate the UI capability. Claude Code still reports `Text`; Codex has not been measured,
because its sandbox refuses to spawn the spike.

So the `canvas` branch is written, and it is one tool wide: `forge.plan.show` renders the plan
through the `ui://planforge/plan.html` resource — see [docs/adr/0008](docs/adr/0008-render-the-plan-on-a-canvas.md).
Every other tool still delivers markdown in the tool result, which is also what a `Text` host gets
from `forge.plan.show` itself.

The Tasks half stands unchanged: nobody negotiates it, so progress is observable only at the
granularity of one tool call per unit of work, plus `forge.status` on demand.

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

What the skill could not do on its own is make that happen early. Run `20260822-190108-fcccbd`,
orchestrated from Cursor, surfaced the path in its closing message and nowhere before it: the
instruction to show the file at the first plan-review result had fallen out of view long before the
result arrived, and no tool result carried the path, so nothing brought it back. Every act result
now carries a `documents` object — a path plus what to do with it, per file — from the moment each
file exists, which is the same remedy `forge.work.poll` got for its next call in 0.18.2 and the same
reason: an instruction that lives only in the skill does not survive an hour of a run.

## The plan was the one artefact the run withheld

`PLAN.md` was written by `forge.plan.confirm` and nothing else, and the skill told the orchestrator
to keep every draft to itself until the critic said `approve`. The two together made plan review a
sequence of verdicts about a document the user had never read — they had chosen the critic and the
builder, and then watched judgements on something invisible for up to five rounds.

The draft was already reaching the server every round as `planDraft`, and already reaching disk in
`forge.log` through the tool-call record, so nothing had to be gathered and nothing new was exposed
by writing it. Every round now writes it to `PLAN.md` before starting the critic — before, because
the round takes minutes and those minutes are when reading it is worth something — and the path
travels out beside the flow log's under `documents.plan`. The chat rule that replaces "keep the
drafts to yourself" is "link them, do not paste them": the old rule was right that five pasted
revisions bury the sixth, and wrong that a file the user can ignore does the same.

What it costs is that `PLAN.md` no longer means "the approved plan". Nothing ever read the file's
existence as approval — `Build` and `CodeReview` both gate on `approved` in the run state — but a
round run *after* an approval would have left that flag raised over text nobody approved. So a round
now withdraws it, zeroing `tasksCompleted` and the builder session with it, and says so in the flow
log. See [docs/adr/0009](docs/adr/0009-the-plan-is-visible-from-the-first-round.md), including the
two orderings inside the act that the safety of this rests on.

## The orchestrator's turn is an act too, and the timeline used to skip it

The same run left four plan-review verdicts in `flow_log.md` with nothing between them. Three of
them said `revise`, so the plan changed three times, and none of those changes is anywhere in the
file — the server records what workers return, and revising the draft is the one act it does not
delegate. A reader of that timeline sees a critic contradicting itself rather than a loop
converging.

`forge.plan.review` now takes the answer with the round it answers into: `revision`, refused when
absent from the second round on, and an optional `deferred`. The split between them is the split
between the two logs. A revision is already visible to the next critic — it is holding the
redrafted plan — so it goes to the flow log alone, where nothing feeds it back. A deferral is
invisible in the draft, which is exactly why the same finding returns every round unless the review
log carries it as a decision; that is the mechanism `forge.review.fix` already used for code
review, and it applies unchanged here.

Both are written after the critique returns rather than before it, so a vendor timeout or a
restarted server — both of which this run hit — records the revision once when the act is retried
with the same arguments.

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

The window those four share is the working tree against `HEAD` plus untracked files rendered as
new-file diffs — staged, unstaged and brand-new alike, composed without staging anything. It was
narrower once: a bare `git diff`, blind to every new file, which is how round 1 of the run behind
issue #21 was spent on findings about code that existed on disk (issue #25). The run folder never
widens in, because `.forge/` ignores itself.

Three limits come with that boundary, all recorded rather than fixed. A third party's edit to
`CONTEXT.md` or an ADR is invisible to drift and to code review. A commit made mid-run moves `HEAD`
and takes its changes out of the window — the flow already forbids committing during a run. And a
vendor worker runs in the workspace, so nothing here stops it reading an excluded file it was not
sent; the pathspec governs what is handed over, not what is reachable.

## The session is not the workspace, and only one host says where it is

`workspaceRoot` arrives as a tool argument and used to decide three unrelated things: where
`.forge/<runId>/` lives, the git window the baseline and the code-review diff are taken over, and
the working directory the critic and builder processes get. The orchestrator picks it from the shape
of the task, and on a monorepo it correctly picks the repository root — which is right for the last
two and wrong for the first. Measured on 2026-09-02, run `20260902-224201-7bf03b` on `claude-code`:
the session's directory was `plugins/cache-detective`, the plan also rewrote the root `README.md`,
`marketplace.json` and `.github/workflows/`, so `forge.begin` was called with the repository root and
`PLAN.md` landed two levels above the session. Claude Code renders a file reference as a link only
when the href is relative to the session's working directory, so the run's most-read document
arrived as unclickable text. See issue #53.

Pinning `workspaceRoot` to the session instead is the zero-code fix and silently shrinks the review.
`GitPathspec.WithoutDocumentation` opens with `"."`, and a `.` pathspec is resolved against git's own
working directory; `git ls-files --others` is working-directory scoped too. Measured in this
repository, `git -C . ls-files --others` names `plugins/cache-detective/CONTEXT.md` while
`git -C plugins/cache-detective ls-files --others` names `CONTEXT.md` — so the baseline, the drift
shown at approval and the diff handed to the critic would all have stopped at the subdirectory, and
the root files the same plan rewrites would have been invisible in exactly the way issue #25
describes.

So the session is asked for rather than passed in. Measured on 2026-09-03 by answering each host's
handshake with a server that records the `initialize` params and then requests `roots/list`:

| host | protocol | declares `roots` | answers `roots/list` |
|---|---|---|---|
| `claude-code` 2.1.258 | 2025-11-25 | `{"listChanged": true}` | the session's working directory |
| `codex-mcp-client` 0.147.0 | 2025-06-18 | no | `{"roots": []}` |
| `Cursor` 1.0.0 | 2025-11-25 | no | error `-32601 Method not found` |

Two of the three answer a request they never advertised, one of them with an error, which is why the
declared capability decides whether to ask at all rather than the shape of the answer. Only the host
that declares it moves its run folder; the other two keep the layout they had.

Roots is deprecated by the specification of 2026-07-28 (SEP-2577, which retires sampling and logging
with it) on the grounds of vague semantics and low adoption, and it names no successor — after it
goes, nothing in MCP tells a server where the user is sitting. Deprecated features stay functional
for a year of spec versions. The fallback is what makes that survivable rather than a deadline: a
host that stops declaring the capability reads as a host that never had one, and the run folder goes
back under `workspaceRoot`. The cost of the removal is the clickable link, not the run.

The decision that follows from all of this — and why the surface was not changed instead — is
[docs/adr/0011](docs/adr/0011-the-run-follows-the-session-not-the-workspace.md).
