---
status: accepted
---

# Remove Cursor as a host and as a vendor

Limits [0006](0006-worker-acts-as-jobs-on-the-cursor-host.md),
[0007](0007-serve-live-catalogues-to-the-interview.md),
[0008](0008-render-the-plan-on-a-canvas.md), [0012](0012-reach-codex-through-exec.md),
[0017](0017-grant-worker-tools-by-exact-server-name.md) and
[0023](0023-refuse-a-fast-request-nothing-confirmed.md) where they rest on Cursor. Every other
mention of Cursor in an earlier record says what held when that record was written.

## Context

Until 0.58.0 the plugin supported Cursor twice over.

As a **host**, it shipped `.cursor-plugin/plugin.json` and an entry in the monorepo's Cursor
marketplace, and the skill sent every worker act there, Scout included, through
`forge.work.start` → `forge.work.poll` → `forge.work.fetch`, because the Cursor host cancels a tool
call at 60 seconds (0006). `forge.work.poll` held a client whose name contains `cursor` to a
45-second wait.

As a **vendor**, `cursor-agent` could fill the Critic, Builder or Scout role: `Vendors/Cursor/`
and `prompts/cursor/`, a catalogue collapsed from about 200 raw ids into families (0007), effort and
Fast joined into the model id with a `-fast` suffix that `IVendor.Normalize` also read back out of a
model or effort (0023), and — the one vendor with no schema flag — structured output through
`SchemaInPrompt`: the schema in the prompt, validation here, one retry (0012).

On 2026-10-22 the owner decided to stop supporting Cursor in this plugin: in the code, the
instructions, the skill and the documentation.

## Decision

From 0.59.0 Cursor is neither a host nor a vendor.

- The Cursor manifest and the plugin's Cursor marketplace entry are gone, and `build/package.ps1`
  refuses a bundle that carries either, or `prompts/cursor/`. The monorepo validator lets a Claude
  catalog entry be missing from the Cursor catalog when its plugin ships no Cursor manifest.
- `VendorFactory` knows `claude` and `codex`; `cursor` is refused like any other unknown id.
  `CursorAgentVendor`, `CursorAgentSession`, `SchemaInPrompt`, `IVendor.Normalize`,
  `ProviderUsage.Cursor`, `WorkerTools.RecordBlanket` and the PATH-fallback overload of
  `ExecutableResolver.Resolve` had no other caller and went with it.
- `forge.work.poll` no longer reads the client's name: every client may wait 1 to 3000 seconds, and
  one that names no wait still waits 45.
- The job tools stay for the Builder acts alone. Since 0006's amendment of 2026-10-21 they carry a
  Builder act that may outlast a host's hour, so `forge.work.start` runs `build.next` and
  `review.fix` and refuses `plan.review`, `review.code` and `scout`, which only the Cursor routing
  sent through a job; their arguments leave its schema, and `PlanReview.RequireDraft`, which only
  the job route asked, goes too. Scout, plan review and code review use their one-call tools.

  > **Amended 2026-10-22 ([0044](0044-overlap-scout-with-the-orchestrators-own-work.md)).** Scout
  > is a job again from 0.60.0, on every host, so the Orchestrator can work while it runs:
  > `forge.work.start` runs `build.next`, `review.fix` and `scout`, and still refuses `plan.review`
  > and `review.code`.
- The `Canvas` profile and `forge.plan.show` stay. They answer the MCP Apps capability, not a host's
  name (0008). Cursor was the one host measured to negotiate it, so until another does they stay
  dormant: Claude Code reports `Text`, and Codex has not been measured.
- `PLANFORGE_SELF_EXCLUDED` stays. Cursor workers carried it, and the Claude vendor sets it on
  `claude mcp list` so that this plugin's own server exits instead of starting for a listing. The
  constant now lives on `ClaudeCliVendor`.

Rejected: dropping only the host or only the vendor. Each half still needs most of what the other
does to be tested and documented, and the decision was to drop both.

## Consequences

- A run begun before 0.59.0 with `cursor` selected for a role fails the next act that names it, on
  the unknown vendor id. Naming another vendor — for Scout, a new `forge.scout.select` — carries the
  run on; nothing rewrites saved state.
- An installation made from the Cursor marketplace keeps the version it has: the marketplace no
  longer lists the plugin, so nothing updates it.
- An Orchestrator that still sends a critic or Scout act to `forge.work.start` is refused before
  any vendor starts, with the two acts a job runs named in the error.
- No code path validates a worker's structured output itself any more: both remaining vendors hand
  the schema to their CLI. A future vendor without a schema flag would have to bring that back.
- The suite lost the Cursor unit and integration tests. Tests that used Cursor only as a sample of
  a shared behaviour — usage counters, Scout structural validation, host fields a worker returns, a
  client with no Host session, the launcher's self-exclusion — now use Codex, Claude or a neutral
  client name.
- `CONTEXT.md` no longer carries the Cursor measurements; they stand in its 0.58.0 text, which the
  earlier records here cite. The decision records keep what they decided at the time, amended where
  it changed.
