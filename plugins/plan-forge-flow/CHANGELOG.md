# Plan Forge Flow releases

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
