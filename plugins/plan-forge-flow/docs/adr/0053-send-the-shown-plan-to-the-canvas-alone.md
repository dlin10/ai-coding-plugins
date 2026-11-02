---
status: accepted
---

# Send the shown plan to the canvas alone

Amends [0008](0008-render-the-plan-on-a-canvas.md).

## Context

`forge.plan.show` answered with `PlanViewResult`: the run id, the whole plan text, its hash, the
drift, the rounds and approval, as the tool's text content. The canvas parsed that text and
rendered it. The text content is also what the host gives the model, so the Orchestrator read back
the plan it had just written, when what it acts on is the hash it passes to `forge.plan.confirm`.
In the concurrency-hunter runs B4c–H2b, on Claude Code with the Canvas profile, the call came 1–3
times a Run with plans of 62–153k characters: 41–45k tokens of the Orchestrator's context a Run
(see [0052](0052-keep-long-reading-out-of-the-orchestrators-context.md)).

The MCP Apps specification gives a tool result three parts: `content`, "text representation for
model context"; `structuredContent`, "structured data optimized for UI rendering (not added to model
context)"; and `_meta`, "not intended for model context". Claude Code was measured on 2 November
2026, because the specification is not what decides:

- **Claude Code gives the model `structuredContent` in place of `content`.** A stdio spike server
  whose one tool returned a different marker in each part, called by `claude -p` 2.1.316: the tool
  result the model received was the `structuredContent` object serialised, the `content` text was
  absent, and the model quoted the structured marker. The desktop app does the same: in its session
  transcripts a Windows-MCP tool that returns both had `{"result": …}`, its structured form, sent to
  the model.
- **`_meta` does not reach the model.** The same call's `_meta` marker was in the result the CLI
  recorded and absent from what the model received.
- **The desktop app negotiates MCP Apps for the servers it starts.** Its `LocalMcpServerManager`
  advertises `io.modelcontextprotocol/ui`, and the runs above recorded `profile: "Canvas"`.

Whether the desktop app's view bridge passes a result's `_meta` on to the view could not be
measured here: that bridge is not in the desktop app's local bundle, and a session cannot attach a
server to itself.

## Decision

- **The plan text travels in the result's `_meta`, under `planforge/plan`.** The content is
  `PlanViewResult` without the plan: run id, plan hash, drift, rounds and approval. There is no
  `structuredContent`, since on Claude Code it would replace the content in the model's context
  with the plan again. `PlanCanvas.PlanMetaKey` and the canvas document spell the key, and a test
  holds them together.
- **The canvas says so when no plan arrives.** It takes the plan from `_meta`; a result without one
  renders the drift and a line saying the plan's text did not reach the view and to ask in the
  chat, never an empty document. On that answer, `approval.md` has the Orchestrator show `PLAN.md`
  in the chat, as on the Text profile.
- `forge.log` records the result as the Orchestrator reads it, so it no longer holds a copy of the
  plan per call.

## Consequences

- The Orchestrator's context loses the plan text of every `forge.plan.show`, 41–45k tokens a Run in
  the runs measured.
- If the desktop app's view bridge drops `_meta`, the canvas shows no plan, and the approval falls
  back to the chat at the cost the Text profile pays. The first Canvas Run after this release
  settles it: the user sees the plan in the canvas or the line saying it did not arrive.
- A host that follows the specification and keeps `structuredContent` from the model would have
  allowed the plan there; Claude Code does not, so this design does not depend on it.
