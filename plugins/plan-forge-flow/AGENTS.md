# Plan Forge Flow

Plan Forge Flow is an MCP stdio server — a .NET 10 executable named `planforge` — packaged as a
plugin for Codex, Claude Code, and Cursor. It is not a CLI: nothing here is meant to be run by a
human at a prompt, and its entry point is the MCP handshake. The binary has one second entry point,
which no human runs either: `planforge.exe --telemetry-collector --port <n>`, the Telemetry
collector (`Orchestration/TelemetryCollector.cs`). A server starts it detached when the host's
profile exports OpenTelemetry to a loopback port; it serves no MCP, takes OTLP/JSON logs at
`/v1/logs` from loopback only, keeps the usage of the Host sessions servers registered with it, and
exits five minutes after the last registered server process does. `Program.cs` dispatches on the
flag before the MCP host is built. The plugin lives inside
the `CodexPlugins` monorepo at `plugins/plan-forge-flow`; all commands below are run from this
directory unless stated otherwise.

## Commands

```bash
dotnet test src/PlanForgeFlow.sln --filter "Category!=Integration"
```

That is the fast suite and the one to run by default. Tests traited `Category=Integration` launch
the real vendor CLIs, take minutes, and cost money — run them deliberately, never as a reflex:

```bash
dotnet test src/PlanForgeFlow.sln --filter "FullyQualifiedName~CursorAgentTests.Only_the_critic_is_started_in_plan_mode"
```

Any change to C# under `src/` requires rebuilding the complete release asset set before handoff:

```powershell
.\build\package.ps1 -InstallBinaries
```

That publishes `win-x64`, verifies the published binary by completing an MCP handshake and asserting
that `tools/list` names all twenty-one `forge.*` tools, refreshes the single self-contained
`bin/win-x64/planforge.exe`, and writes the single versioned
`artifacts/plan-forge-flow-<version>-win-x64.zip`. A change to the tool surface must be mirrored in
the script's assertions. Packaging supports only Windows x64: it fails if a second RID binary, a
second archive, or any sidecar file appears.

From the monorepo root, `npm run validate:plugins` checks every plugin's manifests, skill
frontmatter, cross-host catalog agreement, and version consistency. CI runs it on any push touching
`plugins/**`, so a manifest edit here can turn the whole repository red.

**Releasing is bumping the version.** `.github/workflows/release.yml` runs on every merge to `main`
that touches this plugin: it reads the version out of the manifests and, when no
`plan-forge-flow-v<version>` tag exists yet, runs the fast suite, packages, and creates that tag and
the GitHub release together. So a version bump merged to `main` releases itself, and a merge that
does not bump releases nothing. Do not push a release tag by hand as part of ordinary work — the
tag trigger survives only as a way to re-cut a release whose upload failed, and it refuses a tag
that disagrees with the manifest. What makes the bump load-bearing rather than cosmetic is
`bin/planforge-launcher.cmd`: it downloads `planforge.exe` from the release matching the manifest
version, so a manifest naming a version with no release behind it breaks every fresh install.

## Layout

```text
src/PlanForge/            the MCP server
  Mcp/                    tool surface
  Acts/                   PlanReview, Build, CodeReview, ReviewFix, Scout; the gate parser, runner and keeper
  Vendors/                IVendor and the shared contracts
    Claude/ Codex/ Cursor/    one folder per vendor, matching prompts/
  Orchestration/          capability profile
  Run/ Repo/ Prompts/ Review/ Infrastructure/
src/PlanForge.Tests/      integration tests are traited "Category=Integration"
prompts/                  role contracts and shared contracts, editable without a rebuild
  <vendor>/               only what differs about that vendor; codex needs nothing and has none
skills/forge/SKILL.md     how the orchestrator drives the tools
```

## The four participants

This is the design constraint that explains most of the code, and it is easy to violate by accident:

- **Orchestrator** — the host LLM. Runs the interview and **revises the plan** between review
  rounds. Never a C# class. It is the only participant holding the interview context, which is why
  revision cannot be delegated to a worker process.
- **Critic** — judges plans and diffs. A **fresh process every round**, fed only the current phase
  projection of the Run-local decision ledger so it converges without inheriting its own anchoring
  or the historical transcript. Stateless; never resumed.
- **Builder** — implements against an already-hardened plan and fixes review findings. Never
  revises the plan. Cheap model, one session per plan task or code-review round, resumed only by a
  retry inside it — see [docs/adr/0026](docs/adr/0026-a-builder-session-per-task-and-per-round.md).
- **Scout** — answers one bounded reconnaissance question with sourced evidence. Read-only and
  non-writable; never settles requirements, plans, judges, or edits. Its session is resumable, but
  continuation is chosen explicitly by the Orchestrator.

The direct consequence for the MCP surface in [ForgeTools.cs](src/PlanForge/Mcp/ForgeTools.cs):
every review tool is **one round per call**, because a turn by the orchestrator is mandatory in
between. For plan review the orchestrator revises the draft; for code review it filters the
findings against the approved plan before `forge.review.fix` hands the kept ones to the builder,
and sends typed dispositions, closures, reopening decisions and raises so the ledger tells the next
critic what is settled and what is still open. A raise and the note a fix may carry are the
orchestrator's two ways to say what a finding's rule reaches beyond the place it names — see
[docs/adr/0025](docs/adr/0025-fix-the-rule-not-the-place.md). The loop
used to live inside `forge.review.code` on the premise that nothing in it needed the interview
context; a critic demanding work the plan excluded disproved that — see
[docs/adr/0005](docs/adr/0005-code-review-through-the-orchestrator.md). Do not put the loop back
inside the call.

## The vendor seam

`IVendor` / `IVendorSession` ([Vendors/](src/PlanForge/Vendors)) is the abstraction over model
suppliers that run in a separate process. Three implementations live in per-vendor folders that
mirror `prompts/`: `Claude/`, `Codex/`, `Cursor/`.

Structured output is a hard requirement of the interface. Claude and Codex reach it natively —
`--json-schema` and `codex exec --output-schema`, the latter since
[docs/adr/0012](docs/adr/0012-reach-codex-through-exec.md) — and Cursor has no schema flag, so it
alone goes through `SchemaInPrompt`: schema in the prompt, validation here, exactly one retry. Model catalogues feed the interview: `CatalogCache` probes every vendor in
the background from `forge.begin`, and `forge.models` serves the results so the model question
offers what the vendor actually serves — see
[docs/adr/0007](docs/adr/0007-serve-live-catalogues-to-the-interview.md). A catalogue is `live`
where the vendor publishes a list (codex, cursor) and `resolved` for claude, whose probe turns each
remembered alias into the model id the CLI would send and drops any it does not resolve — see
[docs/adr/0010](docs/adr/0010-resolve-claude-aliases-through-the-cli.md). For model and effort
they stay **advisory**: the vendor CLI decides, and an unrecognised model is a warning, not a
refusal.

Effort is kept separate from model in `Selection` because each vendor expresses it differently —
a flag for Claude, a model property for Codex, a suffix inside the model id for Cursor. The join
belongs in the vendor, never in the core. `Fast` is the third axis for the same reason — a
`--settings` key for Claude, a `service_tier` for Codex, the `-fast` suffix for Cursor — and the one
exception to advisory validation: `Vendors/FastTier.cs` refuses a Fast request the catalogue does not
confirm before any worker starts, every turn names its speed rather than inheriting the vendor's own
configuration, and Claude refuses once more at its session's `init`. See
[docs/adr/0023](docs/adr/0023-refuse-a-fast-request-nothing-confirmed.md).

`VendorFactory` is a deliberate switch rather than DI: a vendor is constructed around
`workspaceRoot`, which arrives as a per-call tool argument, so there is no container lifetime that
fits. The reasoning is recorded in a `<remarks>` block on the class — read it before replacing it.

Adding a vendor means a folder under `Vendors/`, an arm in `VendorFactory`, and — only where the
vendor needs to be told something the role contracts do not already say — a folder under `prompts/`
and the matching bundle assertion in `build/package.ps1`.

**Claude and Cursor keep Critic and Scout read-only by different mechanisms, and Codex not at all**;
none of it is enforced by this codebase — Claude withholds `--permission-mode acceptEdits` and the
builder's shell grant, Cursor relies on `--mode plan` alone, and Codex runs every role without its
sandbox, so only the role contracts stand in the way — see
[docs/adr/0033](docs/adr/0033-run-codex-critics-and-scouts-without-a-sandbox.md). All Worker roles receive the run's
Worker tools, so Critic and Scout stay read-only only while the servers named there are — see
[docs/adr/0017](docs/adr/0017-grant-worker-tools-by-exact-server-name.md). `CONTEXT.md` documents
what was measured for each.

## The Advisor seam

`Advisors/IAdvisor.cs` is the judgment seam for external typed Choice services; Jev and
Microsoft-Decision-1 implement it, and neither is a Vendor or Worker. Both speak systemone and share
`Advisors/SystemOne/SystemOneAdvisor.cs` below the seam — a helper, never a contract: nothing outside
`Advisors/` names it, and an Advisor with another protocol implements `IAdvisor` directly. Adding an
Advisor means a folder under `Advisors/` and a descriptor in `AdvisorCatalog.Default` that marks its
credential `Secret: true`. `forge.begin` and `forge.status` discover configured and
partly configured Advisors from the server process environment on each call. A configured Advisor
is consent to send excerpts from every repository forge runs in on that machine. With none, no
external request is made and the Evidence check stays manual. `forge.evidence.map` runs only after
the Orchestrator's manual Evidence check; `Evidence/` owns candidate selection, answer validation,
status, content-addressed storage and resumable progress. The Evidence map and Scout report never
become Critic, Builder or Scout input. A `covered` is advice and removes nothing. A complete map
also stores the plan hash it judged and the review rounds run by then;
`Evidence/EvidenceMapCurrency.cs` turns that into `evidenceMapWarning` on plan writes and
plan-review rounds when a round has passed and the plan changed since. It warns only: the round,
not the change alone, makes a map due, because patching in answer to a map before its round is
the protocol.

Advisor credentials are read only from the environment when needed and never become tool arguments
or results, exception messages, logs (`forge.log` or `flow_log.md`), telemetry or persisted Run
content (`evidence-map.json`, `scout-answers.json`, `state.json`). Jev requires secret
`PLANFORGE_JEV_API_KEY`; optional `PLANFORGE_JEV_MODEL` defaults to pinned `jev-1.13.0` and accepts
only `jev-<major>.<minor>.<patch>`. Microsoft-Decision-1 requires `PLANFORGE_MSDECISION_ENDPOINT`
(https, without credentials, query, fragment or a Foundry project path), secret
`PLANFORGE_MSDECISION_API_KEY`, and `PLANFORGE_MSDECISION_DEPLOYMENT`, the requested model, which has
no default. `GateSettings.Validate` refuses any secret variable named by
`AdvisorCatalog.Default`, ignoring case, so `forge.plan.confirm` never persists it; report its name,
never its value. `StreamingProcess.Build` strips every such variable from child environments after
`ProcessSpec.Environment` overlays, also ignoring case. This covers Vendor CLIs, catalogue probes,
gates and git, including an overlay that names the secret. Other variables, including the model,
gate environment and Vendor overlays, still reach the child. Do not inspect or rewrite state
written before 0.50.0 for this rule. The Telemetry collector is `planforge.exe` itself, starts no
process and talks only to loopback; its environment is outside this stripping rule.

## Prompts are data, not code

Role prompts live under `prompts/` and are copied beside the binary, so they can be edited and tuned
**without a rebuild** — in a checkout. They are not a per-project knob once the plugin is installed:
an installed `prompts/` sits under the plugin root, where an edit reaches every project that uses
the plugin and is overwritten by the next upgrade. What a user wants said for one run goes through
`forge.instructions.set` instead, which carries it in the act prompt — see
[docs/adr/0019](docs/adr/0019-instruct-the-workers-through-the-act-prompt.md). `builder-contract.md`,
`critic-contract.md`, and `scout-contract.md` carry their respective role contracts;
`prompts/<vendor>/<role>.md` adds only what differs about that vendor — how it
must hand its answer back — and is optional, which is why codex, whose `--output-schema` settles the
question, has no file at all. The role text lived in three copies until they drifted: the codex and
cursor critics lost "do not reopen a settled finding without a proposal" and their builders lost "a
half-finished task reported as done is worse than a blocked one", asymmetric since the first commit. `PromptLibrary` walks up from the binary
because the shipped layouts differ (publish output vs. installed plugin), but **a third layout
cannot be walked to at all**: the launcher downloads the bare executable into a per-version cache
under `%LOCALAPPDATA%`, and the prompts never travel with the release asset. So
`bin/planforge-launcher.cmd` — the only thing that knows both the plugin root and the executable —
names the folder in `PLANFORGE_PROMPTS`, and `PromptLibrary` takes a value there as the root
without probing it. Change the variable's spelling on one side and the assertion in
`build/package.ps1` or `PromptRootTests` turns red; nothing else ties the two halves together. The shared
`prompts/roslyn-contract.md` is appended to every role's prompt — Critic, Scout and Builder — at load
time; it lives once precisely because the 1.x copies drifted apart, and the Builder has it because a
fix has to reach every place that decides the same question, which only references find.
`prompts/scope-contract.md` is appended the same way, but only for code review, where "judge the
diff against the approved plan" has something to attach to.

The canvas document is the deliberate exception. `Mcp/PlanCanvas.html` is a real file for the sake
of editing it as HTML, but it is an `EmbeddedResource` rather than a sidecar: it is UI code with a
postMessage contract to keep, not a tunable, and the bundle is asserted to ship no loose files
beside the binary. Do not move it in with the prompts.

## Run state, and the absence of locks

Everything a run knows lives under `.forge/<runId>/`: `state.json`, `decision-ledger.json`, `PLAN.md`,
`scout-answers.json`, `evidence-map.json`, `flow_log.md`, `forge.log`, `telemetry.json`,
`orchestrator-phase.json`, `baseline.patch`. **Not under
`workspaceRoot`** — that argument is the
git window and the workers' working directory, and the run's own files follow the *session* instead,
the directory the host names through MCP's roots capability (`Run/SessionRoots.cs`), falling back to
`workspaceRoot` for a host that declares none. Only Claude Code declares one today; `CONTEXT.md`
carries the measurement and the deprecation that hangs over it, and
[docs/adr/0011](docs/adr/0011-the-run-follows-the-session-not-the-workspace.md) the decision. Do not
collapse the two roots back together in either direction: pinning `workspaceRoot` to the session
shrinks the review to the session's subtree, and pinning the run folder to `workspaceRoot` puts
`PLAN.md` where the host cannot linkify it. The Flow log is the user-facing audit — every Scout
outcome, Evidence map call, critique, assessment, reopening proposal, decision, build result, fix
attempt and closure, plus the Orchestrator's own revision — and nothing ever feeds it back to a
Worker.
`decision-ledger.json` is the sole authoritative finding state and the only source for bounded
Critic projections. It keeps immutable finding `origin`, mutable `activePhase`, dispositions,
applied decision batches and fix attempts; malformed or unknown state stops the call rather than
recovering to an empty ledger. `PLAN.md` is the run's plan *as it currently stands*, not the approved
one: `forge.plan.write` or `forge.plan.patch`
puts the draft there ahead of the round that judges it — a separate call because a draft streamed
into the review call is not on disk until that call arrives, which made the link late by the whole
of the upload plus the critique — and `forge.plan.review` reads it from there when it is handed no
draft of its own. Approval is `state.json`'s `approved`, and either writer or a round run after it
takes that flag back. A patch checks the current plan hash without a lock; that check and the
replacement are not atomic together — see
[docs/adr/0009](docs/adr/0009-the-plan-is-visible-from-the-first-round.md) and
[docs/adr/0014](docs/adr/0014-writing-the-plan-is-its-own-call.md). There are no locks and no Git refs —
concurrent runs in one workspace are allowed and expected, and the baseline is a commit SHA plus a
patch.

That tolerance is bought by `Infrastructure/AtomicFile.cs`, not by coordination. Writes go
temp-file → `File.Replace`, and readers open with `FileShare.Delete` so a replacement can land
underneath them. On Windows `File.Move(overwrite: true)` can **never** replace a file another handle
has open, whatever share mode it was given, and a blocked replacement surfaces as
`UnauthorizedAccessException` rather than `IOException`. Both facts are load-bearing; the retry loop
catches both exception types. All run-folder writes must go through `AtomicFile`.

The one exception to "no coordination" is inside `AtomicFile`, and it is between threads rather
than between runs: mutations queue on a per-file gate before a write swaps its completed temporary
file or an append competes for the exclusive handle. Windows grants neither operation with a fair
queue, so one thread can otherwise lose every retry while its peers keep making progress. Every
flow that finds a run through `RunLog.Current` appends to the same `forge.log`, so the losing thread
was real, and a dropped log entry is silent by design — `RunLog.Write` may not let a failed write
take the tool call down with it.

The gate changes what a wasted wait costs, and that is the part to hold on to. Before it, appenders
that could not get in wasted the same half second *concurrently*; behind a queue each one spends it
in turn, so a wait that cannot succeed is multiplied by however many threads are behind it. That is
why `Retry` answers a missing **directory** at once instead of waiting: nothing here removes one, so
no wait can end well, and `RunLog.Current`'s fallback keeps pointing at run folders that a session
has already deleted. Measured at four cores: waiting them out costs the suite eleven minutes a run,
answering at once costs twenty seconds. A missing *file* still waits — `File.Replace` takes the
destination away and renames the replacement into its place, so a path can be absent for that
moment, which `A_reader_never_observes_a_partial_write` establishes.

The wait itself is a span rather than the count of probes it used to be, because no two waits are
the same length any more: they are spread either side of the old twenty-five millisecond cadence so
that two waiters do not come back together. Keep the *mean* where it is. Waiting less means retrying
more, and a caller that retries every millisecond holds its thread instead of parking it — a backoff
starting at one millisecond doubled the suite's running time and starved the tasks sharing its pool.

`scout-answers.json` is the only Scout report: appended numbered entries hold the complete answer
as returned, stable item identities and locator checks. `evidence-map.json` keeps content-addressed
judgments, last-complete item marks and the latest map result; it is advisory, never Worker input.

`telemetry.json` holds three kinds of record: `vendor.usage`, one per Worker attempt,
`orchestrator.usage`, one per closed Orchestrator phase (`Orchestration/OrchestratorPhases.cs`), and
`advisor.usage`, one per accepted Evidence map call, including cancellation or failure. The Advisor
record keeps models, request/failure and judgment counts, reported tokens, cache hits, latency
p50/p90, duration, completion and stop reason, with no content. Each accepted call attempts one
Flow-log entry and one usage record in finalization, even when storing the map fails; a record's
own failure is logged and does not change the call's outcome or prevent the other record.
Resumed-attempt growth reads `vendor.usage` records only, and no writer may disturb the other
kinds or a file that is not a JSON array of objects. A record is placed by its `at`, its opening,
not at the end of the file, so any record kind you add must stamp `at` with its opening. The open
phase lives in its own run file,
`orchestrator-phase.json`, not in `state.json`, which a background Job rewrites while a poll runs;
a later server process closes the phase from it — see
[docs/adr/0032](docs/adr/0032-record-the-orchestrator-per-declared-phase.md).

## What is checked, and what is not

Only two things are prevented, both irreversible: secrets leaving for another model
(`Review/SensitiveInput.cs` guards every prompt and refuses a diff touching a sensitive path), and
writes escaping the run folder (one containment check in `RunDirectory`, which also refuses a
non-absolute `workspaceRoot`). Note that the secret regex runs over diffs as well as file contents,
so its leading character class must keep matching the `+` of an added line.

Baseline capture, drift reporting, the code-review window, and the sensitive-path check share one
pathspec: `CONTEXT.md` and `docs/adr/**` are excluded at any depth. They do not share a revision:
baseline and drift retain their `HEAD` comparison, while code review compares the commit recorded by
`forge.begin` with the current working tree whenever it is still an ancestor of `HEAD`, falling back
to the current `HEAD` after history diverges. The check deliberately reads the same resolved review
window as the content diff, so it covers exactly what is sent — a sensitive *name* under an excluded
path is not a leak, and refusing it would only break ADRs that legitimately mention tokens or
secrets. It runs before the empty-diff return, so a documentation-only tree is still inspected. What
none of this does is stop a worker reading an excluded file off disk; see
[docs/adr/0004](docs/adr/0004-documentation-written-during-the-interview.md).

A code-review round may add its own `excludePaths` to that pathspec, and the window and the check
then share them too. A code-review prompt longer than the critic's vendor accepts —
`IVendor.PromptCharacterLimit`, which only codex states — gives the critic its untracked files by
path, then its tracked files' diffs as files under the run folder's `review-round-<N>/`, deleted
when the round ends; the secret guard reads the embedded prompt first, since the critic's own read
sends the same contents. One still too long with every file given by path is refused before the
critic starts, with the sizes that make it up, and spends nothing. See
[docs/adr/0029](docs/adr/0029-refuse-an-oversized-review-prompt-and-let-the-orchestrator-narrow-it.md)
and [docs/adr/0034](docs/adr/0034-give-an-oversized-review-window-by-path.md).

The Builder checks condition gates itself and may run separate targeted checks. It must not run an
executable task gate, including on retries: the server owns that check. If it ran no checks because
the gate belongs to the server, its verification is `unavailable` with an explicit reason; completed
implementation remains `done`. See [docs/adr/0027](docs/adr/0027-leave-task-gates-to-the-server.md).

The one thing that is *verified* rather than prevented is a task's gate. After every builder turn,
`Acts/Gatekeeper.cs` runs the command that immediately follows `**Gate:**` in the task (or the
executable G entries after a full-mode fix, or only the Fix gate after a targeted fix) on the host through `Acts/GateRunner.cs`, and a
non-zero exit rewrites the builder's `done` to `gate_failed` and leaves `tasksCompleted` where it
was. Only code placed *first* after the label counts as a command — `Acts/PlanGates.cs` — because a
gate that opens with prose and names a file in backticks would otherwise be run as that file. The
critic never runs anything; do not change that. See
[docs/adr/0015](docs/adr/0015-the-host-runs-the-gate.md).

Optional case-sensitive gate=full|targeted belongs only to review.fix (direct or work.start),
with full as the default; explicit gate requires fix IDs. Attempts bind mode and exact IDs, and
retries preserve both. Targeted requires one executable Fix gate alongside real G entries under
`## Gates`; the plan Critic checks it before approval, and new/nonterminal fixes validate it before
decisions or jobs. Short success leaves persistent pending findings and covering attempts.
The Orchestrator runs all G1..Gn with conditions before the next Critic (saved fullGate=beforeNextRound)
or after code approval (final), then records successful hostVerified evidence. Both timings require
full verification before success; a failure leaves pending unchanged and requires an Orchestrator
choice, without automatic attribution. Stops/caps report an unfinished run with pending explicitly.
The interview asks once before the first plan: full after each fix, or targeted/final at the end;
explicit targeted/beforeNextRound is supported. Status supplies authoritative saved timing; later
confirm omits fullGate until the user explicitly changes it, without changing plan text for timing.
Unknown, empty or whitespace input is refused; damaged saved timing, including non-string JSON,
stops state reads without mutation. Log through available log.append and start a new run; do not
hand-edit .forge. See [docs/adr/0030](docs/adr/0030-leave-targeted-fixes-pending-until-full-host-verification.md).

Everything else is observable rather than gated. There are no hooks: an orchestrator can abandon a
run midway or edit during the interview, and working-tree drift is shown beside the plan at approval
time rather than blocked. This is deliberate — see
[docs/adr/0002](docs/adr/0002-mcp-server-surface-without-enforcement.md).

Approval is not asked for by this server. `forge.plan.confirm` records a decision the orchestrator
collected through its own host, because the elicitation it replaced could not tell a user refusing
from a host that answered for them and rendered nothing — see
[docs/adr/0003](docs/adr/0003-approval-through-the-orchestrator.md). The consequence to hold on to:
`Approved` in the run state is an assertion by the orchestrator, and no code here can check it.

Drift is reported by `forge.status`, not only by the decision call, because the orchestrator has to
show it to the user before asking rather than after.

## Build constraints that bite

`Directory.Build.props` sets `TreatWarningsAsErrors`, `PublishTrimmed`, `PublishSingleFile`, and —
most consequentially — `JsonSerializerIsReflectionEnabledByDefault=false`. Every serialized type
needs a source-generated `JsonSerializerContext`; adding a record to a tool result without adding it
to `ForgeToolJson` compiles and then fails at runtime. `JsonObject.ToJsonString()` stays safe. The
same applies to a non-scalar tool *argument*: the SDK marshals scalars from its own context, and
anything else must be in `ToolArgumentJson`, which `WithTools` is handed — otherwise the server
fails at startup describing a type it cannot see, which is what `ToolSurfaceTests` and the
packaging script's schema checks catch.

Types are `internal` with `InternalsVisibleTo("PlanForge.Tests")`, so tests exercise the real
classes rather than a public façade.

## Where the reasoning lives

`CONTEXT.md` holds the vocabulary and the **measured** facts behind the design — protocol quirks
established by probing a live server, not by reading documentation. Read it before arguing with a
decision. `docs/adr/` holds the architecture decisions.
