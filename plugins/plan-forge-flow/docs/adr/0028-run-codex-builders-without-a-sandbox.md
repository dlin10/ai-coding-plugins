---
status: accepted
---

# Run Codex Builders without a sandbox

Codex Builders run without Codex's sandbox as the fixed default for every Run. This applies
to implementation, review fixes, and resumed retry turns. Critic and Scout retain their read-only
sandbox. The user chose this role boundary and a fixed default over a per-Run setting.

This changes the Builder's execution boundary: its filesystem and command access depend on
the permissions of the launching process rather than the workspace-write sandbox. Plans may then
depend on access the old sandbox refused, including edits to top-level `.agents`, `.codex`, and
`.git`; reinstating the sandbox would require revisiting those plans.

The Codex launch passes `--dangerously-bypass-approvals-and-sandbox` for Builder on both
`exec` and `exec resume`, replacing its `sandbox_mode="workspace-write"` override. The installed
Codex CLI 0.159.2 advertises that flag for both commands. Critic and Scout continue to receive
`sandbox_mode="read-only"` and never receive the bypass flag. The flag also disables approval
prompts, which have no interactive user in a headless Worker launch.

`builderRoots` remains accepted and persisted for compatibility with existing calls and Runs,
but does not control Codex Builder access or produce a
`sandbox_workspace_write.writable_roots` launch argument. The self-plugin exclusion, Worker tool
grants, structured output, and server-owned executable gates retain their existing contracts.

Launch tests cover fresh and resumed Builders with and without roots and assert that Critic and
fresh/resumed Scout retain read-only launches. The tool description and forge skill describe
`builderRoots` as a compatibility field rather than a permission setting.

The Builder sandbox choice supersedes the Builder part of
[0012](0012-reach-codex-through-exec.md), and the writable-roots mechanism supersedes the Builder
part of [0015](0015-the-host-runs-the-gate.md). The remaining decisions in those records stand.
