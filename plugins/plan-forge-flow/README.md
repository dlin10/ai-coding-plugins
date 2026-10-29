# Plan Forge Flow 0.68.3

Plan Forge Flow is a Codex and Claude Code plugin for decision-complete planning, fresh
adversarial review, controlled implementation, and final code review. It ships as an MCP server: a
typed .NET 10 executable named `planforge` that exposes twenty-two tools. It supports Windows x64
only.

A run has four participants:

- **Orchestrator** — the host agent. It runs the interview with the user and revises the plan
  between review rounds, because it is the only participant holding the interview context.
- **Critic** — a separate model process that reviews the plan, and later the code, against the
  approved plan.
- **Builder** — a separate model process that implements the plan one task at a time.
- **Scout** — a read-only model process that answers bounded questions about the repository.

None of the workers revises the plan, and the Critic and the Builder never talk directly: every
finding passes through the Orchestrator. Codex workers run without Codex's sandbox or approval
prompts, with the launching process's permissions; Critic and Scout stay read-only by their
contracts.

## How a run goes

![Plan Forge Flow workflow from planning through implementation and final review](assets/plan-forge-workflow.svg)

1. **Interview.** `forge.begin` opens a run and records a baseline of the working tree. The
   Orchestrator interviews the user, chooses vendors and models for each role, and asks Scout
   about the code where the plan depends on it.
2. **Plan.** The Orchestrator writes `PLAN.md`: a `## Requirements` section, numbered tasks that
   each end with a `Gate`, and a `## Gates` section for checks no single task owns. After the
   draft, an Impact pass asks Scout who depends on each thing the plan changes.
3. **Plan review.** Each `forge.plan.review` call is one round with a fresh Critic. The
   Orchestrator revises the plan between rounds and accounts for what it changed.
4. **Approval.** The Orchestrator shows the user the plan and the drift since `forge.begin`, and
   `forge.plan.confirm` records the user's answer. Approval names the plan on disk by its hash.
5. **Build.** `forge.build.next` builds one task. The server, not the Builder, runs the task's
   executable gate; a failing gate withholds the task and briefs the retry.
6. **Code review.** `forge.review.code` runs one round in which a fresh Critic judges the diff
   against the approved plan. The Orchestrator filters the findings and sends the ones to fix
   through `forge.review.fix`.

The interview asks once when to run full verification: after each Builder fix turn
(`gate=full; fullGate=beforeNextRound`), or only at the end (`gate=targeted; fullGate=final`).
Targeted mode needs exactly one executable Fix gate in `## Gates`; findings it fixes stay pending
until a full verification closes them. Either way, a run succeeds only after full verification.

## Tools

| Tool | What it does |
|---|---|
| `forge.begin` | Opens a run, takes the baseline, starts vendor catalogue probes, reports configured Advisors |
| `forge.models` | Returns each vendor's model catalogue, newest first, with availability and Fast efforts |
| `forge.scout.select` | Records the Scout vendor, model, effort and speed, or a decision to continue without Scout |
| `forge.scout.run` | Runs one bounded Scout question and stores the answer in `scout-answers.json` |
| `forge.evidence.map` | Asks a configured Advisor how Scout evidence bears on the plan; see [Advisor and Evidence map](#advisor-and-evidence-map) |
| `forge.instructions.set` | Records what the user wants this run's Critic and Builder told |
| `forge.plan.write` | Writes the first draft or a full rewrite of `PLAN.md` and returns its outline and shape |
| `forge.plan.outline` | Returns the plan hash and its targets with IDs and hashes |
| `forge.plan.patch` | Revises selected targets against the current outline hash |
| `forge.plan.review` | Applies plan decisions, then runs one plan-review round |
| `forge.plan.show` | Renders `PLAN.md` with its drift in hosts that support the MCP Apps UI extension |
| `forge.plan.confirm` | Records the user's approval of `PLAN.md` at a given hash, and the verification timing |
| `forge.build.next` | Builds one task and runs its gate |
| `forge.review.code` | Runs one code-review round against the approved plan |
| `forge.review.fix` | Applies code decisions and has the Builder fix the chosen findings, then runs the gates |
| `forge.status` | Reports the run's state: findings, drift, running jobs, decisions, Advisors |
| `forge.decision.record` | Records a settled user decision as `D-nnnn`; every later Critic is shown it |
| `forge.work.start` | Starts a long Builder act or a Scout question as a background job |
| `forge.work.poll` | Waits for a background job and reports its latest activity |
| `forge.work.cancel` | Requests cancellation of a background job |
| `forge.work.fetch` | Fetches the final result of a background job |
| `forge.log.append` | Appends an Orchestrator entry to the run's log, optionally opening a new phase |

`skills/forge/SKILL.md` describes every tool's arguments and when the Orchestrator calls it.

## Plans, findings and decisions

- **The plan is readable from the first round.** `PLAN.md` is written before review starts and
  rewritten with every revision. A patch after approval withdraws the approval; tasks before the
  first changed task stay built.
- **Findings have stable IDs.** `decision-ledger.json` holds every open finding with its ID and
  disposition (`unresolved`, `deferred`, `rejected`). A Critic only proposes; the Orchestrator
  decides every disposition and may raise findings of its own.
- **Decisions are recorded once.** `forge.decision.record` stores the user's settled answers in
  `decisions.json`, and every later Critic sees them, so a review does not reopen a question the
  user already answered.
- **Retries are safe.** One `decisionBatchId` names one logical decision set: an exact retry is a
  no-op. Likewise one `fixAttemptId` names one exact sorted ID set and one gate mode; retrying it
  after an interruption repeats the same fix rather than starting a new one.
- **The user approves.** Nothing in the server asks the user anything; the Orchestrator shows the
  plan in its own host and passes back what the user answered. The tool is for a decision the user
  actually made, and the server cannot tell the difference.

## Vendors

Two vendors can fill the Critic, Builder or Scout role, chosen independently per role:

| Vendor | Reached through | Structured output | Catalogue |
|---|---|---|---|
| `claude` | Claude Code CLI | `--json-schema` | resolved, aliases through `init` |
| `codex` | Codex CLI, `codex exec` | `--output-schema` | live, `codex debug models` |

The catalogues let the interview offer the models the vendor actually serves; an unfamiliar model
is a warning, not a refusal. A worker can run at its vendor's Fast tier where the catalogue lists
it for that model and effort; an unconfirmed Fast request is refused before any worker starts.

Role prompts live in [`prompts/`](prompts) as plain markdown. Edits there reach every project and
are lost on upgrade, so instructions for one run go through `forge.instructions.set` instead.

## Advisor and Evidence map

**What it is.** An Advisor is an external service that answers typed multiple-choice questions and
returns a probability for each option. Two are supported: TypeSafe's Jev and Microsoft-Decision-1
in Azure AI Foundry. The Evidence map uses an Advisor to judge, for each fact in the Scout report
and each implication the Orchestrator derived from it, which parts of the plan it bears on and
whether the plan accounts for it.

**What problem it solves.** After each Impact pass the Orchestrator makes the Evidence check by
hand: every Scout fact that bears on the plan must be either used by the plan or departed from on
purpose. On a large plan with many Scout answers that check is easy to get partly wrong. The map is
a second, independent reading that flags facts the plan appears to miss, so they are dealt with
before the Critic spends a round on them.

**When it runs.** Only when an Advisor is configured, and only after the manual Evidence check:
after the first draft and after each later Impact pass. It is advice only:

- it never replaces the manual check, and a `covered` answer excuses nothing;
- the Orchestrator decides each flag as usual;
- the map is never shown to the Critic, Builder or Scout, and has no part in code review.

Each item is judged once per Advisor and model. A later call asks only about new items, so
revising the plan does not re-send what was already judged; delete `evidence-map.json` to have a
plan judged from scratch. If a Scout answer arrives after the last complete map, plan tools answer
with `evidenceMapWarning`.

**Cost.** Every call reports its estimated and actual cost and the run's total. A run's Advisor
budget is 5 USD unless the user names another as `budgetUsd`; a call that would exceed it sends
nothing and the Orchestrator asks the user whether to raise the budget.

**How to configure it.** Set the variables in the environment the host starts its MCP server
with, such as the Windows user environment, then restart the host. Unset, empty or whitespace
values count as unset, and an Advisor is configured only when all its required variables are set
and valid. `forge.begin` and `forge.status` report configured and partly configured Advisors
without credential values. With several configured, the Orchestrator asks once which to use.

Jev, id `jev`:

| Variable | Meaning |
|---|---|
| `PLANFORGE_JEV_API_KEY` | Required secret |
| `PLANFORGE_JEV_MODEL` | Optional pinned model, default `jev-1.13.0`; must match `^jev-\d+\.\d+\.\d+$` |

Microsoft-Decision-1, id `microsoft-decision`, authenticated by key (Entra ID is not supported):

| Variable | Meaning |
|---|---|
| `PLANFORGE_MSDECISION_ENDPOINT` | Required resource endpoint, such as `https://<resource>.services.ai.azure.com`; https, no credentials, query or fragment; a Foundry project endpoint (`…/api/projects/…`) is invalid |
| `PLANFORGE_MSDECISION_API_KEY` | Required secret, sent in the `api-key` header |
| `PLANFORGE_MSDECISION_DEPLOYMENT` | Required deployment name; pin the model version in the deployment |

Microsoft-Decision-1 is paced by the request quota its deployment reports, so a raised quota needs
no configuration.

**What is sent.** The text of Scout items, implications, plan sections and Change points, which
may quote code. No repository file is read for sending, and the content passes the same secret
check as worker prompts. Configuring a key is consent to send these excerpts from every repository
forge runs in on that machine. **TypeSafe's standard service is not zero-retention.**
Microsoft-Decision-1 requests fall under the terms of your own Azure resource.

The key never appears in a tool argument or result, a log, telemetry or run files, and it is
removed from the environment of every child process: vendor CLIs, gates and git.

## Requirements

- Codex, or Claude Code 2.1.232 or newer
- Windows x64
- Git, and a Git repository for the target change
- The CLI of whichever vendor you select for a role
- .NET 10 SDK only when building from source
- Access to github.com on the first launch of a version, when the launcher downloads the executable

The distributed executable is self-contained; no .NET runtime is needed.

## Installation

The public repository is the marketplace. In Codex:

```text
codex plugin marketplace add <owner>/<repository>
codex plugin add plan-forge-flow@dlin10-codex-plugins
```

In Claude Code:

```text
/plugin marketplace add <owner>/<repository>
/plugin install plan-forge-flow@dlin10-ai-coding-plugins
```

On Codex, open `/hooks` after installing or updating the plugin and trust the plan-forge-flow
`SessionStart` hook. After a context compaction it prints where the run stands, so the
Orchestrator can continue.

The executable is not in the repository. `bin/planforge-launcher.cmd` runs a locally built
`bin/win-x64/planforge.exe` when one exists, and otherwise downloads the executable for the
manifest version from the matching GitHub release into
`%LOCALAPPDATA%\plan-forge-flow\bin\<version>`. On later launches it removes old cached versions,
keeping every version an installed plugin still uses, versions used within seven days, and the two
most recently used older ones. It never deletes a running executable or anything outside that
cache.

## Run state

Each run lives in its own folder in the directory your host says you are working in (under
`workspaceRoot` on hosts that do not say, such as Codex):

```text
.forge/
  .gitignore              # "*" — the folder ignores itself
  <runId>/
    state.json            # run state, including approval
    PLAN.md               # the plan as it currently stands
    decision-ledger.json  # findings and their dispositions
    decisions.json        # the user's settled decisions
    scout-answers.json    # Scout answers with stable item IDs
    evidence-map.json     # Advisor judgments
    flow_log.md           # the human-readable timeline of the run
    forge.log             # the diagnostic log
    telemetry.json        # time, tokens and cost per worker, phase and Advisor call
    orchestrator-phase.json
    baseline.patch
```

All of them except `forge.log` and `baseline.patch` are meant to be read by people. There are no
locks, so concurrent runs in one workspace are allowed, and no Git refs: the baseline is a commit
SHA plus `baseline.patch`.

### Telemetry

`telemetry.json` records each worker attempt, each Orchestrator phase and each Advisor call: wall
time, time inside tool calls, tokens and, where the vendor reports it, cost. Worker counts come from
the vendor CLI. Orchestrator counts come from the host's own OpenTelemetry export when it is
enabled, and otherwise from the host's transcript, which carries no cost.

To enable the export in Claude Code, add to `settings.json` in your Claude config directory
(`CLAUDE_CONFIG_DIR`, else `%USERPROFILE%\.claude`); any free loopback port works:

```json
{
  "env": {
    "CLAUDE_CODE_ENABLE_TELEMETRY": "1",
    "OTEL_LOGS_EXPORTER": "otlp",
    "OTEL_EXPORTER_OTLP_LOGS_PROTOCOL": "http/json",
    "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT": "http://127.0.0.1:47318/v1/logs"
  }
}
```

In Codex, add to `config.toml` in Codex's home (`CODEX_HOME`, else `%USERPROFILE%\.codex`):

```toml
[otel]
exporter = { otlp-http = { endpoint = "http://127.0.0.1:47318/v1/logs", protocol = "json" } }
```

Only these files are read, and the host must be restarted after the change. Plan Forge starts a
local collector on that port that keeps only usage counters, never prompts or account data.

## What is checked, and what is not

The server prevents three things:

- **Secrets leaving for another model.** Every prompt is inspected before it reaches a vendor, and
  sensitive files are refused by name.
- **Writes outside the run.** Run files stay inside the run's folder.
- **A task passing a failed gate.** Where a task's `**Gate:**` is a command, the server runs it after
  every Builder turn and withholds the task when it fails.

Everything else is observable rather than prevented. No hook blocks anything, so an Orchestrator
can abandon a run or edit files during the interview; `forge.status` reports drift since
`forge.begin`, and approval is the Orchestrator's assertion that it asked the user. The reasons for
these trades are in [`docs/adr`](docs/adr).

## Development

```text
src/PlanForgeFlow.sln
src/PlanForge/
src/PlanForge.Tests/
```

Run the tests from `plugins/plan-forge-flow`:

```text
dotnet test src/PlanForgeFlow.sln --filter "Category!=Integration"
```

Integration tests drive the real vendor CLIs and Advisors and cost money, so the default run
leaves them out. `build/package.ps1` publishes `win-x64`, checks the binary with an MCP handshake,
refreshes `bin/win-x64/planforge.exe` with `-InstallBinaries`, and creates the marketplace archive.

[`CONTEXT.md`](CONTEXT.md) holds the vocabulary and the measured facts behind the design, and
[`docs/adr`](docs/adr) the design decisions.

## Attribution

The interview derives from Matt Pocock's MIT-licensed `grill-me` prompt. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The plugin is distributed under the
[MIT License](LICENSE).
