# Render the plan on a canvas where the host negotiates MCP Apps

This supersedes the canvas half of [0002](0002-mcp-server-surface-without-enforcement.md), and only
that half: the surface is still tools with no enforcement, and the Tasks extension is still absent.
What changed is the measurement 0002 rested on. That spike found Claude Code 2.1.233 and Cursor
1.0.0 both sending `extensions: null`, concluded that "the `canvas` profile has nobody to enable
it", and left `McpApps.GetUiCapability(...)` as a gate with nothing behind it. MCP Apps has since
shipped as the first official MCP extension (spec `2026-01-26`, `io.modelcontextprotocol/ui`), and
Cursor implements it. Re-measured on 2026-08-22: Cursor 3.17.8, identifying itself as
`cursor-vscode`, advertises `{"extensions":{"io.modelcontextprotocol/ui":{"mimeTypes":["text/html;profile=mcp-app"]}}}`,
and run `20260822-190108-fcccbd` recorded `"profile": "Canvas"` without a line of this change.
Claude Code still reports `Text`, and Codex was not measured.

So the gate has a caller, and the plan is what goes behind it. One tool — `forge.plan.show` —
returns the plan and the drift and carries `_meta.ui` pointing at `ui://planforge/plan.html`, a
single static document registered as a resource. The plan never travels inside that document: the
host loads the template once and pushes each tool result into it over the postMessage bridge.

**A dedicated tool rather than a UI hung off the review result**, because on Cursor the review acts
run through `forge.work.start` → `poll` → `fetch` (see [0006](0006-worker-acts-as-jobs-on-the-cursor-host.md)),
so `forge.plan.review` is never called on the one host that can render anything, and a fetch result
is generic — it carries whichever act finished, and no plan at all. The approval step already had a
moment where the orchestrator holds the whole plan and needs to put it in front of the user; this
tool is that moment and nothing else.

**Display only.** Approval stays where [0003](0003-approval-through-the-orchestrator.md) put it:
the orchestrator asks, and `forge.plan.confirm` records what it was told. The frame could carry the
answer back itself — MCP Apps lets a view call tools, and a click inside a rendered frame is the
user acting, which is exactly the guarantee elicitation could not give — but that reopens who
collects approval, which is a decision of its own and not one this change needs to make. The canvas
therefore ends in a line telling the user their answer belongs in the chat.

**Progressive enhancement is what keeps the other two hosts whole.** A host that never negotiated
the capability ignores `_meta.ui` and gets the same JSON every other tool returns, which is why the
tool's own description sends `Text` profiles to the chat instead: the result would be the plan they
are already holding, rendered by nobody. Measured against the published binary with two clients,
one advertising the extension and one not, `tools/list` is identical either way — twelve unchanged
tools plus this one, and `_meta.ui` on this one alone. What both now also see is a `resources`
capability and a single `ui://` resource, which is the whole cost of the change to a text host.
