# Plan Forge Flow 0.16.0

Plan Forge Flow is a Codex, Claude Code, and Cursor plugin for decision-complete planning, fresh
adversarial review, controlled implementation, and final code review. It ships as an MCP server: a
typed .NET 10 executable named `planforge` that exposes thirteen tools. Release 0.16.0 supports only
Windows x64.

The host agent is the orchestrator. It runs the interview and revises the plan between review
rounds, because it is the only participant holding the interview context. The critic and the builder
are separate model processes, and neither ever revises the plan.

## Workflow

![Plan Forge Flow workflow from planning through implementation and final review](assets/plan-forge-workflow.svg)

| Tool | What it does |
|---|---|
| `forge.begin` | Opens a run, takes a baseline of the working tree, and starts every vendor's catalogue probe in the background |
| `forge.models` | Returns each vendor's model catalogue for the interview, newest first, with availability and the reason when a vendor is not usable |
| `forge.plan.review` | One review round: a fresh critic judges the current draft, beside the orchestrator's account of what the previous round changed |
| `forge.plan.show` | Renders the plan as a document in hosts that negotiate the MCP Apps UI extension, with the drift beside it |
| `forge.plan.confirm` | Records the user's decision on the plan, and the approved tasks when it is yes |
| `forge.build.next` | Builds one task of the approved plan |
| `forge.review.code` | One code-review round: a fresh critic judges the diff against the approved plan |
| `forge.review.fix` | Hands the findings the orchestrator kept to the builder, and logs the deferred ones with reasons |
| `forge.status` | Reports where the run stands, with filtered working-tree drift since the baseline, excluding `CONTEXT.md` and `docs/adr/**` |
| `forge.work.start` | On Cursor hosts, starts one worker act as a background job |
| `forge.work.poll` | Waits for a background worker act, up to 45 seconds per call |
| `forge.work.fetch` | Fetches the terminal result of a background worker act |
| `forge.log.append` | Appends one orchestrator entry to the run's diagnostic log |

The draft the critic reads states its own intent: a `## Requirements` section above the tasks,
numbered and cited by the tasks that serve them, and the checks that would catch a requirement being
violated — a `Gate` ending each task, plus a `## Gates` section for whatever no single task owns.
The requirements are under review beside the tasks, and only what they exclude is settled, so a plan
aimed at the wrong thing is a finding rather than a clean approve.

Both reviews are one round per call, because the orchestrator has to take a turn in between: it
revises the plan after `forge.plan.review`, and after `forge.review.code` it filters the findings
against the approved plan before `forge.review.fix` relays them. That turn is recorded rather than
assumed — a plan-review round after the first is refused without an account of what the previous
one changed, and it lands in the flow log where the user reads the two loops as a conversation. The
critic and the builder never talk directly — what the orchestrator defers is recorded in the review
log with its reason, so the next round's critic treats it as settled and the user sees it when the
review ends. See
[docs/adr/0005](docs/adr/0005-code-review-through-the-orchestrator.md) for why the sealed loop was
opened.

The plan itself is readable from the first round rather than at the end of them: each round writes
the draft to `PLAN.md` before the critic starts, so what the verdicts are about is a document you
can open while they arrive. The price is that an approval is no longer final within a run — a round
run after one takes it back and resets the build progress. See
[docs/adr/0009](docs/adr/0009-the-plan-is-visible-from-the-first-round.md).

Approval does not go through MCP elicitation, and since 0.8.0 there is no code here that can ask the
user anything. The orchestrator reads the drift out of `forge.status`, shows the user the plan and
that drift however its own host shows things best, and passes back what they answered.
`forge.plan.confirm` records it. See
[docs/adr/0003](docs/adr/0003-approval-through-the-orchestrator.md) for why elicitation was removed
rather than repaired.

On a host that negotiates the MCP Apps UI extension — Cursor does, Claude Code does not — "however
its own host shows things best" has a concrete answer: `forge.plan.show` renders the plan as a
document, with the drift above it, in the host's own UI. It is display only, and every other host
sees the surface it always did. See
[docs/adr/0008](docs/adr/0008-render-the-plan-on-a-canvas.md).

The tool is for a decision the user actually made. Deciding on their behalf is the one thing it must
not be used for, and nothing in this codebase can tell the difference.

## Vendors

Three vendors can fill either role, chosen per call with the `vendor` argument: the critic's choice
goes to the two review tools, the builder's to `forge.build.next` and `forge.review.fix`, so the
roles stay independent:

| Vendor | Reached through | Structured output | Catalogue |
|---|---|---|---|
| `claude` | Claude Code CLI | `--json-schema`, natively | resolved, aliases through `init` |
| `codex` | Codex App Server over stdio | schema in the prompt, validated here | live, `model/list` |
| `cursor` | `cursor-agent` CLI | schema in the prompt, validated here | live, `--list-models` |

Structured output is a hard requirement of the vendor interface, so a vendor without native support
gets the schema in its prompt and one retry against our own validation. The catalogues feed the
interview: `forge.models` serves them so the model question offers what the vendor actually serves,
newest first, rather than the orchestrator's memory of the line-up. They remain advisory for
validation — the vendor CLI decides, and an unfamiliar model is a warning rather than a refusal.

Role prompts live in [`prompts/`](prompts) as plain markdown and can be edited per project without
rebuilding the binary. The shared [Roslyn contract](prompts/roslyn-contract.md) is appended to every
critic prompt; the [scope contract](prompts/scope-contract.md) is appended for code review, where
the critic judges against the approved plan.

## Requirements

- Codex, Claude Code 2.1.232 or newer, or Cursor 3.15.6 or newer
- Windows x64
- Git, and a Git repository for the target change
- The CLI of whichever vendor you select for a role
- .NET 10 SDK only when building from source
- Access to github.com on the first launch of a version, when the launcher downloads the executable

The distributed executable is self-contained, trimmed, and single-file; no preinstalled .NET runtime
is needed. Node.js is no longer required — the hooks that needed it are gone.

## Installation

The public repository is the marketplace. Install the plugin:

```text
codex plugin marketplace add <owner>/<repository>
codex plugin add plan-forge-flow@dlin10-codex-plugins
```

In Claude Code, add the same marketplace and install its Claude plugin:

```text
/plugin marketplace add <owner>/<repository>
/plugin install plan-forge-flow@dlin10-ai-coding-plugins
```

The installed plugin is small:

```text
plugins/plan-forge-flow/
  .codex-plugin/plugin.json
  .claude-plugin/plugin.json
  .cursor-plugin/plugin.json
  .mcp.json
  bin/planforge-launcher.cmd
  prompts/
  skills/forge/SKILL.md
  skills/forge/references/
```

The executable is not in the repository. On launch, `bin/planforge-launcher.cmd` runs a locally
built `bin/win-x64/planforge.exe` when one exists, and otherwise the copy cached under
`%LOCALAPPDATA%\plan-forge-flow` for the manifest version. When neither is there it downloads
`planforge.exe` from the matching GitHub release into that cache with `curl.exe` — once per version
rather than once per session, and with no interpreter of the plugin's own: the launcher is a batch
file because a host starts it for every session and it then stays alive as the server's parent,
where `cmd.exe` holds under 10 MB of working set and `powershell.exe` around 50.

## State

Everything a run knows lives under one folder, isolated by run id, in the directory your host says
you are working in — so the plan and the timeline sit beside your session and your host can link
them, even when the run is reviewing a repository root several levels above. A host that does not
tell the server where you are (today: Codex and Cursor) gets the folder under `workspaceRoot`
instead.

```text
.forge/
  .gitignore            # contains "*" — the folder ignores itself
  <runId>/
    state.json
    PLAN.md               # the plan as it currently stands, rewritten every review round
    review-log.md
    flow_log.md           # the user-facing timeline
    forge.log
    baseline.patch
```

`PLAN.md` and `flow_log.md` are the two files written to be read by a person, and the tools hand
their paths back so the orchestrator can put them in front of you while the run is still moving.
Approval is not the file's existence — it is `approved` in `state.json`, and a review round run
after an approval takes that flag back.

There are no locks: concurrent runs in one workspace are allowed. There are no Git refs: the
baseline is a commit SHA in `state.json` plus `baseline.patch`.

## What is checked, and what is not

Two checks survive, both against irreversible harm:

- **Secrets leaving for another model.** Every prompt is inspected before it is handed to a vendor,
  and a sensitive path is refused by name across the set that is actually sent — the same pathspec
  the diff uses, so a sensitive name under an excluded documentation path is not refused, because
  its contents never leave. Workers run in the workspace and can still read files omitted from the
  filtered diff; see [`docs/adr/0004`](docs/adr/0004-documentation-written-during-the-interview.md).
- **Writes staying inside the run.** One containment test on the run id, instead of six guards.

Everything else is observable rather than prevented. There are no hooks and no gates, so an
orchestrator can abandon a run midway or start editing during the interview. Filtered drift since
`forge.begin` is reported by `forge.status`, excluding `CONTEXT.md` and `docs/adr/**`, so it is
visible to whoever looks — and approval itself is an assertion by the orchestrator that it asked.
These are deliberate trades, recorded in
[`docs/adr/0002`](docs/adr/0002-mcp-server-surface-without-enforcement.md) and
[`docs/adr/0003`](docs/adr/0003-approval-through-the-orchestrator.md), with the documentation boundary
in [`docs/adr/0004`](docs/adr/0004-documentation-written-during-the-interview.md).

## Development

```text
src/PlanForgeFlow.sln
src/PlanForge/
src/PlanForge.Tests/
```

Run the solution from `plugins/plan-forge-flow`:

```text
dotnet test src/PlanForgeFlow.sln --filter "Category!=Integration"
```

Integration tests drive the real vendor CLIs and cost money, so they are traited out of the default
run. `build/package.ps1` publishes `win-x64`, verifies the published binary by completing an MCP
handshake against it, refreshes `bin/win-x64/planforge.exe` with `-InstallBinaries`, and creates the
marketplace archive. Packaging fails if the publish directory contains any sidecar file.

[`CONTEXT.md`](CONTEXT.md) holds the vocabulary and the measured facts behind the design.

## Attribution

The Act 1 interview derives from Matt Pocock's MIT-licensed `grill-me` prompt. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The plugin is distributed under the
[MIT License](LICENSE).
