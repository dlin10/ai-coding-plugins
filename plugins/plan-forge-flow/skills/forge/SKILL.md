---
name: forge
description: Harden an implementation plan through relentless one-question-at-a-time grilling, fresh independent plan reviews, a persistent native Codex builder, and a complete final code review. Use when the user invokes $forge or asks to plan, review, and implement a non-trivial repository change with explicit sign-off.
---

# Plan Forge Flow

Run the four-act workflow mechanically through `scripts/forge.mjs` and use
native Codex sub-agents for all reviewer and builder work.

Before acting, read [workflow.md](references/workflow.md) completely. Its
commands and gates are required, not suggestions.

## Hard rules

- Ask grill questions one at a time.
- For Act 1, invoke an already-available grilling skill when the session exposes
  one; otherwise use the embedded procedure in the workflow reference.
- Finish resolving the Act 1 decision tree before writing the final plan or
  entering Act 2.
- Never modify an existing `PLAN.md` or `PLAN-REVIEW-LOG.md` without the forge
  ownership marker.
- Spawn a fresh `forge_reviewer` for every review round.
- Treat every reviewer as stateless. Supply the complete review log and save
  every critique before consuming its verdict.
- Remain the final arbiter on every `REVISE`: accept valid findings, reject
  invalid ones, and log both decisions with reasons.
- Keep one `forge_builder` agent alive for the whole build and send exactly one
  locked plan step per follow-up.
- Never write code before the user explicitly signs off on the final reviewed
  plan. Persist the user's exact affirmative words with `confirm-signoff`.
- Pass the selected model and reasoning effort explicitly at every spawn.
- The orchestrator is always the current Codex session model/effort; never ask
  the user to identify or choose them.
- At the beginning of Act 2, choose and persist the reviewer model and effort
  before dispatching any review.
- Reuse that exact pinned reviewer model and effort for every Act 4 review; do
  not ask for or configure a separate Act 4 reviewer selection.
- Require Act 4 to review material performance regressions. For .NET changes,
  instruct the reviewer to invoke `Analyzing Dotnet Performance` when that skill
  is available.
- At the beginning of Act 3, ask the user to choose the builder model and
  effort, then persist the answer before `begin-build`.
- Never use `ultra`.
- Do not change a run's selected models or efforts without a user-supplied
  reason recorded through the CLI.
- At any loop cap, stop and ask the user to authorize exactly one additional
  round, accept risk and advance, or stop the workflow. Never extend a cap or
  advance on the user's behalf.
- Never infer a malformed or missing reviewer verdict. Use the allowed retries,
  then present the same three choices to the user.
- Route every state change through `forge.mjs`.
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
