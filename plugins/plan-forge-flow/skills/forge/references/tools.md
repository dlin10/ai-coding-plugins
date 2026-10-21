# Forge reference: the tools and what they return

The full contract of every forge tool, how worker acts are routed on each host, and what to do with the documents their results carry. The core's tool table gives the argument shapes; this file gives the rest.

## The tools, in order

| Tool | When |
|---|---|
| `forge.begin` | Once, before anything else. Returns the `runId`, the connecting `client`, the configured and partly configured `advisors`, and the capability `profile`, takes a baseline of the working tree, and starts every vendor's catalogue probe in the background. Its optional `workerTools` names the MCP servers every critic, builder, and scout may call without being asked; omit it and they get the Roslyn servers (`roslyn-*`). Pass it only when the task needs another server, and never name one that changes files — all three roles get the same grant. |
| `forge.models` | At the first Scout need and again immediately before the Critic/Builder Vendor question. Returns each vendor's model catalogue, newest first, with `available` and the reason when a vendor is not; successful entries are served from `CatalogCache`, while unavailable entries are probed again. |
| `forge.scout.select` | Once, just in time when broad reconnaissance first becomes necessary. Persists one exact Scout Vendor/model/effort selection or the explicit decline to continue without Scout; a later enabling call must also be explicit. |
| `forge.scout.run` | On non-Cursor hosts, runs one bounded Scout question directly. Always pass the required `sessionMode`: `continue` only for a direct follow-up in the same investigation, or `fresh` for an independent question, new subsystem, stale evidence, or deliberate reset. It returns the complete Scout answer under `scout`, unclipped, and appends it to `scout-answers.json`. |
| `forge.evidence.map` | After the manual Evidence check and before plan review, asks a configured Advisor about every Scout item and supplied implication. Pass every Impact pass Change point in `changePoints`, optional `implications` with Scout item IDs in `derivedFrom`, and the selected `advisor` when needed. Returns flags, progress, `complete`, `checkedThisCall`, not-indexed answer numbers and document paths; call directly on every host. |
| `forge.instructions.set` | Once, at the end of Act 1, when the user answered either instruction question with something. Records what they want the critic told and what they want the builder told, verbatim. Omit a role to leave it as it stands, pass `""` to clear it; skip the call entirely when both answers were "no instructions". |
| `forge.plan.write` | For the first draft and genuine rewrites. Writes the complete draft to `PLAN.md`, runs no worker, and answers with the outline read back from the file (null for blank plan text) and its path under `documents`. Surface that path, then run the round. |
| `forge.plan.outline` | Reads the current plan hash and snapshot Edit targets: ID, kind, label, hash and size, without plan text beyond labels. Refuses a missing or blank plan; write the first draft with `forge.plan.write`. |
| `forge.plan.patch` | For a revision that changes some targets. Pass the outline's `planHash` as `baseHash` and nonempty `edits`, each with `op`, `target`, `targetHash` and, for replace/insert, nonblank `content`; delete has no content. Operations are `replace`, `delete`, `insertBefore` and `insertAfter`. Applies against one snapshot, runs no worker, and answers with the new outline, change summary, sizes, `tasksKept`, `firstTaskOwed` and `documents`. On a stale base, read `forge.plan.outline` and redo the edits against its hash. |
| `forge.plan.review` | On non-Cursor hosts, once per round, after `forge.plan.write` or `forge.plan.patch` and with `planDraft` omitted. Apply the plan decisions from the previous critique in `decisions` before the new Critic runs. **You** revise the plan and say in `revision` what changed — required from the second round on. |
| `forge.plan.show` | On a `Canvas` profile only, once the critique settles. Renders `PLAN.md` as a document with the drift beside it, answers with its `planHash`, and records nothing. Send no `plan`. |
| `forge.plan.confirm` | When the critique settles and you have shown the user the plan and asked them. Send no `plan`: it approves `PLAN.md` as it stands. Pass `planHash`, the hash of the plan they saw — from `forge.plan.show`, from your last `forge.plan.write`/`forge.plan.patch` outline, or from `forge.plan.outline` — and a plan that moved since is refused before anything changes. With `approved: true`, it accepts the same complete plan-decision shape as `forge.plan.review`, applies final closures, and refuses while any active plan finding is unresolved. With `approved: false`, send no `decisions`; PLAN/state/ledger and existing approval stay unchanged. Optional case-sensitive `fullGate=beforeNextRound\|final` records full host-check timing; omission preserves it. Omitted settings persist, explicit settings replace, empty settings clear; approval clears the global gate failure, preserving attempt history. Approval keeps the built tasks the revision left; pass `rebuildFrom` with a built task only when the user asks to rebuild from it. |
| `forge.build.next` | On non-Cursor hosts, once per task, repeatedly, until `tasksCompleted` equals `taskCount`. After the builder's turn the server runs the task's gate command itself; a `gate_failed` result is the same task again on the next call. |
| `forge.review.code` | Saved `fullGate=beforeNextRound` refuses review with pending fixes; `final` permits it and returns current `pendingFullGateFindingIds`, including []. Critic assessments do not verify them. On non-Cursor hosts, once per round after the last task. Returns one critique. **You** then filter the findings and call `forge.review.fix`. |
| `forge.review.fix` | On non-Cursor hosts, applies code decisions and optionally runs the Builder for exactly `fixFindingIds` under one `fixAttemptId`. A decisions-only call starts no Builder or gate; it is also where `raises` go, and it answers with their `raisedFindingIds`. The Builder receives the ledger's verbatim findings for those IDs only, followed by your `note`. Optional case-sensitive `gate=full\|targeted` defaults to full and requires fix IDs when explicit. Targeted runs only the Fix gate and leaves findings pending full host verification. Retry with the same gate, attempt and exact IDs. |
| `forge.status` | Reports saved `run.fullGate`, current pending IDs and covering attempt IDs; malformed saved timing stops state reads without mutation. Before asking for approval, after a resumed run, and any time the user asks where things stand. Carries a compact ledger summary with current IDs, dispositions and active phases, the drift, job liveness, and `run.scout` with enabled/selection, current session, and last failure, plus the configured and partly configured `advisors` read from the server process environment at this call, with ids, configured models and variable names/problems, never credential values. |
| `forge.work.start` | On Cursor, starts one worker act, including `scout`; on other hosts, only a Builder act expected to run long (below). `plan.review` and `review.fix` take the same decisions and retry IDs as their direct tools, and `review.fix` the same `note` and optional `gate=full\|targeted` (only with fix IDs); retries preserve gate, attempt and exact IDs. Pending-review and Fix gate checks match direct calls; invalid ledger IDs, phases, states, batch conflicts or fix-attempt sets are rejected before a job is created. If `started` is false, rejoin the returned active `jobId`. For Scout, pass only `question` and explicit `sessionMode`. |
| `forge.work.poll` | Waits for the started job — 45 seconds, or `waitSeconds` — returns the moment it ends, and reports its latest stdout activity and recognised event. On Cursor omit `waitSeconds`; on other hosts pass `waitSeconds: 3000`. A value beyond the client's maximum is clamped and `waitClamped` says so. A `running` result means call it again immediately; it is not narration-worthy and never ends your turn. |
| `forge.work.cancel` | Requests cancellation of one job without waiting for it to stop. Use only when the user explicitly asks, or after showing its liveness and obtaining confirmation; then poll and fetch it normally. A terminal job is a successful no-op. |
| `forge.work.fetch` | The result is a historical completion snapshot; status and a new fix replay show current pending IDs. Fetches the terminal worker result after polling. |

Every tool takes `workspaceRoot` and, after `forge.begin`, `runId`. On a Cursor client, every worker
act — including Scout — goes through `forge.work.start` → `forge.work.poll` → `forge.work.fetch`;
do not call a one-call worker tool there. On non-Cursor hosts, call `forge.scout.run` directly
for Scout, and use the one-call worker tools — `forge.plan.review`, `forge.build.next`,
`forge.review.code`, `forge.review.fix` — for the other acts. Those legacy tools take `model`, an
optional `effort`, an optional `fast`, and an optional `vendor`: `claude`, `codex`, or `cursor`,
defaulting to `claude`. `forge.work.start` takes the same `fast` for every act but Scout.
The critic's selection goes to the two review tools, the builder's to `forge.build.next` and
`forge.review.fix`, and Scout always reuses its persisted selection. Direct and Cursor-background
forms have the same decision contract; a decisions-only `review.fix` has no `fixFindingIds` or
`fixAttemptId` and starts no Builder.
If `forge.work.start` returns `started: false`, rejoin its active job with poll → fetch.

Every worker act answers with its own result beside a `documents` object — the critique under
`critique`, the build under `build`, the fix under `fix`, and the complete Scout answer under `scout`.
On Cursor the act's own payload is the `result` string of `forge.work.fetch`; Scout's result string
is the answer JSON only. `forge.plan.write` and `forge.plan.patch` answer with the outline and
`documents`, and on Cursor `forge.work.start` and every `forge.work.poll` carry `documents` too.
`documents` always holds `flowLog` and `plan`, each with a `path` and what to do with it, and each
`null` until its file exists. Only a
successful direct Scout call or a successful completed Scout fetch also carries `documents.scout`,
whose instruction is exactly: “show the Scout report to the user now, and show it again after each
later successful Scout call appends its answer.” Non-Scout results omit it even after a report
exists. What to do with the documents is below.

Worker calls run for minutes, and the host's clock on a tool call is not yours to extend. On Cursor,
use start → poll → fetch for every act, including Scout, so the surviving server can rejoin a detached
worker; on every other host, call Scout directly and use the one-call tools for the other acts. The
exception on other hosts: their clock cuts any one call at the hour and records nothing, so a Builder
act expected to run long — a task whose gate runs the suite, a fix batch — may go through
`forge.work.start` (`build.next` or `review.fix`) with `forge.work.poll` called with
`waitSeconds: 3000`, which costs one turn per fifty minutes. On Cursor one `forge.work.poll` waits 45
seconds, so an act that takes minutes needs many of them in a row. Either way, keep
calling it, in the same turn, until the state is no longer `running`, and only then fetch. Each poll
result says which call it wants next. A `running` poll is not a result, not narration-worthy, and never
a reason to end your turn — never stop on one to ask the user to continue, because there is nothing
for them to answer. If the originating server process exits, an in-flight job id is unknown to a new
server and cannot be rejoined; after restart, start a new act. Persisted terminal results remain under
`.forge/<runId>/`.

`lastActivityAt` is the last stdout line, including output the parser did not recognise;
`lastEvent` is a short description of the last recognised vendor event. Use them to explain what a
running job is doing, never to invent an automatic cancellation policy: every vendor attempt already
stops after 30 minutes with no stdout. Call `forge.work.cancel` only on the user's explicit request,
or show both fields and obtain confirmation first. Cancellation is nonblocking, so continue with
poll → fetch until the failed terminal result is persisted.

## Show the workers' output as you go

Every tool result lands in your context and nowhere else — the user sees none of it unless you
surface it. The server keeps the user-facing timeline for you: every worker call appends its
outcome to `<runPath>/flow_log.md` — Scout outcomes, critiques with verdict and findings, your own
revision and typed decisions between plan-review rounds, build results with status and files changed,
reopening proposals, fix attempts, gate results and closures. Nothing feeds this file back to a
Worker; it is the audit to show, while the bounded ledger projection is Critic input.

The plan is the second such file. `forge.plan.write` puts the first draft at `<runPath>/PLAN.md`
before the round that judges it starts; later revisions use `forge.plan.patch` for some targets
or `forge.plan.write` for a rewrite, so the user reads the current plan while the round runs rather
than meeting it once at approval.

Both travel as `documents.flowLog` and `documents.plan`, each with its `path` and what to do with
it, on every result that has something to show — `forge.plan.write`, `forge.plan.patch`, every worker
act, and on Cursor the start and every poll as well. The Scout-only `documents.scout` report metadata appears
only after a successful direct Scout call or a successfully completed Scout job fetch. Either
ordinary document can be `null` while its file does not exist yet. **Surface the plan in the same
turn as the write that created it, and the flow log after the first Scout outcome or first critique,
whichever happens first** — surface its live path in that turn and refresh both after every later
Worker act; no host watches the disk for you. Neither waits for the end of the run: a link handed
over once the work is finished is a link to something the user could no longer watch:

- A host that renders local files (the Claude Code desktop app) — show the files, and re-send them
  after each call.
- A host attached to an editor — open each once with `code <path>` or `cursor <path>`. The Cursor
  Agents window renders them as snapshots, so re-run the same command after each call; a VS Code
  tab refreshes itself.
- A terminal or TUI host — give the user the paths once so they can open them in their own editor.

The paths are absolute, and on a host that tells the server where the session is they sit inside it —
even when `workspaceRoot` names a repository root several levels up. Where your host turns a path
relative to your working directory into a link, write them that way; a backticked absolute path
renders as text the user cannot click. If a `documents` path is not under your working directory,
your host did not declare its roots and the absolute path is all there is.

However the files are surfaced, keep one line of narration in chat per worker call: the verdict and
finding count, or the task built and its status, so the user sees the run move without opening
anything. When chat is all the host has, expand that line to the findings themselves — severity,
where, what and ID — and for code review say which IDs you fixed, deferred, rejected, reopened or
closed, with the reasons and gate outcome.
Never paste raw JSON, and never let narration grow into pasting the plan itself — that is what the
`documents.plan` link is for.
