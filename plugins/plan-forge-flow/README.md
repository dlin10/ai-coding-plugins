# Plan Forge Flow for Codex

## 1. Description and goal

Plan Forge Flow is a Codex plugin for non-trivial repository changes that
benefit from deliberate planning, independent review, controlled
implementation, and a final code audit.

Its goal is to turn an ambiguous request into a decision-complete,
repository-grounded plan and then implement that plan without losing the
decisions, review findings, or verification evidence accumulated along the
way. The workflow is resumable, records explicit user risk decisions, and uses
native Codex sub-agents rather than nested Codex CLI processes.

The plugin provides one user-facing skill: `$forge`.

## 2. Workflow

![Plan Forge Flow workflow from planning through implementation and final review](assets/plan-forge-workflow.svg)

### Phase 0 — environment and state

The orchestrator checks the environment, installs the model-free
`forge_reviewer` and `forge_builder` agent definitions, and initializes
repository-local state.

`forge.mjs` owns workflow transitions, locking, hashes, baselines, retry
counters, verdict parsing, and resume data. It does not spawn models itself and
does not run compilation or tests.

Internal state lives under `.forge/` and is excluded through a managed
`.git/info/exclude` block. Only one active forge run is supported per Git
repository.

### Act 1 — grill and plan

The orchestrator resolves the design tree one decision at a time. If a grilling
skill such as `grill-me` is available, Act 1 invokes it and asks it to return
control after the decision tree is resolved. Otherwise, the embedded interview
procedure asks one question at a time, recommends an answer, and inspects the
repository instead of asking questions that the code can answer.

The result is an owned `PLAN.md` containing independently implementable and
verifiable steps. Decisions and later review history are recorded in
`PLAN-REVIEW-LOG.md`. Existing files without the forge ownership marker are
never overwritten.

### Act 2 — adversarial plan review

At the beginning of Act 2, the orchestrator selects and pins the reviewer model
and reasoning effort:

- The orchestrator always uses the current Codex session model and effort.
- The reviewer may not be weaker than the orchestrator according to the
  discovered model priority.
- When both use the same model, reviewer effort must be at least the
  orchestrator effort.
- `ultra` is forbidden.

Every review round uses a fresh, read-only reviewer with no inherited
conversation context. The reviewer reads the full plan and review log, inspects
the repository, tries to falsify the plan, and ends with a strict
`VERDICT: APPROVED` or `VERDICT: REVISE`.

The orchestrator remains the final arbiter: it accepts or rejects each finding,
records the reason, and revises the plan when necessary. After approval, the
user must explicitly sign off on the current plan hash before any code is
written.

### Act 3 — persistent builder

At the beginning of Act 3, the user chooses the builder model and effort. One
workspace-write builder is then kept alive for the entire implementation. It
receives exactly one locked plan step per follow-up and may edit the working
tree, but it may not stage or commit changes.

The orchestrator verifies every completed step:

- Run all verification commands and checks specified by that plan step.
- If the step specifies none, run the applicable build and/or typecheck plus
  relevant tests.
- If an expected check cannot run, record the check, reason, and verification
  gap in `PLAN-REVIEW-LOG.md`.

`complete --task` is a procedural gate: it records the orchestrator's
verification decision, but `forge.mjs` does not execute the checks. A material
plan amendment receives one fresh review round before implementation resumes.

### Act 4 — full code review and fixes

Act 4 reuses the exact reviewer model and effort selected in Act 2, while still
spawning a fresh read-only reviewer for every round.

The final review covers:

- the complete tracked diff from the HEAD pinned at `begin-build`;
- the tracked working-tree snapshot that existed before the run;
- every current untracked file;
- plan compliance, correctness, security, data integrity, compatibility,
  concurrency, error handling, tests, and material performance regressions.

Text untracked files up to 100 KB are included automatically. Binary, larger,
and secret-like files require explicit review permission. Pre-existing findings
are reported but may be fixed only after separate user authorization.

Accepted in-run findings return to the persistent builder for correction,
verification, and another fresh review. The run ends as `done` after a
full-coverage approval or as `done-with-findings` after explicit user risk
acceptance.

## 3. Requirements

- Codex 0.145 or newer
- The Codex `multi_agent` feature enabled
- Git
- Node.js 18 or newer
- A Git repository for the target change
- Permission to create managed agent definitions under `~/.codex/agents`

`codex debug models` is optional. Model discovery can use the current session,
the native spawn schema, a cached catalog, and a conservative static fallback
when the CLI catalog is unavailable.

## 4. Installation

Add the GitHub repository as a Codex marketplace, then install the plugin:

```text
codex plugin marketplace add https://github.com/dlin10/CodexPlugins.git
codex plugin add plan-forge-flow@dlin10-codex-plugins
```

Use `codex plugin marketplace list` to verify that the marketplace is
configured and `codex plugin list` to verify that `plan-forge-flow` is
available.

Start a new Codex task after installation. On first use, `$forge`
idempotently installs its two managed agent definitions. If either definition
is installed or updated, start one more new Codex task so the native
`spawn_agent` schema includes those roles.

To pick up a newer version, refresh the Git marketplace snapshot, reinstall the
plugin, and start a new task:

```text
codex plugin marketplace upgrade dlin10-codex-plugins
codex plugin add plan-forge-flow@dlin10-codex-plugins
```

## 5. Usage

Open a Codex task in the target Git repository.

### Start a new workflow

```text
Use $forge to plan, review, and implement <your change>.
```

`$forge` checks the environment, creates the workflow state, and guides the
request through all four acts.

### Resume an existing workflow

```text
Use $forge to resume the existing Plan Forge run in this repository.
Inspect the persisted status and continue from its current phase.
Do not initialize a new run.
```

Resume from the same repository checkout or worktree. The current phase,
review history, model selections, pending work, and completed plan steps are
stored under `.forge/`. Do not run cleanup or delete that directory before
resuming.

## 6. Attribution

Plan Forge Flow for Codex is a Codex adaptation of the original Cursor Plan
Forge Flow.

The Act 1 one-question-at-a-time grilling procedure derives from Matt Pocock's
MIT-licensed `grill-me` prompt. Full third-party attribution is available in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

This plugin is distributed under the [MIT License](LICENSE).
