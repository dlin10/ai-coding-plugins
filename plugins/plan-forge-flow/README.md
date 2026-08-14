# Plan Forge Flow 0.6.1

Plan Forge Flow is a Codex, Claude Code, and Cursor plugin for decision-complete
planning, fresh adversarial review, controlled implementation, and final code
review. The runtime is a typed .NET 10 executable named `planforge`; the
supported install surface is a repository marketplace containing all supported
RID-specific executables.

Codex retains its enforced clean-run workflow. Cursor 3.15.6 and newer uses an
editable native `.plan.md` and local Build in the current or a new Agent. Cursor
reviewer isolation and its Build execution preamble are advisory: Cursor can
ignore `readonly: true` or the preamble, so status reports both
`reviewerGuarantee=advisory` and `approvalGuarantee=advisory`.

## Workflow

![Plan Forge Flow workflow from planning through implementation and final review](assets/plan-forge-workflow.svg)

## Requirements

- Codex 0.145 or newer with `multi_agent` enabled, Claude Code 2.1.226 or newer,
  or Cursor 3.15.6 or newer
- Git
- .NET 10 SDK only when building from source
- Git LFS when checking out the repository or publishing bundles
- Node.js 18 or newer for Claude's asynchronous agent-evidence hooks
- A Git repository for the target change

The distributed executable is self-contained, trimmed, single-file, and does
not require a preinstalled .NET runtime. Node.js is used only by Claude's
fail-open evidence hook; Codex and Cursor operation does not require Node.js.

## Installation

The public repository is the repository marketplace. Clone it with Git LFS
enabled, then add the repository marketplace and install the plugin:

```text
codex plugin marketplace add <owner>/<repository>
codex plugin add plan-forge-flow@dlin10-codex-plugins
```

In Claude Code, add the same repository marketplace and install its Claude
plugin:

```text
/plugin marketplace add <owner>/<repository>
/plugin install plan-forge-flow@dlin10-ai-coding-plugins
```

The plugin contains every supported RID and a launcher selects the current
host automatically:

```text
.agents/plugins/marketplace.json
plugins/plan-forge-flow/
  .codex-plugin/plugin.json
  .claude-plugin/plugin.json
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

## Claude agent behavior

Claude Code receives six fresh read-only reviewer definitions and six
persistent builder definitions, covering `none`, `low`, `medium`, `high`,
`xhigh`, and `max` effort. Agent definitions do not pin a model; the
orchestrator supplies the selected model at invocation time or leaves it
inherited. The `none` variants likewise omit an effort override.

Claude selections use exactly `sonnet`, `opus`, `haiku`, `fable`, or
`inherit`. Haiku supports only `none`; Sonnet, Opus, and Fable support
`low` through `max`; inherit supports every shipped effort and omits the Agent
model argument. Evidence preserves the requested alias, requires the resolved
model, normalizes a missing `modelsUsed` list, and flags family or exact-model
swaps. Doctor rejects Claude's global effort and subagent-model overrides when
running inside Claude Code.

`run doctor --host claude` also resolves Codex, including Windows npm
`.cmd`/`.bat` shims, initializes App Server, validates OpenAI/ChatGPT auth, and
returns its ordered model/effort catalog. It reports `ready`, `absent`, or
`unusable`. An absent Codex selects Anthropic-only behavior; an installed but
unusable Codex requires an explicit continue-without-Codex or stop-and-repair
decision before Act 1. A ready Codex enables provider-first model then effort
selection independently for reviewer and builder.

Each reviewer has an exact allowlist containing `Read`, `Grep`, `Glob`,
`ToolSearch`, and the current nine read-only Roslyn MCP semantic tools. It has
no shell, mutation, delegation, arbitrary MCP namespace, or wildcard Roslyn
access. Builders inherit normal Claude Code tools and remain subject to the
user's permissions.

Asynchronous `PostToolUse(Agent)` and `SubagentStop` hooks capture advisory
model, effort, agent, and result evidence under
`${CLAUDE_PLUGIN_DATA}/agent-evidence/`. They fail open and never write evidence
inside the repository or installed plugin directory.

All host reviewer prompts use a Roslyn-first contract for C#/.NET semantic
claims. A reviewer first verifies the intended solution from an absolute path
and returned compilation identity. Missing, unreachable, wrong-solution, and
inconclusive Roslyn states activate an explicit read-only text fallback without
blocking Forge or automatically reducing coverage. Every critique carries
exactly one `ROSLYN: USED`, `ROSLYN: FALLBACK`, or
`ROSLYN: NOT_APPLICABLE` marker. Build, analyzers, tests, and runtime checks
remain the final verification evidence.

`run doctor` reports a structured advisory `roslyn` object. A valid local
configuration is not proof of MCP capability or solution identity; after the
ordinary doctor, the host skill performs an optional read-only semantic probe
and reports failures only as readiness warnings.

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
session builder|reviewer|start|status|result|cancel
run doctor|status|set|cleanup
hook capture-context
```

OpenAI roles optionally use a typed `codex app-server` JSONL client. It accepts
only OpenAI/ChatGPT authentication and exact model/effort pairs advertised by
the paginated App Server catalog. Reviewer threads are fresh, read-only, and
deleted after audit. Builder holds remain read-only and persistent, then resume
after materialization with repository-scoped write access, network enabled, and
no approvals. Detached `session start` reads its prompt from stdin; status,
heartbeat, stable failure category, cancellation, and atomic terminal results
remain under external `FORGE_PLUGIN_DATA`.

Options are command-specific:

```text
plan lock                  --relock --amendment
plan stage                 --host cursor --run-id --model --effort --cursor-version --observed-model --waiver-reason --accept-risk --authorization-note + chat draft on stdin
                           Claude: --host claude --run-id --provider --requested-model --resolved-model --models-used --effort + canonical plan on stdin
plan finalize              --host cursor --run-id --model --effort --cursor-version --observed-model --waiver-reason
                           Claude: --host claude --run-id --provider --requested-model --resolved-model --models-used --effort --builder-hold-id
plan invalidate            --host cursor|claude --run-id --reason
plan abandon               --host cursor|claude --run-id
plan materialize           Codex: --amendment + stdin JSON; Cursor: --host cursor --run-id; Claude: --host claude --run-id --plan-file
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
run cleanup                --purge-generated-agents --legacy
```

`run status` always returns one data shape: `{ "state": <status-or-null>,
"pendingRun": <host-run-or-null> }`. Cursor and Claude status report both values when a
materialized state and its external pending run coexist; Codex always reports a
null `pendingRun`.

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

Every new materialized `.forge/state.json` requires `schemaVersion: 2`, a
persisted `codex`, `cursor`, or `claude` host, and complete provider-qualified
role selections. Missing, old, unknown, or future schemas are rejected as
`unsupported-state-schema` before mutation. Runs are not migrated; use the
ownership-audited `run cleanup --legacy` and start a fresh run.

Cursor and Claude preapproval use a schema-v4 external `PendingRun` under host user data
(`FORGE_PLUGIN_DATA` overrides its root). During review it temporarily stores
the canonical chat draft; finalization clears that draft and retains review
responses, dispatch evidence, model waivers, and guarantees. At the first Build
materialization the CLI discovers the matching safe native plan and stores its
current text plus a technical hash only inside the replayable transaction.
`.forge`, scoped refs, and managed exclude state are created only after this
gate succeeds. Conflicting or unowned artifacts fail without reset. Cleanup
never edits or deletes the Cursor-owned plan file.

Claude retains the exact canonical reviewed plan through Ready. After native
approval, `plan materialize --host claude --run-id … --plan-file …` accepts only
a regular, non-symlink UTF-8 file whose bytes exactly match that snapshot. This
comparison precedes the repository lock and every `.forge`, Git-ref, or exclude
write. A plan or reviewer-selection revision clears all reviews and the builder
hold. The materialize operation then owns lock/begin and resumes the persistent
builder hold after the transaction is committed.

Claude's first post-approval Default-mode action is this manual materialize
command. Its successful result establishes the transaction, acquires the
repository lock, performs plan lock/build begin, and only then permits the held
builder to resume. The conversational instruction ordering remains advisory;
the CLI's exact-snapshot and ownership checks are the enforced boundary.
Reviewer and builder providers are selected independently, so all four
Anthropic/OpenAI pairings are supported. OpenAI through Codex App Server is
optional. Claude doctor distinguishes absence from a broken installation and
keeps failures non-mutating. Continuing after an `unusable` result disables
OpenAI for both roles for that run; stopping leaves Forge unstarted.

## Cursor native plan behavior

Cursor keeps the plan in chat while it stages the draft through stdin, runs a
fresh reviewer automatically, records revisions, and asks separately for the
implementation model and waiver. After `plan finalize` returns `ready`, Forge
creates the registered native plan as the terminal action of the same Plan
turn. Normal flow does not require `/forge resume`; resume is recovery-only for
an interrupted pending phase. `run doctor --host cursor` rejects any
pre-existing `.forge` target before this flow begins and never removes or
migrates that target.

The registered native plan contains exactly one run/workspace HTML marker and a
visible materialization preamble. The review log intentionally does not bind
that file by path or hash, so user edits after review are accepted. The first
materialization validates and snapshots the current complete native text,
including Cursor frontmatter; its transaction hash protects atomic replay and
the resulting `.forge/PLAN.md`, not approval identity.

Cursor 3.15.6 does not expose reliable model/effort override evidence. Every run
therefore requires explicit consent and records `modelGuarantee=waived`, Cursor
version, requested model and effort, observed `Auto`/unavailable state, reason,
and timestamp. The recommended reviewer is `gpt-5.6-sol/xhigh`; the recommended
implementation builder is `gpt-5.6-terra/medium`.

`readonly: true` remains an intent flag. The reviewer prompt absolutely forbids
mutations, writing shell commands, delegation, and state changes, but this is
not an isolation boundary. A normal acceptance run must leave its disposable
workspace unchanged.

## Pending-plan trust model

Pending plans are untrusted transport artifacts, not proof of approval. For
Codex, the CLI validates the pending document's schema, host, and workspace,
while the skill owns the collaboration-mode and first-Default-turn sequencing;
the runtime has no authoritative mode signal to verify. For Cursor, reviewer
isolation and approval remain advisory, and edits to the native plan after chat
review are intentionally accepted. The materialization transaction binds the
exact native plan bytes used for replay and the final staged state, but does not
claim that those bytes are identical to the reviewed chat draft. See
[`docs/adr/0001-pending-plan-trust-model.md`](docs/adr/0001-pending-plan-trust-model.md).

## Codex hook behavior

The `UserPromptSubmit` hook invokes the RID-aware launcher at
`sh bin/planforge-launcher.sh hook capture-context` (or
`bin/planforge-launcher.ps1` on Windows) with a five-second timeout. The
launcher selects the matching bundled executable from `bin/<rid>/`. On the
first prompt after a Plan-mode `<proposed_plan>`, the hook stages the latest plan
as a temporary per-workspace pending plan outside the repository and refreshes
it after later proposed plans. Staging is not approval, materialization, or
consent to use Forge. Only an explicit `$forge` invocation or a direct request
to run Plan Forge Flow opts in; ordinary planning, review, and implementation
requests must continue without Forge even when pending plan data exists. For an
opted-in run, only the first Default-mode implementation turn may materialize
the plan. The hook does not infer collaboration mode from `permission_mode`,
which describes approval behavior. Malformed or unrelated hook input produces
no stdout and exits 0. The hook response is written directly at the Codex hook
JSON root; it is not an interactive CLI envelope.

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
`FORGE_AGENTS_DIR`. Claude Code supplies `CLAUDE_PLUGIN_DATA` for its external,
persistent plugin evidence.
Each critique is accompanied by a small JSON decision file at
`<critique-file>.json` with `verdict` and, for code/fix reviews, `coverage`.

## Attribution

The Act 1 interview derives from Matt Pocock's MIT-licensed `grill-me` prompt.
See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The plugin is distributed
under the [MIT License](LICENSE).
