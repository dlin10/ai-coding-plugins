# Plan Forge Flow releases

## 0.20.0

The plan reaches the user exactly once, at approval, and until now it reached them as a wall of
markdown in the chat — the worst place to read a document you are being asked to sign off. The
spike behind [docs/adr/0002](docs/adr/0002-mcp-server-surface-without-enforcement.md) had ruled a
canvas out because no host negotiated the UI capability. That is no longer true: MCP Apps shipped
as the first official MCP extension, Cursor implements it, and runs orchestrated from Cursor have
been quietly recording `profile: "Canvas"` against a branch that was never written.

- `forge.plan.show` renders the plan as a document in the host's own UI, with the working-tree
  drift above it. Display only: it writes nothing and decides nothing, approval is still
  `forge.plan.confirm` recording an answer the orchestrator collected, and the canvas says so to
  the user. See [docs/adr/0008](docs/adr/0008-render-the-plan-on-a-canvas.md).
- Nothing changes for a host that does not negotiate the capability. Verified against the published
  binary with two clients, one advertising the extension and one not: `tools/list` is identical
  either way, and `_meta.ui` appears on this one tool alone. Claude Code and Codex keep the twelve
  tools they had; what they also see now is a `resources` capability and one `ui://` resource.
- The canvas document is self-contained — no stylesheet, font or script from any origin — because
  the host frames it under a CSP that allows none, and a plan that renders as unstyled text at the
  moment of approval is worse than no canvas at all.

## 0.19.0

A Cursor run left a flow log holding four plan-review verdicts in a row — three of them `revise` —
with nothing between them to say what the plan changed in answer to any of them. Revising the draft
is the one act the orchestrator does not delegate, so the server never saw it and never wrote it
down, and the timeline read as a critic contradicting itself. The same run surfaced the log's path
in its closing message and nowhere earlier, by which time there was nothing left to watch.

- `forge.plan.review` now takes `revision` — what you changed in answer to the previous round's
  findings — and refuses the call without it from the second round on. It lands in `flow_log.md`
  ahead of the round it answers into, and no worker ever reads it.
- The same call takes an optional `deferred`, for findings answered with a decision rather than a
  change. Like the code-review loop's, it lands in the review log as well, so the next round's
  critic treats those findings as settled instead of raising them again.
- Every worker act result now carries a `flowLog` object — the path plus what to do with it — from
  the moment the file exists. The instruction to surface the timeline used to live only in the
  skill, and an hour into a run the skill is no longer what the orchestrator is reading. This is
  the remedy 0.18.2 gave `forge.work.poll`, applied to the one thing the user actually watches.
- Both arguments are also accepted by `forge.work.start` for the `plan.review` act, and the missing
  revision is refused there rather than inside the job, so it stays an argument error with no vendor
  behind it.

## 0.18.3

A Cursor critic returned a verdict two and a half minutes into its act, and the run failed twenty
minutes later with "The operation was canceled". `cursor-agent` had finished and exited; an MCP
server it spawned kept the stdout handle it inherited, so the pipe never reached EOF — and EOF was
what the reader was waiting for. The critique sat complete in the vendor's own session store while
the job spent the rest of its timeout, and because the process was long gone by then the run log
recorded neither a kill nor an exit to say what had happened. Every vendor read output this way, so
this was luck rather than a Cursor-only fault.

- A vendor process ending now ends its stream. Each read races the next line against the process's
  own exit, and once it has exited the pipe is drained for two seconds — long enough for output it
  already wrote, short enough that a handle held by something else costs nothing. The stderr tail
  is bounded the same way, for the same reason.
- The prompt write moved inside the block that kills and logs. A vendor that never drains its stdin
  blocked that write where nothing was watching, which left a live process behind and a log holding
  nothing but a launch line.

## 0.18.2

A Cursor run stopped mid-review and asked the user to type "continue". One `forge.work.poll` waits
45 seconds, and a critic on a reasoning model takes minutes, so a `running` result is the normal
case rather than the exception — but the instruction to poll again lived only in the skill, and a
host far enough into a run to have moved past it reads a bare `running` as the end of the wait.
Nothing was lost: the job kept going and poll → fetch rejoined it. It was still a stall on a call
the orchestrator could have made itself.

- `forge.work.poll` now answers with the call it wants next: another poll while the job runs, a
  fetch once it stops, and on `running` an explicit refusal to end the turn or ask the user. The
  payload travels with the result, so it survives the skill falling out of the host's attention.
- The skill says the same thing as a prohibition rather than a note, and names the 45-second bound
  so the many-polls-in-a-row shape is visible before the first one returns.

## 0.18.1

A Cursor critic or builder ran without the solution's MCP servers: headless `cursor-agent` loads
global and plugin servers but drops workspace `.cursor/mcp.json` entries unless they are approved
at launch, and the persistent approval `cursor-agent mcp enable` writes does not reach print mode
(measured against 2026.08.11-e8db854). A reviewer told to verify a plan against Roslyn MCP found
the server missing every round and fell back to text search — the same symptom the Codex critic
showed for a different reason.

- Both roles now pass `--approve-mcps`. Plan mode never blocked MCP calls, so the critic guard is
  unchanged; the flag approves every configured server for the session, so a plan-mode critic can
  also reach the user's global MCP servers — a server that must stay out of reach belongs behind
  `cursor-agent mcp disable`.

## 0.18.0

The critic judged a plan for completeness alone — `approve` once nothing is left for the implementer
to guess at — so a plan that was detailed, self-consistent and aimed at the wrong thing passed
without a finding. What the run was actually for lived in the orchestrator's interview context,
where no worker and no later session can read it.

- The plan now states its own intent: a `## Requirements` section above `## Approach`, numbered
  `R1`…`Rn`, carrying what must become true, what must not change and what the run excludes, with
  every task citing the requirements it serves. No new artifact and no new tool — `PlanTasks` still
  walks only what is under `## Approach`, and both review acts already send the whole plan.
- New `prompts/requirements-contract.md`, appended for plan review exactly as `scope-contract.md`
  is for code review. It puts the requirements themselves under review and asks for coverage in
  both directions, settling only the exclusions: a yardstick the critic may attack is what keeps it
  from degrading into a conformance checker against a wrong requirement.
- Verification became findable. Every task ends in a `Gate` — the command or condition showing it
  done — and checks no single task owns go under an optional `## Gates` section that the
  orchestrator runs itself after the last task and before the first code-review round. Not the
  builder, whose session is per task; not the critic, because a build writes into the tree it is
  judging.
- A requirements finding the interview never settled goes back to the user mid-loop rather than at
  approval, where its answer would invalidate every round run since.

## 0.17.0

Two interview-time gaps, both in the orchestrator's instructions rather than the server. The skill
ladder's first step named `grill-me` and `grill-with-docs`, which the current generation of those
skills hides from the model-facing catalogue and reduces to one-line aliases of `grilling` — so
step 1 was unreachable, and resolving past it told the user an installed skill "is not in the
catalogue". And nothing tied the plan's depth to the builder that would execute it: the model
questions had no fixed position in the flow, so a plan could be drafted before the builder was
even chosen.

- The ladder is two steps: `grilling` (plus `domain-modeling` in documented mode), then the
  built-in rules. The orchestrator says which skill is running the interview and never claims a
  skill is missing — a catalogue omits slash-only skills that may well be installed.
- Vendors and models are chosen at the end of Act 1, before the first draft. The plan's depth
  scales inversely to the builder's model and effort, every task is written to be read alone, and
  the preamble names the builder's selection.
- The critic prompts judge depth against the builder the preamble names, falling back to the old
  "competent implementer" bar when no builder is stated.

## 0.16.0

The interview asked for models out of the orchestrator's training data while the code already knew
the real answer and threw it away (#23): `ProbeAsync` filled each vendor's catalogue, had no
production caller, and no tool read the result.

- New `forge.models` tool serves every vendor's catalogue to the interview: availability with the
  probe's reason, `live` versus `declarative` source, and per model the display name, description,
  effort levels, and the vendor's own defaults. `forge.begin` starts all probes in the background,
  so by the vendor question the answer is already cached; a failed probe is not cached, and the
  skill no longer offers a vendor whose probe failed.
- Codex keeps `model/list`'s own order — measured already newest-first, with `isDefault` marking
  the vendor's pick — and its parser now reads the display name, description, and default effort.
- Cursor's ~200 raw ids collapse into families: strip `-fast` and the effort suffix, group, and
  advertise exactly the variants the list contained (`default` names the bare id and joins to
  nothing). Families sort newest first by the version parsed out of the id, segment-wise and
  numeric — `claude-opus-4-8` is 4.8, not 48 — with versionless ids at the tail. The bracket
  overrides the CLI's own tip advertises were measured on 2026-08-19 and rejected even for the
  tip's own example, so the suffix join stays and can only rebuild observed ids.
- Claude's catalogue remains declarative — re-verified against `claude --help` — and is now
  labelled as such instead of pretending to be live. See docs/adr/0007.

## 0.15.1

The review window was blind to every new file, not just new ADRs (#25): `git diff` lists neither
untracked nor staged files, so a change that added a whole new module was reviewed as if the module
were not there, the critic reported confident findings about its absence, and a file created during
the interview never surfaced as drift at approval.

- The window all four consumers share — baseline capture, drift report, code-review diff, and the
  sensitive-path guard — is now the working tree against `HEAD` plus untracked files rendered as
  new-file diffs, composed with `ls-files` and `diff --no-index` so the server never stages
  anything. The documentation exclusions are unchanged, and `.forge/` stays out because it ignores
  itself.
- Recorded limits that remain: a commit made mid-run moves `HEAD` and takes its changes out of the
  window, and an empty new file renders no hunk, so it stays invisible.

## 0.15.0

A builder that could execute nothing still reported its task `done` (#24): `status` was `done` or
`blocked`, so a worker whose sandbox denied every spawn answered `done` and put the caveat in
prose, and the 224 `vendor.tooluse` events that run logged carried only the item type — the denial
never reached the log.

- `BuildResult` carries a required `verification` object — `outcome: passed | failed |
  unavailable` plus `evidence` — so "implemented but could not prove it" is expressible. The
  report is the builder's word by design; the server records it into the flow log and the tool
  result, and the skill directs the orchestrator to run the task's verification step itself on
  `unavailable` or `failed` before advancing.
- The run log now records tool outcomes, not just tool names. Codex `item/completed` events carry
  the command, exit code, and an output tail; Claude `tool_use` inputs and `tool_result` payloads
  (with `is_error`) are parsed out of the stream. Cursor's intermediate events remain unmeasured
  and unread. New `vendor.toolresult` entries keep each value a separate JSONL field, cut by the
  existing truncation.

## 0.14.0

Worker acts now run as background jobs with one active job per run and persisted terminal results,
bounded shutdown reaping, and Cursor-safe `start` → `poll` → `fetch` routing. The MCP surface adds
`forge.work.start`, `forge.work.poll`, and `forge.work.fetch`; `forge.status` reports the active
job, and the Forge skill documents rejoining jobs and the blank-findings deferred path.

## 0.13.0

A failed run left nothing to investigate (#20). `review-log.md` and `flow_log.md` record the
results of acts that succeeded, so an act that threw wrote nothing at all — the run behind #19 kept
`state.json` and no record of whether `cursor-agent` was spawned, with what arguments, or how it
died. Vendor lifecycle events existed but only streamed into a channel nothing reads.

Every run now writes `.forge/<runId>/forge.log`: one JSON object per line, appended through the
same write path as the rest of the run folder, so concurrent server processes cannot interleave
inside an entry.

- The tool surface records every call — the tool, its arguments with long fields such as
  `planDraft` truncated rather than dropped, and then its result, its exception with the stack, or
  its cancellation. Cancellation is the entry with no other trace: it is how a host's timeout looks
  from inside the server.
- `StreamingProcess` records each launch with the executable, the full argument list and the
  working directory, then the pid, the exit code and a bounded stderr tail. A kill says which clock
  ran out — the caller's cancellation, the vendor timeout, or the output cap. The
  `--model gpt-5.6-sol-xhigh` line from #19 would have been readable in the log immediately.
- `VendorEvent`s are persisted as they are raised, instead of only reaching the unread channel.
- The MCP SDK's own `ILogger` output now lands there too, through a provider that resolves the
  current run per entry. `ClearProviders()` had been discarding it, and it is the only record of a
  call that dies inside the SDK before any act of ours writes anything.
- New tool `forge.log.append` gives the orchestrator a sanctioned way in — what it selected, what
  it retried, why it deferred a finding. A tool rather than a documented exception to "do not
  hand-edit anything under `.forge/`": the run id keeps passing the same containment check, and the
  format stays one thing rather than one per agent.

## 0.12.1

`CursorAgentSession` loaded the role prompt from `prompts/cursor/<role>.md` and then never sent it,
so Cursor critics and builders ran without their role instructions. cursor-agent has no
system-prompt flag (measured against 2026.08.11-e8db854: the help lists none), so the instructions
now travel at the head of the prompt itself, ahead of the task and the schema contract — the
counterpart of Claude's `--append-system-prompt` and the Codex App Server's
`developerInstructions`.

The host-timeout half of #19 is settled by measurement (probe MCP server, 2026-08-18; the numbers
are in `CONTEXT.md`):

- The Claude Code manifest now sets `"timeout": 3600000` on its server entry — the hour the Codex
  host already grants through `tool_timeout_sec`. Measured against Claude Code CLI 2.1.234, the
  per-server field is honored as a hard wall clock and lifts the client's idle abort, which would
  otherwise cut a worker call that stays silent past the stdio default.
- The Cursor manifest is unchanged because nothing can be set: no Cursor schema has a timeout
  field, cursor-agent's MCP client sends no `progressToken` and cancels a tool call at a hard 60
  seconds, and Cursor does not reset its clock on `notifications/progress`. Wiring the vendor
  events channel to MCP progress was therefore rejected; the channel stays deliberately unread.
  The skill now warns the user when orchestrating from Cursor instead.

A run orchestrated from Cursor sent `model: "gpt-5.6-sol-xhigh", effort: null` and timed out at the
MCP layer (#19). Measurement disproved the suspected cause: that id is real in Cursor's line-up,
and cursor-agent rejects a genuinely unknown model in seconds with a clear stderr message that the
server already surfaces — the timeout was the host's own tool-call timeout on a long review. What
was left to fix is making bad selections read as bad requests, and stopping the selection flow
producing confusing ones in the first place (#18).

- A failed cursor-agent run now names the selection it was given — model, effort, and the joined
  id actually sent — so a vendor rejection reads as a request to correct rather than
  infrastructure to retry with the same payload.
- `forge.begin` reports the connecting client (the MCP handshake's `clientInfo.name`) in a new
  `client` field, so the skill can branch on the host without guessing.
- The skill skips both vendor questions when orchestrating from inside Cursor: every model there
  runs through the one `cursor-agent` CLI, so the vendor distinction is already the model choice.
  Both roles default to the `cursor` vendor and only the two model questions remain.
- Cursor model ids carry their effort as a suffix (`gpt-5.3-codex-high`); the skill now offers
  full ids exactly as `cursor-agent --list-models` spells them, passes the chosen id as `model`
  with `effort` unset, and never invents an id by appending a suffix.
- Records the measurements in `CONTEXT.md`: the ten-second rejection, the stdin draining that
  rules out a prompt-size pipe race, and that current Cursor now negotiates the UI capability.

## 0.11.0

The workers' output now has a user-facing home. Tool results land only in the orchestrator's
context, so unless it narrated every round, the user watched the run blind.

- Adds `flow_log.md` to the run folder: every critique (verdict, summary, findings), every build
  result (status, summary, files changed) and every fix round (kept findings, deferrals with
  reasons, builder outcome) is appended as it happens. Nothing feeds it back to a worker —
  `review-log.md` remains the critic's input and is unchanged.
- The skill now tells the orchestrator to surface the flow log with whatever the host has — the
  Claude Code desktop panel, `cursor <path>` into the Cursor Agents window, an editor tab — and
  to refresh it after each worker call, with a one-line narration per call in chat. Measured on
  Cursor 3.15.19: the Agents window renders `.forge/` markdown as Preview, as a snapshot that a
  repeated `cursor <path>` refreshes.

## 0.10.0

The critic-to-builder loop no longer runs sealed inside one call. Running the flow on this
repository showed why it cannot: when the critic demands work the approved plan excluded, only the
orchestrator can arbitrate, and the sealed loop had locked it out — the diff grew every round
instead of converging. See `docs/adr/0005-code-review-through-the-orchestrator.md`.

- **Breaking.** `forge.review.code` runs one critic round per call and returns the critique, like
  `forge.plan.review`. It takes `model`, `effort`, and `vendor` for the critic; the six
  role-qualified parameters are removed.
- Adds `forge.review.fix`: the orchestrator passes the findings it kept to the builder, and records
  the ones it deferred — each with a reason — in the review log, where the next round's critic
  reads them as settled and the user sees them when the review ends.
- The code-review critic now receives the approved plan alongside the diff, plus a shared
  `prompts/scope-contract.md` appended at load time: out-of-plan demands are `minor` notes, never
  grounds for `revise` on their own.
- Code-review rounds are counted in the run state against their own cap, and continue the plan
  review numbering, so a second review run no longer overwrites earlier `critiques/round-NN.json`
  files.
- Code review and the fix step now require an approved plan, since both judge or repair the diff
  against it.

## 0.9.0

- **Breaking.** `forge.review.code` now takes separate critic and builder model, vendor, and
  effort parameters; the legacy `vendor` parameter is removed.
- **Breaking.** Working-tree drift, the code-review diff and the sensitive-path guard share one
  pathspec that excludes `CONTEXT.md` and `docs/adr/**` at any depth, so documentation written
  during the interview is neither reported as drift nor sent to a vendor. The guard now runs before
  the empty-diff return, and covers exactly what is sent: a sensitive *name* under an excluded path
  no longer aborts the run, which is what stops an ADR called `0005-token-rotation.md` from killing
  every review. A third party's edit to those paths is invisible too — see
  `docs/adr/0004-documentation-written-during-the-interview.md`.
- Adds two interview modes: without documentation, and with a maintained domain model. The skill
  availability chain now makes the `grilling`, `domain-modeling`, `grill-me`, and
  `grill-with-docs` requirements explicit, including the built-in fallback when the host publishes
  no catalogue or a composite step is only partly available.
- Adds the documented-mode write boundary: before approval, the orchestrator may write only
  `CONTEXT.md` and files under `docs/adr/`.
- Makes the builder resume token vendor-aware and clears it on a fresh session that returns no
  token, so a token cannot outlive the vendor session that created it.
- Adds per-role vendor, model, and effort selection, allowing the critic and builder to use
  different vendors and model tiers.

## 0.8.0

Approval no longer runs through MCP elicitation. A host can declare the
capability, answer on the user's behalf and render nothing, and the server cannot
tell that from the user refusing — so 0.7.0 stalled runs with no dialog on screen
and no explanation anywhere. See
`docs/adr/0003-approval-through-the-orchestrator.md`.

- **Breaking.** `forge.plan.approve` is removed. `forge.plan.confirm` replaces
  it: the orchestrator shows the plan, asks, and passes back the answer. The
  surface stays at six tools.
- **Breaking.** `forge.status` returns `{ run, driftedFiles }` rather than the
  run state alone. Drift belongs there because the orchestrator has to show it
  before asking, and the decision call is where it would arrive too late.
- Deletes `IOrchestrator`, `NegotiatedOrchestrator`, `PlanPresentation` and
  `CanElicitApproval`, which existed only to compose and gate the elicitation
  message. `CapabilityProfile` stays: `forge.begin` still reports it.
- Fixes `ApproveResult.driftedFiles`, which every approval path returned empty
  whatever the working tree looked like. Drift was computed only for the text of
  the elicitation and never left the server.
- The server advertises its real version. `serverInfo.version` was the literal
  `"2.0.0"` from the first commit of the MCP server onwards — a version no
  release ever had, and one that could not distinguish 0.7.0 from 0.8.0 in a bug
  report. It is now read from the assembly, which packaging stamps from the
  manifest.
- `skills/forge/SKILL.md` keeps the drafts out of the conversation: the plan is
  shown to the user once, when the critic returns `approve`, rather than round by
  round. It also spells out the four steps of asking, and that an amended plan
  goes back through review.

## 0.7.0

Rewritten as an MCP server. The plugin is now `planforge` exposing six tools —
`forge.begin`, `forge.plan.review`, `forge.plan.approve`, `forge.build.next`,
`forge.review.code`, `forge.status` — instead of a CLI driven by host hooks.

- Adds `IVendor`: the critic and builder roles can be filled by Claude Code,
  the Codex App Server, or `cursor-agent`, chosen per call. Structured output
  is a hard interface requirement; the two vendors without a native schema get
  it through the prompt with validation and one retry on our side.
- Makes the host agent the orchestrator: it runs the interview and revises the
  plan between review rounds. Plan review is one round per call; code review
  runs its whole critic-to-builder loop inside one call.
- Removes all enforcement. The hooks, the twelve Claude agent descriptors, the
  Codex agent TOMLs, the parallel Cursor tree, the plan-mode gates, the
  execution lease, the run locks, and the `refs/plan-forge/*` refs are gone.
  Working-tree drift between `forge.begin` and `forge.plan.approve` is shown to
  the user rather than prevented. See
  `docs/adr/0002-mcp-server-surface-without-enforcement.md`.
- Keeps two checks: no prompt carrying secrets is handed to a vendor, and
  nothing is written outside `.forge/<runId>/`.
- Moves role prompts to `prompts/<vendor>/{critic,builder}.md`, editable
  without rebuilding, with one shared Roslyn contract instead of two copies.
- Drops the Node.js requirement along with the hooks that needed it, and the
  launcher scripts along with the hooks that invoked them.
- Narrows release and CI support to Windows x64. Distribution contains one LFS
  executable and one `plan-forge-flow-0.7.0-win-x64.zip` asset.

## 0.6.2

- Automatically arms a schema-v1 external Claude activation for both direct
  `/plan-forge-flow:forge` and model-invoked Skill entry paths.
- Adds a synchronous `ExitPlanMode` gate that requires Act 2 review, finalize,
  `Ready`, and exact normalized reviewed-plan identity without auto-approving
  Claude's native dialog.
- Adds session-bound `run begin`/`run abandon`, activation-aware planning
  commands and status, explicit cross-session takeover, and deterministic
  activation cleanup after materialize, abandon, or run cleanup.
- Ships a Node.js RID dispatcher with active-run fail-closed behavior, updates
  the Claude Code minimum to 2.1.232, and packages the gate in all six bundles.

## 0.6.1

- Moves Claude OpenAI readiness into doctor with native/npm-shim resolution, structured ready/absent/unusable results, a live ordered catalog, and an explicit continue-without-Codex or stop-and-repair gate before provider-first reviewer and builder selection.

## 0.6.0

- Adds a Claude Code 2.1.226+ plugin manifest and marketplace entry alongside the existing Codex and Cursor surfaces.
- Adds six reviewer and six builder definitions for inherited/no-override through max effort; invocation-time model selection remains unpinned in agent frontmatter.
- Gives Claude reviewers an explicit least-privilege allowlist of four file/discovery tools and the nine current read-only Roslyn MCP tools, without wildcard MCP access.
- Adds asynchronous, fail-open `PostToolUse(Agent)` and `SubagentStop` evidence hooks that write only under Claude's external plugin-data directory.
- Adds a shared Roslyn-first reviewer contract with exact audit markers, audited text fallback, and host-side solution-identity verification for Claude, Codex, Cursor, and OpenAI-facing prompts.
- Adds structured nonblocking Roslyn configuration status to `run doctor`; host skills perform the actual optional semantic capability probe after doctor without changing its verdict.
- Adds Claude's exact Anthropic alias/effort matrix, inherited-model omission, normalized resolved-model evidence with swap detection, and doctor rejection of Claude environment overrides.
- Adds a typed Codex App Server JSONL client, OpenAI-only catalog and identity validation, fresh reviewer and persistent builder lifecycles, and detached atomic session workers with cancellation and heartbeats.
- Adds schema-v2 Forge state and schema-v4 host-neutral pending transactions with provider-qualified role evidence and no automatic migration.
- Adds Claude's exact reviewed-snapshot materialization gate, persistent builder-hold replay, four Anthropic/OpenAI provider pairings, and replacement only after confirmed terminal identity loss.
- Adds two-phase ownership-audited `run cleanup --legacy` for old state, staging directories, scoped refs, managed excludes, and external pending artifacts.
- Release archives now validate all three manifests/marketplaces, the shared skill, Claude hooks and script, exact 12-agent allowlists, and the selected RID binary. Claude evidence hooks require Node.js 18+; the self-contained CLI does not.

## 0.5.2

- Replaces repository-lock booleans with scoped lock tokens and splits Codex fresh, Codex amendment, and Cursor materialization interfaces.
- Completes Cursor plan locking and build preparation in staging, leaving the directory move as the single materialization commit point and removing successor-state reconciliation.
- Splits pending-run schema, workflow, materialization, and Cursor review-evidence responsibilities into focused modules.
- Unifies `run status`, cleans owned materialization staging directories, manages their Git exclude pattern, and records the pending-plan trust model.

## 0.5.1

- Moves Cursor review before native plan creation: the chat draft is staged through stdin, reviewed automatically, finalized after builder selection, and only then materialized as the terminal native plan action.
- Makes `/forge resume` recovery-only and restores the exact native preamble to the materialization gate without a resume requirement.
- Adds schema-v2 Cursor pending runs with temporary chat drafts and transaction-only native plan snapshots; native edits after review are intentionally accepted.
- Makes Cursor doctor fail before planning when the workspace already contains a `.forge` target.
- Adds static plugin validation for chat-first review ordering, terminal native creation, and the exact materialization preamble.
- Restores release packaging on Windows PowerShell 5.1 without weakening bundle path checks.

## 0.5.0

- Adds Cursor 3.15.6 plugin discovery, `/forge`, native editable Plan Mode, and local Build in the current or a new Agent.
- Adds versioned host-aware state, external Cursor PendingRun approval state, workspace-scoped refs, OS-released locks, and recoverable two-phase materialization.
- Reports Cursor reviewer and approval guarantees as advisory and records an explicit per-run model waiver.
- Keeps clean-install Codex behavior and its hook-based PendingPlan workflow.

Pre-0.5 `.forge/state.json` and active runs are intentionally unsupported. Plan Forge Flow does not migrate, clean up, or resume them; inspect or preserve any old artifacts manually, then start a fresh 0.5.x run in a clean workspace.
