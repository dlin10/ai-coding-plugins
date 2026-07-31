---
name: forge
description: Harden an implementation plan through one-question-at-a-time grilling, fresh independent reviews, native Plan-mode approval, a persistent builder, and a complete final code review.
---

# Plan Forge Flow

Run the four-act workflow mechanically through `../../scripts/forge.mjs`, which
lives at the plugin root rather than inside this skill folder, and use native
Codex sub-agents for all reviewer and builder work.

Before acting, read [workflow.md](references/workflow.md) and
[native-plan-ux.md](references/native-plan-ux.md) completely. Their commands
and gates are required, not suggestions.

## Hard rules

- Ask grill questions one at a time.
- Run the `start-plan` preflight before any Act 1 action and stop if it does not
  confirm a fresh Plan-mode prompt.
- Ask every bounded-choice question through `request_user_input` when that tool
  is available, one question per call, recommended option first. Use the
  required two-option pagination contract for model and effort pickers.
- For Act 1, invoke an already-available grilling skill when the session exposes
  one; otherwise use the embedded procedure in the workflow reference.
- Finish resolving the Act 1 decision tree before entering Act 2. Keep the
  complete canonical plan and review evidence in conversation until native
  implementation approval materializes them in Default mode.
- Never modify an existing `PLAN.md` or `PLAN-REVIEW-LOG.md` without the forge
  ownership marker.
- Spawn a fresh `forge_reviewer` for every review round.
- Treat every reviewer as stateless. During Act 2, supply the complete plan and
  review log in its prompt. After materialization, save every critique before
  consuming its verdict.
- Remain the final arbiter on every `REVISE`: accept valid findings, reject
  invalid ones, and log both decisions with reasons.
- Keep one `forge_builder` agent alive for the whole build and send exactly one
  locked plan step per follow-up.
- Never write code before Codex's native plan widget yields an authenticated
  implementation prompt and `forge.mjs resume` materializes the run.
- Pass the selected model and reasoning effort explicitly at every spawn.
- Never ask for or compare against the orchestrator model/effort. Model priority
  orders the picker only; it is not an eligibility or strength gate.
- At the beginning of Act 2, choose the reviewer model and effort before
  dispatching any review. Keep the choice in conversation until materialization.
- Reuse that exact pinned reviewer model and effort for every Act 4 review; do
  not ask for or configure a separate Act 4 reviewer selection.
- Require Act 4 to review material performance regressions. For .NET changes,
  instruct the reviewer to invoke `Analyzing Dotnet Performance` when that skill
  is available.
- After showing the complete reviewed plan, choose the builder model and effort,
  bind it to that plan hash, then emit only the final native proposed-plan block.
- Never use `ultra`.
- After materialization, keep the selected reviewer and builder pinned. A plan
  revision invalidates the pre-materialization builder choice and repeats the
  preview and picker.
- At any loop cap, stop and ask the user to authorize exactly one additional
  round, accept risk and advance, or stop the workflow. Never extend a cap or
  advance on the user's behalf.
- Never infer a malformed or missing reviewer verdict. Use the allowed retries,
  then present the same three choices to the user.
- Before native implementation approval, keep orchestration state in the
  transcript and remain read-only. After materialization, route every durable
  state change through `forge.mjs`.
- Do not fix pre-existing findings without separate user opt-in.
- Before cleanup, explicitly ask whether the user wants to delete the owned
  `PLAN.md` and `PLAN-REVIEW-LOG.md`. Default to keeping them. Call
  `cleanup --delete-artifacts` only after an explicit yes; otherwise call
  `cleanup`.

## What NOT to do

- Do not use Act 2 to review an implementation; Act 2 reviews the plan and Act 4
  reviews code.
- Do not let reviewers or builders commit or stage changes. The working tree is
  the handoff.
- Do not hand-edit `.forge/state.json`, forge refs, or managed exclude blocks.
- Do not replace native Codex sub-agents with nested `codex exec` or Codex CLI
  subprocesses.
- Do not bypass a cap, verdict, sign-off, coverage, ownership, or provenance
  gate without the explicit user action required by the CLI.
