# Plan Forge Flow — domain language

The vocabulary the code and prompts must use. The old vocabulary conflated "host" and "provider";
these terms replace it.

| Term | Meaning |
|---|---|
| **Vendor** | A model supplier that can do work in a separate process: Claude Code CLI, Codex App Server, Cursor Agent, later Grok. Not "provider" — that word was overloaded. |
| **Orchestrator** | The host agent: runs the interview, **revises the plan in response to critique**, calls the tools. Always an LLM, never a C# class. Strong model. |
| **Act** | A major stage of a run. The three delegated acts are classes: `PlanReview`, `Build`, `CodeReview`. The interview is not an act class; it lives in the orchestrator. |
| **Critic** | The vendor role that **judges**: reviews the plan, reviews diffs. A fresh process each round, fed the review log as input. |
| **Builder** | The vendor role that **implements**: writes code against plan tasks and fixes code-review findings. Never revises the plan. Persistent session. Cheap model. |
| **Run** | One pass, keyed by `runId`, isolated under `.forge/<runId>/`. |
| **Capability profile** | What a given host can actually do. Two profiles were designed, `canvas` and `text`; only `text` is built — see below. |

## Tier asymmetry is a design-wide constraint

The roles are **not interchangeable in model strength**, and the old code did not express this — its
`ForgeRole.Reviewer` and `ForgeRole.Builder` chose models the same way. Here the tier is part of the
role's definition:

- **Orchestrator (strong)** holds the interview context, which is why it, and not a worker, revises
  the plan. A fresh reviser process does not have that context, so the revision cannot be delegated
  to a vendor.
- **Builder (cheap)** works against an already-hardened plan, where the decisions were made for it.
- **Critic** judges; the user picks the tier, defaulting nearer the strong end.

The direct consequence for the tool surface: **the plan-review loop cannot live inside a single
call**, because a turn by the host LLM is mandatory between rounds. The code-review loop can, because
the orchestrator is not needed there.

## The critic is fresh each round, but reads the review log

A persistent critic defends its own earlier assessment and normalises what it has already read,
degrading the exact capability it was hired for. A naively fresh critic oscillates: round 3 reopens
what round 1 accepted. But that oscillation comes from missing **information**, not missing memory —
so the fresh process receives the review log as input data ("here is what was raised, here is how it
was closed") and converges without inheriting the anchoring. Judging someone else's prior findings
and defending your own are different acts. The cost is nil: the plan is a few kilobytes and the
system prompt is identical, so caching applies.

Interface consequence: `CanResume` is needed only by the Builder; the Critic is always stateless.

## The App Server spells its sandbox two different ways

Measured against `codex` 0.147.0 on 2026-08-15, by sending a deliberately invalid value and reading
the variants back out of the error:

- `thread/start` takes `sandbox` as a **kebab-case string**: `read-only`, `workspace-write`,
  `danger-full-access`.
- `turn/start` takes `sandboxPolicy` as an **object whose `type` is camelCase**: `readOnly`,
  `workspaceWrite`, `externalSandbox`, `dangerFullAccess`.

The old client sent `readOnly` to `thread/start`, which today's server rejects outright. Nothing
warns about this: the two spellings are close enough to look like a typo rather than a protocol fact.

Two more properties of the same surface, both measured rather than documented: `effort` is *not*
validated by the App Server — a bad level is accepted, forwarded, and fails the turn upstream, so it
surfaces as a failed turn rather than a rejected request. And `thread/resume` needs the thread to
have a recorded rollout, which only exists after a turn has completed; a thread that was opened but
never used cannot be resumed. That is why a Builder's resume token is only worth storing once its
first task is done.

## Only the `text` profile exists

Measured on 2026-08-15 against a spike server built on the MCP C# SDK 2.2.0: Claude Code 2.1.233 and
Cursor 1.0.0 both negotiate protocol `2025-11-25` and report `extensions: null` with no UI
capability. Neither the MCP Apps extension (the `canvas` profile) nor the Tasks extension (streamed
progress) is negotiated by any available host.

So the plan is delivered as markdown in the tool result, approval runs through elicitation — which
does work, including the full multi-round tool response cycle — and progress is observable only at
the granularity of one tool call per unit of work, plus `forge.status` on demand. The `canvas`
branch is not written until a host negotiates the capability; `McpApps.GetUiCapability(...)` from
the SDK is the check that would enable it.
