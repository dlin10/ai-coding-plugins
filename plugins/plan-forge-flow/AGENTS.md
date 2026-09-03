# Plan Forge Flow

Plan Forge Flow is an MCP stdio server — a .NET 10 executable named `planforge` — packaged as a
plugin for Codex, Claude Code, and Cursor. It is not a CLI: nothing here is meant to be run by a
human at a prompt, and the only supported entry point is the MCP handshake. The plugin lives inside
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
that `tools/list` names all thirteen `forge.*` tools, refreshes the single self-contained
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
  Acts/                   PlanReview, Build, CodeReview, ReviewFix
  Vendors/                IVendor and the shared contracts
    Claude/ Codex/ Cursor/    one folder per vendor, matching prompts/
  Orchestration/          capability profile
  Run/ Repo/ Prompts/ Review/ Infrastructure/
src/PlanForge.Tests/      integration tests are traited "Category=Integration"
prompts/<vendor>/         role prompts, editable without a rebuild
skills/forge/SKILL.md     how the orchestrator drives the tools
```

## The three participants

This is the design constraint that explains most of the code, and it is easy to violate by accident:

- **Orchestrator** — the host LLM. Runs the interview and **revises the plan** between review
  rounds. Never a C# class. It is the only participant holding the interview context, which is why
  revision cannot be delegated to a worker process.
- **Critic** — judges plans and diffs. A **fresh process every round**, fed the review log as input
  data so it converges without inheriting its own anchoring. Stateless; never resumed.
- **Builder** — implements against an already-hardened plan and fixes review findings. Never
  revises the plan. Persistent session, cheap model.

The direct consequence for the MCP surface in [ForgeTools.cs](src/PlanForge/Mcp/ForgeTools.cs):
every review tool is **one round per call**, because a turn by the orchestrator is mandatory in
between. For plan review the orchestrator revises the draft; for code review it filters the
findings against the approved plan before `forge.review.fix` hands the kept ones to the builder,
and the deferred ones are logged with reasons so the next critic treats them as settled. The loop
used to live inside `forge.review.code` on the premise that nothing in it needed the interview
context; a critic demanding work the plan excluded disproved that — see
[docs/adr/0005](docs/adr/0005-code-review-through-the-orchestrator.md). Do not put the loop back
inside the call.

## The vendor seam

`IVendor` / `IVendorSession` ([Vendors/](src/PlanForge/Vendors)) is the abstraction over model
suppliers that run in a separate process. Three implementations live in per-vendor folders that
mirror `prompts/`: `Claude/`, `Codex/`, `Cursor/`.

Structured output is a hard requirement of the interface. Claude has it natively (`--json-schema`);
Codex and Cursor do not, so both go through `SchemaInPrompt` — schema in the prompt, validation
here, exactly one retry. Model catalogues feed the interview: `CatalogCache` probes every vendor in
the background from `forge.begin`, and `forge.models` serves the results so the model question
offers what the vendor actually serves — see
[docs/adr/0007](docs/adr/0007-serve-live-catalogues-to-the-interview.md). A catalogue is `live`
where the vendor publishes a list (codex, cursor) and `resolved` for claude, whose probe turns each
remembered alias into the model id the CLI would send and drops any it does not resolve — see
[docs/adr/0010](docs/adr/0010-resolve-claude-aliases-through-the-cli.md). For validation they stay
**advisory**: the vendor CLI decides, and an unrecognised model is a warning, not a refusal.

Effort is kept separate from model in `Selection` because each vendor expresses it differently —
a flag for Claude, a model property for Codex, a suffix inside the model id for Cursor. The join
belongs in the vendor, never in the core.

`VendorFactory` is a deliberate switch rather than DI: a vendor is constructed around
`workspaceRoot`, which arrives as a per-call tool argument, so there is no container lifetime that
fits. The reasoning is recorded in a `<remarks>` block on the class — read it before replacing it.

Adding a vendor means a folder under `Vendors/`, a prompt pair under `prompts/`, an arm in
`VendorFactory`, and a bundle assertion in `build/package.ps1`.

**Each vendor keeps the critic read-only by a different mechanism**, and none of it is enforced by
this codebase — Codex uses a real sandbox, Claude withholds `--permission-mode acceptEdits`, Cursor
relies on `--mode plan` alone. `CONTEXT.md` documents what was measured for each.

## Prompts are data, not code

Role prompts live under `prompts/<vendor>/<role>.md` and are copied beside the binary, so they can
be edited and tuned per project **without a rebuild**. `PromptLibrary` walks up from the binary
because the shipped layouts differ (publish output vs. installed plugin), but **a third layout
cannot be walked to at all**: the launcher downloads the bare executable into a per-version cache
under `%LOCALAPPDATA%`, and the prompts never travel with the release asset. So
`bin/planforge-launcher.cmd` — the only thing that knows both the plugin root and the executable —
names the folder in `PLANFORGE_PROMPTS`, and `PromptLibrary` takes a value there as the root
without probing it. Change the variable's spelling on one side and the assertion in
`build/package.ps1` or `PromptRootTests` turns red; nothing else ties the two halves together. The shared
`prompts/roslyn-contract.md` is appended to every critic prompt at load time — it lives once
precisely because the 1.x copies drifted apart. `prompts/scope-contract.md` is appended the same
way, but only for code review, where "judge the diff against the approved plan" has something to
attach to.

The canvas document is the deliberate exception. `Mcp/PlanCanvas.html` is a real file for the sake
of editing it as HTML, but it is an `EmbeddedResource` rather than a sidecar: it is UI code with a
postMessage contract to keep, not a tunable, and the bundle is asserted to ship no loose files
beside the binary. Do not move it in with the prompts.

## Run state, and the absence of locks

Everything a run knows lives under `.forge/<runId>/`: `state.json`, `PLAN.md`, `review-log.md`,
`flow_log.md`, `forge.log`, `baseline.patch`. **Not under `workspaceRoot`** — that argument is the
git window and the workers' working directory, and the run's own files follow the *session* instead,
the directory the host names through MCP's roots capability (`Run/SessionRoots.cs`), falling back to
`workspaceRoot` for a host that declares none. Only Claude Code declares one today; `CONTEXT.md`
carries the measurement and the deprecation that hangs over it, and
[docs/adr/0011](docs/adr/0011-the-run-follows-the-session-not-the-workspace.md) the decision. Do not
collapse the two roots back together in either direction: pinning `workspaceRoot` to the session
shrinks the review to the session's subtree, and pinning the run folder to `workspaceRoot` puts
`PLAN.md` where the host cannot linkify it. The flow log is the
user-facing timeline — every critique, build result and fix round, plus the orchestrator's own
revision between plan-review rounds — and nothing ever feeds it back to a worker, which is what
lets builder entries live there without shifting what the next critic judges; `review-log.md` is
critic input and stays free of them, carrying only the deferrals the next critic must treat as
settled. `PLAN.md` is the run's plan *as it currently stands*, not the approved one: every plan-review
round writes the draft it was handed, before the critic starts, so the user has a document to watch
instead of meeting the plan once at approval. Approval is `state.json`'s `approved`, and a round run
after it takes that flag back — see
[docs/adr/0009](docs/adr/0009-the-plan-is-visible-from-the-first-round.md). There are no locks and no Git refs —
concurrent runs in one workspace are allowed and expected, and the baseline is a commit SHA plus a
patch.

That tolerance is bought by `Infrastructure/AtomicFile.cs`, not by coordination. Writes go
temp-file → `File.Replace`, and readers open with `FileShare.Delete` so a replacement can land
underneath them. On Windows `File.Move(overwrite: true)` can **never** replace a file another handle
has open, whatever share mode it was given, and a blocked replacement surfaces as
`UnauthorizedAccessException` rather than `IOException`. Both facts are load-bearing; the retry loop
catches both exception types. All run-folder writes must go through `AtomicFile`.

## What is checked, and what is not

Only two things are prevented, both irreversible: secrets leaving for another model
(`Review/SensitiveInput.cs` guards every prompt and refuses a diff touching a sensitive path), and
writes escaping the run folder (one containment check in `RunDirectory`, which also refuses a
non-absolute `workspaceRoot`). Note that the secret regex runs over diffs as well as file contents,
so its leading character class must keep matching the `+` of an added line.

Baseline capture, drift reporting, the code-review diff, and the sensitive-path check share one
pathspec: `CONTEXT.md` and `docs/adr/**` are excluded at any depth. The check deliberately takes the
same pathspec, so it covers exactly what is sent — a sensitive *name* under an excluded path is not a
leak, and refusing it would only break ADRs that legitimately mention tokens or secrets. It runs
before the empty-diff return, so a documentation-only tree is still inspected. What none of this
does is stop a worker reading an excluded file off disk; see
[docs/adr/0004](docs/adr/0004-documentation-written-during-the-interview.md).

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
to `ForgeToolJson` compiles and then fails at runtime. `JsonObject.ToJsonString()` stays safe.

Types are `internal` with `InternalsVisibleTo("PlanForge.Tests")`, so tests exercise the real
classes rather than a public façade.

## Where the reasoning lives

`CONTEXT.md` holds the vocabulary and the **measured** facts behind the design — protocol quirks
established by probing a live server, not by reading documentation. Read it before arguing with a
decision. `docs/adr/` holds the eleven architecture decisions.
