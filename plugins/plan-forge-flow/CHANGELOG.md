# Plan Forge Flow releases

## 0.7.0

- Extends Claude's external activation to schema v2 and a full-run lifecycle:
  `planning`, `materializing`, `executing`, and recoverable `cleaning`.
- Adds a synchronous session-scoped `Stop` gate that prevents Claude from
  skipping materialization, locked implementation tasks, full code review/fix
  rounds, or terminal cleanup, with progress hashes and bounded same-state
  loop behavior.
- Requires matching execution lease, session, workspace, and source run for
  Claude build/review mutations; strengthens terminal, risk, takeover, legacy,
  and recovery cleanup rules.
- Drops migration for schema-v1 activations and older materialized Claude runs.
- Temporarily narrows release and CI support to Windows x64. Distribution now
  contains one LFS executable and one `plan-forge-flow-0.7.0-win-x64.zip` asset;
  the Unix launcher is a deterministic unsupported-platform stub.

## 0.6.2

- Automatically arms a schema-v1 external Claude activation for both direct
  `/plan-forge-flow:forge` and model-invoked Skill entry paths.
- Adds a synchronous `ExitPlanMode` gate that requires Act 2 review, finalize,
  `Ready`, and exact normalized reviewed-plan identity without auto-approving
  Claude's native dialog.
- Adds session-bound `run begin`/`run abandon`, activation-aware planning
  commands and status, explicit cross-session takeover, and deterministic
  activation cleanup after materialize, abandon, or run cleanup.
- Ships a Node.js RID dispatcher with active-run fail-closed behavior, updates
  the Claude Code minimum to 2.1.232, and packages the gate in all six bundles.

## 0.6.1

- Moves Claude OpenAI readiness into doctor with native/npm-shim resolution, structured ready/absent/unusable results, a live ordered catalog, and an explicit continue-without-Codex or stop-and-repair gate before provider-first reviewer and builder selection.

## 0.6.0

- Adds a Claude Code 2.1.226+ plugin manifest and marketplace entry alongside the existing Codex and Cursor surfaces.
- Adds six reviewer and six builder definitions for inherited/no-override through max effort; invocation-time model selection remains unpinned in agent frontmatter.
- Gives Claude reviewers an explicit least-privilege allowlist of four file/discovery tools and the nine current read-only Roslyn MCP tools, without wildcard MCP access.
- Adds asynchronous, fail-open `PostToolUse(Agent)` and `SubagentStop` evidence hooks that write only under Claude's external plugin-data directory.
- Adds a shared Roslyn-first reviewer contract with exact audit markers, audited text fallback, and host-side solution-identity verification for Claude, Codex, Cursor, and OpenAI-facing prompts.
- Adds structured nonblocking Roslyn configuration status to `run doctor`; host skills perform the actual optional semantic capability probe after doctor without changing its verdict.
- Adds Claude's exact Anthropic alias/effort matrix, inherited-model omission, normalized resolved-model evidence with swap detection, and doctor rejection of Claude environment overrides.
- Adds a typed Codex App Server JSONL client, OpenAI-only catalog and identity validation, fresh reviewer and persistent builder lifecycles, and detached atomic session workers with cancellation and heartbeats.
- Adds schema-v2 Forge state and schema-v4 host-neutral pending transactions with provider-qualified role evidence and no automatic migration.
- Adds Claude's exact reviewed-snapshot materialization gate, persistent builder-hold replay, four Anthropic/OpenAI provider pairings, and replacement only after confirmed terminal identity loss.
- Adds two-phase ownership-audited `run cleanup --legacy` for old state, staging directories, scoped refs, managed excludes, and external pending artifacts.
- Release archives now validate all three manifests/marketplaces, the shared skill, Claude hooks and script, exact 12-agent allowlists, and the selected RID binary. Claude evidence hooks require Node.js 18+; the self-contained CLI does not.

## 0.5.2

- Replaces repository-lock booleans with scoped lock tokens and splits Codex fresh, Codex amendment, and Cursor materialization interfaces.
- Completes Cursor plan locking and build preparation in staging, leaving the directory move as the single materialization commit point and removing successor-state reconciliation.
- Splits pending-run schema, workflow, materialization, and Cursor review-evidence responsibilities into focused modules.
- Unifies `run status`, cleans owned materialization staging directories, manages their Git exclude pattern, and records the pending-plan trust model.

## 0.5.1

- Moves Cursor review before native plan creation: the chat draft is staged through stdin, reviewed automatically, finalized after builder selection, and only then materialized as the terminal native plan action.
- Makes `/forge resume` recovery-only and restores the exact native preamble to the materialization gate without a resume requirement.
- Adds schema-v2 Cursor pending runs with temporary chat drafts and transaction-only native plan snapshots; native edits after review are intentionally accepted.
- Makes Cursor doctor fail before planning when the workspace already contains a `.forge` target.
- Adds static plugin validation for chat-first review ordering, terminal native creation, and the exact materialization preamble.
- Restores release packaging on Windows PowerShell 5.1 without weakening bundle path checks.

## 0.5.0

- Adds Cursor 3.15.6 plugin discovery, `/forge`, native editable Plan Mode, and local Build in the current or a new Agent.
- Adds versioned host-aware state, external Cursor PendingRun approval state, workspace-scoped refs, OS-released locks, and recoverable two-phase materialization.
- Reports Cursor reviewer and approval guarantees as advisory and records an explicit per-run model waiver.
- Keeps clean-install Codex behavior and its hook-based PendingPlan workflow.

Pre-0.5 `.forge/state.json` and active runs are intentionally unsupported. Plan Forge Flow does not migrate, clean up, or resume them; inspect or preserve any old artifacts manually, then start a fresh 0.5.x run in a clean workspace.
