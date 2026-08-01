# Plan Forge Flow 0.4.0

Plan Forge Flow is a Codex plugin for decision-complete planning, fresh
adversarial review, controlled implementation, and final code review. The
runtime is a typed .NET 10 executable named `planforge`; the supported install
surface is a repository marketplace containing all supported RID-specific
executables.

## Requirements

- Codex 0.145 or newer with `multi_agent` enabled
- Git
- .NET 10 SDK only when building from source
- Git LFS when checking out the repository or publishing bundles
- A Git repository for the target change

The distributed executable is self-contained, trimmed, single-file, and does
not require a preinstalled .NET runtime or Node.js.

## Installation

The public repository is the repository marketplace. Clone it with Git LFS
enabled, then add the repository marketplace and install the plugin:

```text
codex plugin marketplace add <owner>/<repository>
codex plugin add plan-forge-flow@dlin10-codex-plugins
```

The plugin contains every supported RID and a launcher selects the current
host automatically:

```text
.agents/plugins/marketplace.json
plugins/plan-forge-flow/
  .codex-plugin/plugin.json
  bin/planforge-launcher.sh
  bin/planforge-launcher.ps1
  bin/win-x64/planforge.exe
  bin/win-arm64/planforge.exe
  bin/linux-x64/planforge
  bin/linux-arm64/planforge
  bin/osx-x64/planforge
  bin/osx-arm64/planforge
  skills/
  agents/
  hooks/
```

The six RID archives produced by `build/package.ps1` remain optional release
assets for offline or local-marketplace installation. They are not the source
of the repository marketplace. For the direct repository marketplace, commit
the two launchers and all six `bin/<rid>/` executables through Git LFS; keep
`artifacts/` ignored.

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

Options are command-specific:

```text
plan start                 --session-context
plan lock                  --relock --amendment
approval resume            (no additional options)
build dispatch             --stage --task-number --retry --cancel --dispatch-id --model --effort --plan-sha256 --authorization-note --accept-risk
build complete             --task-number --dispatch-id --verification-passed --authorization-note --accept-risk
build resolve              --conflict --dispatch-id
build begin                --amendment --relock
review prepare             --allow-paths --full --plan-sha256 --authorization-note
review authorize-preexisting --authorized-paths --authorization-note --accept-risk
review verdict             --stage --critique-file --accept-risk --authorization-note
session builder|reviewer  --id --dispatch-id --model --effort --authorization-note
run status                 (no additional options)
run set                    --key --value --amendment --accept-risk --authorization-note
run cleanup                --delete-owned-artifacts --purge-generated-agents
```

`--workspace` is available on every grouped CLI command; `hook capture-context`
reads its JSON input from stdin. `--authorized-paths` is a bounded JSON array,
and `--authorization-note` is bounded text.

Every builder or reviewer session must report the exact pinned `--model` and
`--effort`; a fix review uses explicit `fix-build` and `fix-review` dispatch
stages, with the builder completed before the reviewer is registered.

`approval issue` accepts exactly the bounded stdin keys
`humanPlan`, `reviewLog`, `completedReviewRounds`, `maxRounds`, `reviewer`,
and `builder`. The resulting approval wire format is nested envelope v3:
`version`, `plan`, `repository`, `origin`, `nonce`, and `selections`; no model
catalog snapshot is embedded. v1/v2 approvals are rejected.

Materialized `.forge/state.json` is nested state v3 with explicit `generation`
metadata; its `models` group contains only `reviewer` and `builder`. Flat v2,
old catalog-bearing state, or unknown state is rejected without migration.
Materialization uses atomic artifact/state writes, one workspace lock, and a
single last-used nonce file; it has no crash journal or legacy replay migration.

## Trust boundary and hook behavior

The `UserPromptSubmit` hook invokes the RID-aware launcher at
`sh bin/planforge-launcher.sh hook capture-context` (or
`bin/planforge-launcher.ps1` on Windows) with a five-second timeout. The
launcher selects the matching bundled executable from `bin/<rid>/`. The hook derives
collaboration mode from the matching transcript turn, authorizes only an
immediate native implementation submission bound to the approved wrapper, and
stores bounded v2 session capture. Malformed or unrelated hook input produces
no stdout and exits 0. Valid native hook responses are written directly at the
Codex hook JSON root; they are not wrapped in the interactive CLI envelope.

The approval wrapper uses canonical UTF-8/LF plan and review bytes, exact
repository identity, transcript origin, one-time nonce, normalized runtime
model selections, and builder-to-plan hash binding. Materialization writes only
the owned `PLAN.md`, `PLAN-REVIEW-LOG.md`, and nested state. `approval resume`
passes the generated wrapper text directly to
materialization; wrapper file paths are not accepted. Forge asks for model and
effort separately for reviewer and builder in
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

Use `build/package.ps1` to publish the six RIDs, refresh `bin/<rid>/` with
`-InstallBinaries`, and create optional per-RID marketplace archives. The
package step fails if a publish directory contains a runtime,
dependency, debug, or other sidecar file.

Operational overrides retain the existing names: `CODEX_HOME`,
`FORGE_PLUGIN_DATA`, `FORGE_AGENTS_DIR`, and `FORGE_SESSION_MAX_AGE_MS`.
Each critique is accompanied by a small JSON decision file at
`<critique-file>.json` with `verdict` and, for code/fix reviews, `coverage`.

## Attribution

The Act 1 interview derives from Matt Pocock's MIT-licensed `grill-me` prompt.
See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The plugin is distributed
under the [MIT License](LICENSE).
