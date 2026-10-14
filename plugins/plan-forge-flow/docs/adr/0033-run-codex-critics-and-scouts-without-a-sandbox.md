---
status: accepted
---

# Run Codex Critics and Scouts without a sandbox

Supersedes the Critic and Scout half of [0028](0028-run-codex-builders-without-a-sandbox.md) and the
Degraded vendor half of [0031](0031-refuse-a-shell-less-answer-and-report-a-degraded-sandbox.md);
the rest of those records stands.

Codex Critics and Scouts ran under `-c sandbox_mode="read-only"`, while Builders already ran outside
the sandbox (0028). In run `20261012-163837-6fc7b1` codex-cli 0.162.1's sandbox setup failed before
every command the Critics and Scouts tried, and 0031 answered by reporting codex as a Degraded
vendor: `forge.models` carried `degraded`, and the skill withheld codex from Critic and Scout. The
user chose to take Critic and Scout out of the sandbox.

## Decision

**Every codex launch bypasses the sandbox.** Builder, Critic and Scout, fresh or resumed, pass
`--dangerously-bypass-approvals-and-sandbox` and no `sandbox_mode`. codex-cli 0.160.1, installed on
2026-10-14, advertises the flag for `exec` and `exec resume`, as 0.159.2 did when 0028 was measured.
As for the Builder, this is the fixed launch for every Run, not a per-Run setting. Nothing else about
the launch changes: the Critic stays `--ephemeral`, the Scout resumable, and the self-plugin
exclusion, Worker tool grants and structured output keep their contracts.

**The Degraded vendor is gone.** No Worker runs in the sandbox, so a sandbox that cannot start a
shell blocks no role. `forge.models` no longer reports `degraded`, the server keeps no record of the
shells its Workers started, the probe no longer reads codex's sandbox log, and a failed
`sandbox.helpers` in `codex doctor` stays out of readiness and out of its warnings. The skill no
longer withholds a vendor from Critic or Scout on that ground.

**A Shell-less answer is still refused.** A codex turn of any role that tried commands and started
none fails as 0031 decided, quoting codex's launch diagnostic. It no longer quotes the sandbox log,
whose setup is on no Worker's path, and the remedy is to retry once codex can start a command.

## Considered and rejected

- **Keep `degraded`, fed by doctor's `sandbox.helpers`.** It would describe a sandbox no role uses,
  and on a machine where that check fails it would keep withholding codex from the very roles this
  change lets run.
- **A per-Run choice of sandbox.** Not asked for; 0028 settled the same question for the Builder in
  favour of a fixed launch.
- **`-c sandbox_mode="danger-full-access"` for Critic and Scout.** It would spell the same launch
  two ways for different roles. The flag is the one already measured on both commands.

## Consequences

A codex Critic or Scout is read-only only by its role contract. Claude withholds the edit and shell
grants and Cursor runs `--mode plan`, but nothing at codex's launch stops its Critic or Scout from
editing the workspace or running a command that does, with the launching process's permissions and
every granted Worker tool. An edit by a Critic lands in the tree it is judging and in the next
review window; like any working-tree change it is shown as drift, not blocked (0002).

The Windows sandbox's setup is on no codex Worker's path, so a failure like 0.162.1's reaches none
of them. `forge.models` loses a field; an Orchestrator that looks for `degraded` finds it absent,
which reads as nothing degraded.
