# Plan Forge Flow releases

## 0.5.0

- Adds Cursor 3.15.6 plugin discovery, `/forge`, native editable Plan Mode, and local Build in the current or a new Agent.
- Adds versioned host-aware state, external Cursor PendingRun approval state, workspace-scoped refs, OS-released locks, and recoverable two-phase materialization.
- Reports Cursor reviewer and approval guarantees as advisory and records an explicit per-run model waiver.
- Keeps clean-install Codex behavior and its hook-based PendingPlan workflow.

Pre-0.5 `.forge/state.json` and active runs are intentionally unsupported. Plan Forge Flow does not migrate, clean up, or resume them; inspect or preserve any old artifacts manually, then start a fresh 0.5.0 run in a clean workspace.
