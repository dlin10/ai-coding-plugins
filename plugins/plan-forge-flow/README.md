# Plan Forge Flow 0.5.0

Plan Forge Flow is a Codex and Cursor plugin for decision-complete planning,
fresh adversarial review, controlled implementation, and final code review. The
runtime is a typed .NET 10 executable named `planforge`; the supported install
surface is a repository marketplace containing all supported RID-specific
executables.

Codex retains its enforced clean-run workflow. Cursor 3.15.6 and newer uses an
editable native `.plan.md` and local Build in the current or a new Agent. Cursor
reviewer isolation and its Build execution preamble are advisory: Cursor can
ignore `readonly: true` or the preamble, so status reports both
`reviewerGuarantee=advisory` and `approvalGuarantee=advisory`.

## Workflow

![Plan Forge Flow workflow from planning through implementation and final review](assets/plan-forge-workflow.svg)

## Requirements

- Codex 0.145 or newer with `multi_agent` enabled, or Cursor 3.15.6 or newer
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
  .cursor-plugin/plugin.json
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
  cursor/commands/
  cursor/skills/
  cursor/agents/
  hooks/
```

The root `.cursor-plugin/marketplace.json` exposes the Cursor package. Install
it from the local marketplace, enter native Plan Mode with Shift+Tab, and invoke
`/forge`. Cloud Build, Canvas, and Cursor 3.14.x are not supported.

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
plan lock|stage|finalize|invalidate|abandon|materialize
agents install
build dispatch|complete|resolve|begin
review prepare|record-response|authorize-preexisting|verdict
session builder|reviewer
run doctor|status|set|cleanup
hook capture-context
```

Options are command-specific:

```text
plan lock                  --relock --amendment
plan stage                 --host cursor --source --run-id --model --effort --cursor-version --observed-model --waiver-reason --accept-risk --authorization-note
plan finalize              --host cursor --run-id --model --effort --cursor-version --observed-model --waiver-reason
plan invalidate            --host cursor --run-id --reason
plan abandon               --host cursor --run-id
plan materialize           Codex: --amendment + stdin JSON; Cursor: --host cursor --run-id
build dispatch             --stage --task-number --retry --cancel --dispatch-id --model --effort --authorization-note --accept-risk
build complete             --task-number --dispatch-id --verification-passed --authorization-note --accept-risk
build resolve              --conflict --dispatch-id
build begin                --amendment --relock
review prepare             --allow-paths --full --authorization-note
review record-response     --host cursor --dispatch-id --stage  (complete response on stdin)
review authorize-preexisting --authorized-paths --authorization-note --accept-risk
review verdict             --stage --critique-file --accept-risk --authorization-note
session builder|reviewer  --id --dispatch-id --model --effort --authorization-note
run status                 (no additional options)
run set                    --key --value --amendment --accept-risk --authorization-note
run cleanup                --purge-generated-agents
```

`--workspace` and `--host` are available on stateful commands; omitted `--host`
means `codex` for clean-install Codex command compatibility. Cursor always passes
`--host cursor`. `hook capture-context` reads its JSON input from stdin.
`--authorized-paths` is a bounded JSON array, and `--authorization-note` is
bounded text.

Every builder or reviewer session must report the exact pinned `--model` and
`--effort`; a fix review uses explicit `fix-build` and `fix-review` dispatch
stages, with the builder completed before the reviewer is registered.

`plan materialize` reads the plan selected by the hook and accepts exactly the
bounded stdin keys `reviewLog`, `completedReviewRounds`, `maxRounds`,
`reviewer`, and `builder`. The hook selects the latest Plan-mode
`<proposed_plan>` from the current transcript; the caller supplies the review
metadata and normalized model choices manually. `reviewLog` must be one
non-empty string containing all review rounds, not an array or object.

Every new materialized `.forge/state.json` requires `schemaVersion: 1` and a
persisted `codex` or `cursor` host. Missing, zero, unknown, or future schemas are
rejected as `unsupported-state-schema` before mutation. Plan Forge Flow 0.5.0
does not migrate or resume pre-0.5 state; start a fresh run instead.

Cursor preapproval is a versioned external `PendingRun` under host user data
(`FORGE_PLUGIN_DATA` overrides its root). It records the exact canonical native
plan path and full-file hash, reviews, dispatch, model waiver, guarantees, and
the two-phase materialization transaction. `.forge`, scoped refs, and managed
exclude state are created only after local Build invokes a successful Cursor
materialization gate. Matching interrupted materialization is replayable;
conflicting or unowned artifacts fail without reset. Cleanup never edits or
deletes the Cursor-owned plan file.

## Cursor native plan behavior

The registered native plan contains exactly one run/workspace HTML marker and a
visible execution preamble. The marker, preamble, and body are reviewed and
hashed together. Canonicalization removes one UTF-8 BOM, normalizes CRLF/CR to
LF, and normalizes the final newline; every other post-review edit invalidates
approval and requires `/forge resume`, fresh review, and finalization.

Cursor 3.15.6 does not expose reliable model/effort override evidence. Every run
therefore requires explicit consent and records `modelGuarantee=waived`, Cursor
version, requested model and effort, observed `Auto`/unavailable state, reason,
and timestamp. The recommended reviewer is `gpt-5.6-sol/xhigh`; the recommended
implementation builder is `gpt-5.6-terra/medium`.

`readonly: true` remains an intent flag. The reviewer prompt absolutely forbids
mutations, writing shell commands, delegation, and state changes, but this is
not an isolation boundary. A normal acceptance run must leave its disposable
workspace unchanged.

## Codex hook behavior

The `UserPromptSubmit` hook invokes the RID-aware launcher at
`sh bin/planforge-launcher.sh hook capture-context` (or
`bin/planforge-launcher.ps1` on Windows) with a five-second timeout. The
launcher selects the matching bundled executable from `bin/<rid>/`. On the
first prompt after a Plan-mode `<proposed_plan>`, the hook stages the latest plan
as a temporary per-workspace pending plan outside the repository and refreshes
it after later proposed plans. Staging is not approval or materialization; only
the first Default-mode implementation turn may materialize it. The hook does
not infer collaboration mode from `permission_mode`, which describes approval
behavior. Malformed or unrelated hook input produces no stdout and exits 0. The
hook response is written directly at the Codex hook JSON root; it is not an
interactive CLI envelope.

Plans and review logs use canonical UTF-8/LF bytes but no ownership marker.
`plan materialize` writes `.forge/PLAN.md`, `.forge/PLAN-REVIEW-LOG.md`, and
nested state; `run cleanup` removes matching owned `.forge/` artifacts and the
Codex pending plan. Forge asks for model and effort separately for reviewer and
builder in free text; runtime rejection or an ambiguous answer can be retried
up to three times per role, and `ultra` is always forbidden.

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

Operational overrides are `CODEX_HOME`, `CURSOR_HOME`, `FORGE_PLUGIN_DATA`, and
`FORGE_AGENTS_DIR`.
Each critique is accompanied by a small JSON decision file at
`<critique-file>.json` with `verdict` and, for code/fix reviews, `coverage`.

## Attribution

The Act 1 interview derives from Matt Pocock's MIT-licensed `grill-me` prompt.
See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The plugin is distributed
under the [MIT License](LICENSE).
