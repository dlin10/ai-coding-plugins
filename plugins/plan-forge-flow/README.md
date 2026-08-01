# Plan Forge Flow 0.4.0

Plan Forge Flow is a Codex plugin for decision-complete planning, fresh
adversarial review, controlled implementation, and final code review. The
runtime is a typed .NET 10 executable named `planforge`; the supported install
surface is a versioned per-RID local-marketplace bundle.

## Requirements

- Codex 0.145 or newer with `multi_agent` enabled
- Git
- .NET 10 SDK only when building from source
- Git LFS when checking out the repository or publishing bundles
- A Git repository for the target change

The distributed executable is self-contained, trimmed, single-file, and does
not require a preinstalled .NET runtime or Node.js.

## Installation

Download the archive for the host RID (`win-x64`, `win-arm64`, `linux-x64`,
`linux-arm64`, `osx-x64`, or `osx-arm64`) from the versioned release and extract
it. The extracted directory is the marketplace root and contains:

```text
.agents/plugins/marketplace.json
plugins/plan-forge-flow/
  .codex-plugin/plugin.json
  bin/planforge[.exe]
  skills/
  agents/
  hooks/
```

Add that extracted root as a local marketplace and install the plugin:

```text
codex plugin marketplace add <local-bundle-root>
codex plugin add plan-forge-flow@plan-forge-flow-bundle
```

Raw source checkout is not a supported installation surface. It is used for
development and CI only.

## Grouped CLI

The executable exposes only grouped commands. `--workspace` is optional and
defaults to the current directory. Interactive commands emit one JSON success
envelope (`ok`, `command`, `data`) or one JSON error envelope (`ok`, `command`,
`error`). Exit codes are 0 for success, 1 for usage/environment/unexpected
errors, 2 for verdict failures, and 3 for state failures.

```text
plan start|lock
approval issue|resume
agents install
build dispatch|complete|resolve|begin
review prepare|authorize-preexisting|verdict
session builder|reviewer
run doctor|status|set|cleanup
hook capture-context
```

The idiomatic options include `--workspace`, `--session-context`,
`--plan-sha256`, `--task-number`, `--critique-file`, `--allow-paths`,
`--authorized-paths` (a bounded JSON array), `--authorization-note`,
`--accept-risk`, `--delete-owned-artifacts`, `--purge-generated-agents`, and
`--purge-replay-ledger`, together with `--stage`, `--retry`, `--cancel`,
`--fix`, `--full`, `--amendment`, `--conflict`, `--relock`, `--id`,
`--dispatch-id`, `--model`, and `--effort`.

Every builder or reviewer session must report the exact pinned `--model` and
`--effort`; fix reviews register the builder and complete `build complete
--fix` before a reviewer session can consume the review dispatch.

`approval issue` accepts exactly the bounded stdin keys
`humanPlan`, `reviewLog`, `completedReviewRounds`, `maxRounds`, `reviewer`,
and `builder`. The resulting approval wire format is nested envelope v3:
`version`, `plan`, `repository`, `origin`, `nonce`, and `selections`; no model
catalog snapshot is embedded. v1/v2 approvals are rejected.

Materialized `.forge/state.json` is nested state v3 with explicit `generation`
metadata; its `models` group contains only `reviewer` and `builder`. Flat v2,
old catalog-bearing state, or unknown state is rejected without migration. New replay
records use the v2 journal/tombstone namespace; completed acknowledged v1
tombstones remain untouched, while pending v1 journals block until recovered or
explicitly purged with `--purge-replay-ledger`.

## Trust boundary and hook behavior

The `UserPromptSubmit` hook invokes the fixed bundled executable at
`bin/planforge hook capture-context` with a five-second timeout. It derives
collaboration mode from the matching transcript turn, authorizes only an
immediate native implementation submission bound to the approved wrapper, and
stores bounded v2 session capture. Malformed or unrelated hook input produces
no stdout and exits 0. Valid native hook responses are written directly at the
Codex hook JSON root; they are not wrapped in the interactive CLI envelope.

The approval wrapper uses canonical UTF-8/LF plan and review bytes, exact
repository identity, transcript origin, one-time nonce, normalized runtime
model selections, and builder-to-plan hash binding. Materialization writes only
the owned `PLAN.md`, `PLAN-REVIEW-LOG.md`, and nested state after all checks
pass. Forge asks for model and effort separately for reviewer and builder in
free text; runtime rejection or an ambiguous answer can be retried up to three
times per role, and `ultra` is always forbidden.

## Development

All .NET implementation files are under this plugin directory:

```text
src/PlanForgeFlow.sln
src/PlanForgeFlow.Cli/
src/PlanForgeFlow.Cli.Tests/
```

Run the two-project solution from `plugins/plan-forge-flow`:

```text
dotnet restore src/PlanForgeFlow.sln
dotnet test src/PlanForgeFlow.sln
```

Use `build/package.ps1` to publish the six RIDs and create the marketplace
archives. The package step fails if a publish directory contains a runtime,
dependency, debug, or other sidecar file.

Operational overrides retain the existing names: `CODEX_HOME`,
`FORGE_PLUGIN_DATA`, `FORGE_AGENTS_DIR`, `FORGE_SESSION_MAX_AGE_MS`,
`FORGE_STATE_LOCK_CRASH_AT`, and `FORGE_CRASH_AT`. Replay recovery is
generation-aware: acknowledged v1 tombstones are preserved, while pending v1
journals require recovery or an explicit `--purge-replay-ledger` decision.

## Attribution

The Act 1 interview derives from Matt Pocock's MIT-licensed `grill-me` prompt.
See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The plugin is distributed
under the [MIT License](LICENSE).
