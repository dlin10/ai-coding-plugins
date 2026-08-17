# Plan Forge Flow 0.9.0

Plan Forge Flow is a Codex, Claude Code, and Cursor plugin for decision-complete planning, fresh
adversarial review, controlled implementation, and final code review. It ships as an MCP server: a
typed .NET 10 executable named `planforge` that exposes six tools. Release 0.9.0 supports only
Windows x64.

The host agent is the orchestrator. It runs the interview and revises the plan between review
rounds, because it is the only participant holding the interview context. The critic and the builder
are separate model processes, and neither ever revises the plan.

## Workflow

![Plan Forge Flow workflow from planning through implementation and final review](assets/plan-forge-workflow.svg)

| Tool | What it does |
|---|---|
| `forge.begin` | Opens a run and takes a baseline of the working tree |
| `forge.plan.review` | One review round: a fresh critic judges the current draft |
| `forge.plan.confirm` | Records the user's decision on the plan, and the approved tasks when it is yes |
| `forge.build.next` | Builds one task of the approved plan |
| `forge.review.code` | Reviews the diff, has the builder fix findings, repeats until the verdict settles |
| `forge.status` | Reports where the run stands, with filtered working-tree drift since the baseline, excluding `CONTEXT.md` and `docs/adr/**` |

Plan review is one round per call because the orchestrator has to take a turn in between. Code review
is the whole loop inside one call, because nothing in it needs the orchestrator.

Approval does not go through MCP elicitation, and since 0.8.0 there is no code here that can ask the
user anything. The orchestrator reads the drift out of `forge.status`, shows the user the plan and
that drift however its own host shows things best — an artifact, a widget, plain chat — and passes
back what they answered. `forge.plan.confirm` records it. See
[docs/adr/0003](docs/adr/0003-approval-through-the-orchestrator.md) for why elicitation was removed
rather than repaired.

The tool is for a decision the user actually made. Deciding on their behalf is the one thing it must
not be used for, and nothing in this codebase can tell the difference.

## Vendors

Three vendors can fill either role. `forge.plan.review` and `forge.build.next` choose one per call
with the `vendor` argument; `forge.review.code` chooses the critic and builder independently with
role-qualified vendor arguments:

| Vendor | Reached through | Structured output | Catalogue |
|---|---|---|---|
| `claude` | Claude Code CLI | `--json-schema`, natively | declarative |
| `codex` | Codex App Server over stdio | schema in the prompt, validated here | live, `model/list` |
| `cursor` | `cursor-agent` CLI | schema in the prompt, validated here | live, `--list-models` |

Structured output is a hard requirement of the vendor interface, so a vendor without native support
gets the schema in its prompt and one retry against our own validation. Model catalogues advise;
the vendor CLI decides, and an unfamiliar model is a warning rather than a refusal.

Role prompts live in [`prompts/`](prompts) as plain markdown and can be edited per project without
rebuilding the binary. The shared [Roslyn contract](prompts/roslyn-contract.md) is appended to every
critic prompt.

## Requirements

- Codex, Claude Code 2.1.232 or newer, or Cursor 3.15.6 or newer
- Windows x64
- Git, and a Git repository for the target change
- The CLI of whichever vendor you select for a role
- .NET 10 SDK only when building from source
- Git LFS when checking out the repository or publishing bundles

The distributed executable is self-contained, trimmed, and single-file; no preinstalled .NET runtime
is needed. Node.js is no longer required — the hooks that needed it are gone.

## Installation

The public repository is the marketplace. Clone it with Git LFS enabled, then install the plugin:

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
  bin/win-x64/planforge.exe
  prompts/
  skills/forge/SKILL.md
  skills/forge/references/
```

## State

Everything a run knows lives in the repository under one folder, isolated by run id:

```text
.forge/
  .gitignore            # contains "*" — the folder ignores itself
  <runId>/
    state.json
    PLAN.md
    review-log.md
    critiques/
    baseline.patch
```

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
