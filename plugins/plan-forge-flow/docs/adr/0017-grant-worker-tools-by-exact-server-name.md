# Grant worker tools by exact server name, looked up at every launch

A headless worker asks nobody, so an MCP tool its vendor does not already allow is refused: run
`20260916-134641-21e3d5` lost every Roslyn call of its claude builder that way (issue #90). The run
now carries **worker tools** — server-name patterns given to `forge.begin` as `workerTools`,
`roslyn-*` when omitted — and each launch turns them into grants by listing the vendor's servers
from the worker's own working directory and granting the ones that match **by their exact names**:
`--allowedTools mcp__<server>` for claude, `-c mcp_servers.<server>.default_tools_approval_mode="approve"`
for codex. Cursor needs nothing, because `--approve-mcps` already grants every server it loads.

**`forge.begin`, not `forge.plan.confirm`.** The critic is told to judge symbol claims through
Roslyn too, and plan-review critics run before anything is confirmed; a setting beside
`builderRoots` would reach only the builder.

**Why a lookup, when claude has a way around it.** Claude's rule syntax cannot name a family of
servers — `mcp__roslyn-*` and `mcp__*` were both refused on 2026-09-17 — but a `PreToolUse` hook in
`--settings`, matched by regular expression and answering `allow`, let the same call through with no
lookup at all. It was measured and rejected for one reason: codex has neither a pattern nor a hook
here, so it must list its servers and approve them by name regardless, and doing the same for claude
keeps one mechanism — pattern, vendor list, exact names — whose result the run log can show as the
servers actually granted rather than as a regular expression. The cost is a second process per
launch: `claude mcp list` prints text and health-checks every server (about 4 s), `codex mcp list
--json` takes about 3 s. A lookup that fails is logged and the worker starts without the grants,
because the worker can still do the work by text search and a failed act would cost more.

**Considered and rejected**

- *Enumerating Roslyn's nine tools per server for codex*, as the issue first proposed. The server
  name is not known in advance — `roslyn-mcp` in a codex project config, `roslyn-<slug>` in claude's
  local scope — and the per-server key already exists and is what `roslyn-setup-repo` writes.
- *A fixed Roslyn grant with no setting.* It serves the one known need, but a plan relying on any
  other MCP server would hit the same refusal with no way around it short of a release.

**Consequences.** A grant is permission, not a tool: a pattern that matches nothing grants nothing,
and the run log says so. The critic receives the same grants as the builder, so its read-only
posture now depends on the granted servers being read-only too — true of Roslyn, and the
orchestrator's to keep true for anything else it names.
