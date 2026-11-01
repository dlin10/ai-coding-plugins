# Plan Forge Flow — domain language

The vocabulary the code and prompts must use. The old vocabulary conflated "host" and "provider";
these terms replace it.

| Term | Meaning |
|---|---|
| **Vendor** | A model supplier that can do work in a separate process: Claude Code CLI, Codex CLI, later Grok. Not "provider" — that word was overloaded. Both are reached the same way, one process per act; see `docs/adr/0012`. |
| **Orchestrator** | The host agent: runs the interview, **revises the plan in response to critique**, calls the tools. Always an LLM, never a C# class. Strong model. |
| **Act** | A major stage of a run. The five delegated acts are classes: `PlanReview`, `Build`, `CodeReview`, `ReviewFix`, `Scout`. The interview is not an act class; it lives in the orchestrator. |
| **Worker** | A separate Vendor process acting as a Critic, Builder, or Scout for one delegated act. Never the Orchestrator. |
| **Self-plugin exclusion** | A Worker cannot reach Plan Forge Flow itself, so it cannot enter another Run. Every other capability inherited from the host remains available. Codex disables the plugin by config and Claude by one-process settings. The explicitly invoked `forge` skill is manual-only and absent from automatic model context. |
| **Job** | One delegated act running in the background, keyed by `jobId` and started, watched, optionally cancelled and collected through `forge.work.start` / `poll` / `cancel` / `fetch`. The shape a worker act takes when it may outlast the host's clock on one call — a long Builder act — or when the Orchestrator works beside it — a Scout question; the one-call tools stay the shape everywhere else. Fresh Scout jobs of a Run run side by side; any other job runs alone. See `docs/adr/0044`. |
| **Worker liveness** | The most recent sign that a Job's Worker is still progressing: the time of its last output line and a bounded description of its last recognised Vendor event; silence is absence of output, not elapsed runtime. |
| **Critic** | The vendor role that **judges**: reviews the plan, reviews diffs. A fresh process each round, fed the Canonical decision ledger's bounded Worker-input projection and the Run's current Decision records. |
| **Builder** | The vendor role that **implements**: writes code against plan tasks and fixes code-review findings — a finding as the rule it breaks, at every place that decides the same question. Never revises the plan. One Builder session per plan task or code-review round. Cheap model. |
| **Builder session** | The Vendor conversation one plan task or one code-review round runs in, recorded as `builderSessionId` with its `builderSessionScope`. The scope's first turn starts fresh; only a retry inside it — a gate failure, a `blocked` answer, a cut-short or killed turn, a round's later fix calls — resumes. A fresh task session is told which files earlier tasks changed, by task number, and nothing else about them. A session for the whole Run grew claude builders past 900k tokens, compacted the Brief away, and cost about 38% more — see `docs/adr/0026`. |
| **Builder Brief** | The approved plan's stable, session-scoped implementation context: the exact non-whitespace Markdown prefix before `## Approach`, including the Builder selection, requirements, exclusions, run-wide gates, and any other preamble sections, while excluding every numbered task. Everything in that prefix is Vendor-bound context, not a place for transient interview notes or host values. On a fresh session — every plan task's and every code-review round's first turn — the act prompt labels a non-empty prefix `# Builder Brief` before that turn's task or kept findings; a retry resumed inside the same Builder session relies on the copy already in its history, and the user's Builder instructions remain the final directly addressed block. Reconfirming a changed Brief invalidates the prior Builder session so it cannot retain stale context, while a change confined to numbered tasks preserves that session because each task is delivered as a turn-specific delta. The Brief is evidence and constraints for implementation, never authority to revise the plan. |
| **Scout** | The read-only Worker role that answers one bounded reconnaissance question with sourced evidence. It never settles requirements, plans, judges, or edits; the Orchestrator decides what evidence belongs in the requirements and plan. Its lazily selected Vendor, model, effort, and Fast choice are retained for the Run, and later calls resume its persistent session when the Vendor supports it. Scout may always use internet access its Vendor or granted read-only Worker tools already provide; no separate per-call authorization exists. |
| **Scout report** | The Run's cumulative record of Scout answers, `scout-answers.json`, the only one there is: every successful answer is appended as its own numbered entry holding the question it answers and the answer's structure as the Scout returned it, and an answer that fails or is refused before it is appended adds nothing. There is no markdown rendering of it. Each item is identified by its answer number, category and position, which never change because answers are only appended. Each answer carries six sourced categories: confirmed facts, material assumptions, open decisions, likely change surface, verification evidence, and Dependents and pinned behaviour. Repository facts cite a path plus line or symbol, and external facts cite a URL. The tool result carries the complete answer it appended, unclipped. |
| **Dependents and pinned behaviour** | The Scout report category that answers "what else breaks" for every location in the likely change surface: its consumers, the tests asserting its current behaviour, and the artefacts outside the code that pin it — snapshots, metrics and baselines, gate scripts, docs, specs, skills and prompts. A location with nothing found is still listed, saying so and naming what was searched. |
| **Change point** | Something the plan's approach changes that anything else can depend on: a symbol, a contract, a wire or persisted format, a counter, a prompt or skill text. The unit an Impact pass asks about. |
| **Impact pass** | The Scout question asked after the plan's approach is drafted and before its first review: it names every Change point and asks, for each one, who depends on it and what pins its current behaviour. Unlike an orientation question, which asks where things are before an approach exists, it is required for every plan. It is itself a Scout need, so it starts the lazy Scout selection when none has been made; it is repeated when a revision introduces a Change point not checked before; and when the Run continues without Scout, the Orchestrator makes the same check itself. |
| **Evidence check** | The Orchestrator's comparison of its plan with every fact in the Scout report that bears on a Change point, made after each Impact pass and before the review that follows it. Each such fact is either used by the plan or departed from on purpose; a deliberate departure is recorded in the Run log, and also in the plan wherever a Critic would otherwise raise it. |
| **Advisor** | An external service that answers bounded typed questions — pick one of the alternatives supplied — over state the server supplies, and returns its pick with how probability spread across the alternatives. TypeSafe's Jev, Microsoft-Decision-1 in Azure AI Foundry and OpenAI's Decisions API are the three; the first two speak TypeSafe's systemone protocol, OpenAI's its own. Not a Vendor and never a Worker: no process, no session, no generated text, and it creates no obligation, quotation or explanation. Its answers are advice: they never approve a plan, close a finding, waive a review, change a gate or certify code, and code checks every answer against the questions it was asked. Not "provider", for the reason given under Vendor. |
| **Configured Advisor** | An Advisor whose every required `PLANFORGE_`-prefixed environment variable is set for the server's process — never a vendor's own variable such as a standard API key, so that it is only ever set on purpose for Plan Forge. Configuring one is the user's consent to send excerpts from any repository forge runs in on that machine. With none configured the Evidence check runs as it always has; with one, it is used without a question; with several, the user chooses, as with a Vendor. A partly configured Advisor is not configured. |
| **Evidence map** | The Run's advisory record of how the Scout report's items bear on the plan, made to assist the Evidence check and never to replace it. It accounts for every item of every successful Scout answer and every implication the Orchestrator supplies, including items with no candidate plan section and judgments left unchecked, and judges each item once per Advisor and model: a later call asks only about items no complete map has judged. Asked for only after the manual Evidence check is complete: it can raise items the Orchestrator then decides as usual, and it removes none — a "covered" from it excuses nothing. Its calls are priced and kept within the Run's Advisor budget. See `docs/adr/0036` and `docs/adr/0046`. |
| **Implication** | An obligation the Orchestrator derived from Scout evidence that no Scout item states outright, handed to the Evidence map with the items it rests on so the map can check it like an item. Always the Orchestrator's own synthesis; an Advisor never creates one. |
| **Matrix task** | A plan task whose test walks every combination of one rule's axes — the inputs its answer depends on — and checks each cell against an expectation table the plan writes from the requirements, never from the code. It names its axes, its excluded combinations with their reasons, and the cells known to be wider than their expectation, each with a reason that becomes an open question; it replaces the one-by-one cases of that rule elsewhere in the plan. The Orchestrator's **matrix check**, made while the approach is drafted and before the Impact pass, decides which rules need one; from the second round on, review adds one when more than half of a round's new findings are combinations or edge cases of one rule. |
| **Run** | One pass, keyed by `runId`, isolated under `.forge/<runId>/`. |
| **Review window** | The final code state a code-review Critic judges: the net change from the Run's baseline commit to its current working tree. If the current history no longer descends from that commit, the window narrows to the current `HEAD` and identifies itself as a fallback; documentation remains outside it. One round may leave further paths out of it (`excludePaths`), only for that call; see docs/adr/0029. A window too long for the Critic's vendor is handed over by path rather than by content, untracked files first; see docs/adr/0034. |
| **Flow log** | The complete user-facing timeline of a run, `flow_log.md`: every Scout outcome, Evidence map call (models and counts without item or plan text), identified Critic finding, Builder result, fix round with its Fix note, Orchestrator revision, Decision record, disposition decision, Raise, reopening proposal and closure. It is appended by the server and never fed back to a Worker; the Canonical decision ledger is the Critic's bounded input. |
| **Canonical decision ledger** | The Run-local source of truth for findings that still matter to a future Critic. Each entry preserves an immutable `origin` (`plan_review` or `code_review`) and has a mutable `activePhase`, so a plan-origin decision accepted for reopening during code review keeps its identity while moving into the code projection. Plan review receives active plan entries; code review receives settled plan decisions and active code entries. Closed entries leave the ledger, keeping only their Invariant in the Invariant register, while the Flow log remains the complete user-facing audit. |
| **Decision ledger file** | The Run's `decision-ledger.json`: an indented, versioned JSON snapshot containing the next monotonic finding number, current entries in ID order, applied decision batches, fix-attempt records, and the Invariant register when it holds a record. Every replacement goes through `AtomicFile`; malformed content, an unsupported schema version, a violated invariant, or exhausted write retries fail the tool call rather than recreating empty state or continuing a Worker without its decisions. Unlike Telemetry, this state is not best effort. |
| **Finding** | One gap a Critic reports, or the Orchestrator raises: `severity`, `where` and `what`. A Critic's finding also names its Invariant, the axes along which the places that must satisfy it vary, how it searched for them, whether its list of them is `complete`, and `invariantOf`, the earlier finding whose Invariant it violates again. The server keeps those as they came, a blank value as none, and refuses no critique for them; an `invariantOf` naming an ID the Run never had is dropped and the Flow log says so. |
| **Invariant** | The property a Finding says is violated, stated without the place and at the level of the plan's rule, so that it covers every place that must satisfy it. Its axes — layers such as requirement, task rule, matrix, gate, docs, deciders, readers and tests, and forms such as call kinds, value channels and object kinds — are what a fix walks and what the next Critic checks. A Fix attempt's prompt carries each finding's Invariant and axes under it. See `docs/adr/0051`. |
| **Invariant register** | The part of the Canonical decision ledger that outlives closure: for every closed Finding that named an Invariant, its ID, phase, severity, place, Invariant, axes and `invariantOf`, how it closed, and the fixer's claim — the evidence of an `addressedByRevision` or `hostVerified` closure, or the part of the Builder's report that names the finding, for the attempt whose gate closed it, cut to 600 characters; a claim that looks like a secret is withheld and marked so. Every review Critic, plan and code alike, is shown it after the projection as `# Invariants of earlier rounds`, together with the displayed entries' Invariants, grouped by the first finding of each `invariantOf` chain and bounded at 12,000 characters, and checks each claim rather than taking it. Absent from the file while empty. See `docs/adr/0051`. |
| **Finding identity** | A monotonic Run-local identifier assigned when a Critic first raises a finding, or when the Orchestrator applies a Raise, from one sequence spanning plan review and code review. Later dispositions and any reopening reference that identifier. Closing a finding never releases its number, and a defect that reappears after closure is a new finding with a new identity. |
| **Disposition** | The current state of an entry in the Canonical decision ledger: `unresolved` when newly raised, `deferred` when accepted as valid but deliberately left undone in this Run, or `rejected` when judged incorrect or inapplicable. Only the Orchestrator submits a disposition change, whoever its Decision-maker; the server validates and persists that transition, while a Critic or Builder never changes it. Deferred and rejected entries preserve the original finding verbatim, the Decision-maker, and the reason. `Addressed` is a Flow-log closure event, not a persisted disposition: a successful plan revision or fully verified code fix removes the entry under the Orchestrator's prior instruction; short success retains it pending full verification. A plan revision's closure of a blocker or major carries the Orchestrator's evidence — what it read, the class it listed, the failing test cell — which the Flow log keeps with the closure. |
| **Decision-maker** | Who a ledger decision — disposition, closure, reopening answer — or a Decision record is attributed to: `user` when the user's answer settled that particular finding, question or how it was addressed, `orchestrator` otherwise. Approving the plan as a whole settles no finding, so it makes none of them the user's; a closure the gate makes after a fix attempt is nobody's choice and carries `orchestrator`. An attribution the Orchestrator asserts for every action alike and the server never verifies; not an authority, since the Orchestrator is the only submitter whichever value it carries. |
| **Finding reopening** | A Critic's proposal to return to a displayed deferred or rejected finding by identity because current material or evidence changed the decision. The Critic cannot change state. Declining records the audit and leaves the ledger untouched. Accepting makes the same entry unresolved; during code review, a plan-origin entry keeps `origin=plan_review` and changes `activePhase` to `code_review`. A defect that reappears after closure is a new finding instead. A rejected disposition otherwise remains effective through the end of the Run. |
| **Decision batch** | One non-empty logical set of disposition, closure and reopening decisions and Raises the Orchestrator submits, named by `decisionBatchId`. Its validated decision DTOs are canonically serialized in finding-ID order, and the ledger keeps only the SHA-256 digest of those bytes with the batch result. Repeating the same key and the same digest is a no-op with the saved result; reusing the key with a different digest is a conflict that returns the saved result. A new key is only for a new legal delta. |
| **Decision record** | One settled decision of a Run, appended to `decisions.json` by `forge.decision.record` the moment an answer settles something — in the interview, between review rounds, at approval, mid-build or in code review — instead of a free-text Run-log note: the Orchestrator phase it was made in, the question as asked, the options offered with the recommended one marked, the answer as given, its Decision-maker, the IDs it bears on, and the earlier record it supersedes. Never removed; a changed answer supersedes, and the superseded record stays. A record with the same content as one already there is that record. Like `approved`, the Orchestrator's assertion: the server never knows whether a question was asked. Every review Critic, plan and code alike, is shown the current records as a bounded **Decisions section** after its ledger projection — one line each, the oldest left out beyond 12,000 characters and named — for which a decision is not a gap; `forge.status` shows the count and the last five, the resume card the count and the file, and the Builder none. See `docs/adr/0045`. |
| **Decision identity** | A Decision record's Run-local identifier, `D-0001` on, assigned by the server in record order from a sequence of its own, apart from Finding identity. A superseding record names it, and a replay of the same content answers with it. |
| **Fix attempt** | One logical Builder execution named by `fixAttemptId` and bound to one exact sorted non-empty `fixFindingIds` set outside the decision payload. The binding includes an immutable gate mode (full or targeted). Retained or cut-short work retries the same attempt, set and mode; another set or mode under the key is invalid. A terminal successful attempt returns its saved result without another Builder or gate. |
| **Raise** | A finding the Orchestrator puts into the Canonical decision ledger itself during code review, typically a place a Builder reported as `left:`. It travels in a decisions-only `forge.review.fix` batch as `severity`, `where`, `what`, Decision-maker and reason, takes the next Finding identity, and becomes an unresolved `code_review` entry marked `raised`, which the next Critic assesses like any other. The call returns the new IDs as `raisedFindingIds` for a later Fix attempt. Plan review refuses Raises, and a batch with Raises carries no Fix attempt. See `docs/adr/0025`. |
| **Fix note** | The Orchestrator's `note` on a Fix attempt, framing its batch by the rules its findings break: for each rule, the rule itself with the requirement it comes from, its owner and its axes; for the batch, what must not change and when to stop. An attempt's first call carries the whole note, a retry only what changed since the last call. The Builder reads it after the verbatim findings under "From the orchestrator" and before the user's Run instructions; the Flow log records it verbatim. Narrative, not ledger state, so a retried attempt carries whatever note the retry sends; the server requires none and checks none of it. |
| **Plan file** | The run's plan as it currently stands, `PLAN.md`: written whole by `forge.plan.write` or revised by targets through `forge.plan.patch`, which persists the complete result before the round. Also rewritten by a round handed a draft of its own, and by `forge.plan.confirm` with the text the user approved. The plan text is UTF-8 read with a leading byte-order mark dropped; a patch removes any byte-order marks at the result's start before checking, writing, hashing or measuring it, while a whole-draft write stores its draft as given. Blank means whitespace after setting aside leading byte-order marks. Approval is `approved` in run state; either writer or a later round withdraws it and forgets the Builder session. A whole-draft write or a round handed a draft resets build progress to zero; a patch keeps the built tasks before the first task whose text it changes. |
| **Edit target** | One piece of plan text a Plan patch may replace, delete or insert beside. Outside Approach, heading lines begin at column 0 with one to six `#` followed by space or tab; each starts a target. With exactly one build-recognized `## Approach`, the build's patterns run on CRLF-normalized text with positions mapped back: its heading ends at the first task or section end, each task starts at the line holding the match's first digit and runs to the next task or section end, and the section-end line starts a heading target even if it is not an ordinary heading. Other heading lines inside the section are not targets; without exactly one Approach, only heading lines divide the plan. A nonempty preamble precedes the first target. Fences are ignored: a heading cuts a code block outside Approach; a build-recognized task or section end cuts it inside. Targets never overlap, include each line's ending break, and concatenate to the exact plan text. IDs `s0`, `s1`, … and target hashes name only that snapshot. |
| **Plan outline** | A plan snapshot's Edit targets in document order — ID, kind (`preamble`, `heading`, `task`), one-line label, hash and size — with the whole plan's hash and size. Hashes are the first 16 lowercase hexadecimal characters of SHA-256 over UTF-8 plan or target text; sizes are UTF-16 code units, called characters. Labels are the heading/task first line or preamble's first nonblank line, trimmed and cut to 80 characters, or `(blank)` for a whitespace-only preamble. Carries no plan text beyond labels. `forge.plan.outline` refuses a missing or blank plan; `forge.plan.write` returns a null outline for blank plan text. |
| **Plan patch** | A partial revision against one exact Plan outline hash and target IDs/hashes: `replace`, `delete`, `insertBefore` or `insertAfter`, applied independently of edit order. Refuses conflicting mutations on one target, insertions at one position (after X and before its successor coincide), and an insertion anchored on a deleted target. Written text uses CRLF only when the base has breaks and all are CRLF, otherwise LF; it ends with a break, keeps supplied trailing blank lines, and a separator is added between pieces when needed. Untouched targets keep their exact text. The whole result must be nonblank, changed, and still accepted by the build's task parser if the base was accepted, including task-number parsing. Refusals change no protected file. Approval is withdrawn before replacing the complete plan; failures before replacement leave it unchanged, while a failure of the patch Flow entry reports that it was patched and gives the new hash. There is no lock or multi-file transaction, and the hash check is not atomic with replacement. A replay conflicts as stale: read the outline and redo the edits, never infer success from equal text. The next Critic receives the complete plan through the usual review route. |
| **Revision** | The Orchestrator's answer to a plan-review round: what changed in the draft plus any typed identity-keyed decisions now justified. Review calls may carry a partial decision subset; final approved confirmation enforces that no unresolved active plan entries remain. Narrative goes only to the Flow log. |
| **Granted round** | The user's answer to a reached cap: `userGrantedRound` on the next round call raises that cap by exactly one and runs the round past it. Spent by the call that carries it, so the round after needs a fresh answer, and counted in the run state beside the cap it moved — `grantedReviewRounds` next to `reviewRoundCap`, `grantedCodeReviewRounds` next to `codeReviewRoundCap`. |
| **Automatic round** | A round at the cap that runs without a Granted round because the review is converging: the last round's finding count — its new findings plus the unresolved ones its Critic assessed as still present — is below the round before it, none of them is a `blocker`, and, in plan review, fewer than half of the last round's new findings are Fallout. It leaves the cap where it was, and runs for as long as the count keeps falling — never more rounds than the count at the cap, since it cannot fall below zero — and it is counted in the run state as `autoContinuedReviewRounds` or `autoContinuedCodeReviewRounds`, beside per-round `reviewTallies` / `codeReviewTallies`. The Flow log and the round's result (`continuedAutomatically`) carry the two counts. See `docs/adr/0041`. |
| **Fallout** | A new plan-review finding that the plan's own revisions caused: labelled `own` (something an earlier revision wrote is wrong) or `sibling` (a place that answers the same question was left behind); `sameClass` and `older` are not Fallout. From the third round on, `forge.plan.review` takes `fallout`, the previous round's new finding IDs each under exactly one of the four labels, and refuses a call whose labels do not cover them exactly. The server writes the Flow-log line and keeps the round's `new` and `fallout` counts in its `reviewTallies` entry; the IDs still owed are `falloutOwed` in the state file, which `forge.status` leaves out. Half a round or more as Fallout ends Automatic rounds and is the signal to sweep the rules the revisions touched instead of running another round. See `docs/adr/0049`. |
| **Run log** | The operational record of a run, `forge.log`: JSONL, append-only, written by the server for every tool call, vendor process and vendor event, and by the orchestrator through `forge.log.append`. Distinct from the flow log, which is the user-facing timeline of results; this one exists for the runs that produced none. |
| **Worker usage** | Per-attempt Worker telemetry where normalized `inputTokens` is the complete input processed, cache read and cache creation are optional subsets of that total, and output includes its optional reasoning subset; provider prices, durations, context windows and per-model breakdowns remain outside it. |
| **Plan Forge-owned attempt data** | The Act, Vendor, role, requested model, effort and Fast choice, session mode, turn identity, attempt number and outcome that Plan Forge knows directly, plus the prompt size, local process start time, process wall duration and union of observed tool-call intervals it measures itself. `duration` is wall time minus tool time, after both are truncated to seconds; it equals wall time when tool boundaries are absent or incomplete. Background work after a tool result is not subtracted. |
| **Requested selection** | The `model`, optional `effort` and Fast choice Plan Forge was asked to use, recorded as asked and without guessing which model the provider ultimately served. |
| **Fast tier** | A Vendor's faster speed for a model, sold at a higher usage price, and chosen for a Worker as a third axis beside model and effort. Off unless asked for, and off is then asked for explicitly rather than left to the Vendor's own configuration. A Fast request is honoured only where it is confirmed — by the Catalogue for every Vendor, and for claude again by the session's own start — and refused otherwise. |
| **Served speed** | The speed a Vendor reports it actually ran a Worker at, as distinct from the Requested selection. Only claude reports it, at session start and at the end: a Fast request it will not serve at the start fails the attempt before any API call, while a fall back to standard speed during the turn (`cooldown`) is a warning on a result that still counts. |
| **Telemetry file** | The Run's indented `telemetry.json`, created by its first record and never left empty, containing a JSON array with one human-readable Usage record per Vendor attempt, one Orchestrator usage record per Orchestrator phase and one Advisor usage record per accepted Evidence map call, including cancellation or failure, told apart by their `event` (`vendor.usage`, `orchestrator.usage`, `advisor.usage`), and no prompt or output content, separate from the operational Run log and the user-facing Flow log. Adding a record is a process-local, per-file serialized read–insert–rewrite that atomically replaces the file and places the record after the last element whose `at`, its opening, is no later — an Advisor usage record opens at its call start; an Orchestrator usage record closes after the Worker records of the attempts that began inside its phase and stands before them; an existing file that is not the expected JSON array is preserved unchanged and the telemetry write fails harmlessly. Like the rest of mutable Run state, it does not promise safe concurrent mutation of the same Run by separate Plan Forge processes. |
| **Usage record** | The terminal `vendor.usage` Telemetry-file entry for one Worker attempt, with JSON numbers for counts and token counters, strings for identity, classification and duration, an array for malformed field names, absent optional values omitted except for the explicitly nullable `toolDuration`, and no failure or output detail. Its display order is stable—time and event, Act position, requested Worker, session and turn identity, outcome and owned measurements, then token counters, the Session running total where one was kept, and malformed paths—but consumers treat it as an unordered JSON object. |
| **Usage timestamp** | The `at` time when Plan Forge starts the attempt's vendor process and sends its prompt, written in the machine's local time as `yyyy-MM-dd HH:mm:ss`, without offset or fraction. Records are appended on completion, so file order is completion order. Duration measurements use a monotonic clock and exclude post-exit output processing. |
| **Reported Worker usage** | The provider counters carried by a Vendor's terminal event for one attempt; intermediate message counters are not accumulated, and an attempt without terminal usage has no inferred counters. Where the Vendor reports a Session running total instead, a resumed attempt's counters are what that total grew by since the session's latest recorded report. |
| **Session running total** | What a Vendor reports for its whole persistent session rather than for one attempt: every codex token counter, which `exec resume` restores from the thread's rollout, and claude's `modelUsage`, summed over its models, and `total_cost_usd`, which `--resume` restores from the transcript's `cost-state`. Claude's totals count its subagents and context compaction, which the result line's `usage` leaves out, so its tokens are read from there. A resumed attempt keeps the reported total as `sessionTotal` beside its own growth, so the attempt after it counts from that report even when a growth was omitted. What a codex attempt spent before ending without a terminal event stays in the thread's total and lands in the next reported growth. |
| **Worker usage boundary** | Worker usage covers Critic, Builder, and Scout attempts only. The host Orchestrator's work is recorded separately as Orchestrator usage, and catalogue Probes are outside both. |
| **Host session** | The host's own conversation the Orchestrator runs in, as the host identifies it: a Claude Code session, a Codex thread. A host that names neither gives Plan Forge none. Distinct from a Vendor session, which belongs to a Worker. |
| **Orchestrator phase** | A named stretch of the Orchestrator's work in a Run: `interview`, `planning`, `plan_revision`, `approval`, `build`, `code_review` or `wrap_up`. Starting the Run opens `interview`; the Orchestrator declares each later phase when it begins it, a phase lasts until the next declaration, and the declaration `done` closes the last one without opening another. A name may recur, and each occurrence is a phase of its own; Plan Forge adds the round or task the Run is at. A phase never closed — a Run abandoned before `done` — has no record. |
| **Orchestrator time** | The wall time of an Orchestrator phase less the time spent inside Plan Forge's own tool calls, counted once where calls overlap: while a call runs, the server is working and the Orchestrator is waiting for it. It includes the Orchestrator's own model time, its other tools and every wait for the user, which Plan Forge cannot tell apart. |
| **Orchestrator usage** | The record of one Orchestrator phase: its Orchestrator time, and the tokens the Host session spent during it as the Usage source reports them. A host with no identifiable Host session has time only. |
| **Usage source** | Where Orchestrator tokens are read from, chosen once per server process, when its Host session first becomes known: the host's telemetry export when the user's own host settings send it to Plan Forge, otherwise the Host session's transcript. Both are best effort; a source that cannot answer leaves the tokens absent and never changes a tool call's outcome. |
| **Telemetry collector** | The one background Plan Forge process that receives the hosts' telemetry export for every Host session on the machine and keeps it in memory, for the Plan Forge servers of those sessions to ask about their own. Started on demand by a server, and started again by a server that finds it gone. Alive while any server that registered with it is: it ends a short grace period after the last of them exits, however long their tool calls run, or earlier, together with the host it was started under. What it held is lost with it, and a phase it can no longer cover is marked incomplete rather than undercounted. |
| **Worker prompt size** | The UTF-8 byte count of every Plan Forge-authored text fragment supplied for one attempt, counted once across all transport channels and excluding Vendor-owned instructions, resumed history and other context Plan Forge cannot observe. |
| **Worker duration** | Plan Forge's monotonic wall-clock wait from immediately before a Vendor-process launch through acceptance or rejection of its structured result, truncated to whole seconds and recorded as `duration` with at least two unbounded total-hour digits followed by minutes and seconds (`hh:mm:ss`). |
| **Malformed usage field** | A provider-reported counter that is not an explicit non-negative JSON integer, is inconsistent with related counters, or as a Session running total falls below the session's previous report: only that normalized value and its dependants are omitted, independent counters remain, and its safe source JSON path is named in `malformedUsageFields` without carrying the value. |
| **Worker attempt outcome** | Plan Forge's terminal judgment of one Vendor attempt, in precedence order: `cancelled` before a result was accepted, `failed` when the Vendor, API or process failed, `invalid_output` when a non-failing process returned no usable structured result, and `succeeded` when its structured result was accepted; usage quality never changes it. |
| **Worker turn identity** | One Plan Forge-generated random `turnId` names a call to a Worker's session and its one-based `attempt` number distinguishes every Vendor-process launch Plan Forge makes within that turn; retries internal to one Vendor process remain inside that attempt's terminal cumulative usage. |
| **Vendor session mode** | Whether a particular Vendor process was launched without (`fresh`) or with (`resumed`) a resume token, recorded as `sessionMode` independently of the session identity the process eventually reports. |
| **Vendor session identity** | The optional `sessionId` of a Vendor's persistent conversation, shared by separate resumed Worker turns and distinct from Plan Forge's per-turn identity: the current process's reported value is authoritative, with the supplied resume token as fallback when a resumed attempt ends before reporting one. |
| **Telemetry act** | The delegated Act attributed to a Usage record: `plan_review`, `build`, `code_review`, `review_fix`, or `scout`, distinguishing work that shares a role. |
| **Telemetry position** | The human-readable place of a Worker turn inside its Act: `round` for `plan_review` and `code_review`, the code-review `round` being fixed for `review_fix`, and `taskNumber` plus `taskCount` for `build`; a retried build keeps the same task number, while the task text remains only in `PLAN.md`. |
| **Interview mode** | The orchestrator's choice between an interview without documentation and one that maintains the domain model as it goes. |
| **Catalogue** | The models and effort levels a vendor advertises, and which of them it offers with a Fast tier and at what price hint, served to the interview by `forge.models`. **Live** when the vendor reported the list itself (codex); **resolved** when the list of aliases is one this repo remembers but the vendor turned each alias into the concrete model it stands for at probe time (claude). An alias the vendor did not resolve is not offered. Advisory for validation either way: the vendor CLI decides. |
| **Scout selection** | The Run's initially unset choice for the Scout Vendor, model, effort, and Fast tier, the last confirmed when chosen. On the first need the Orchestrator either stores an exact selection or records that the Run continues without Scout; the latter suppresses later automatic questions, while an explicit later choice may enable Scout. Calls reuse the stored selection exactly, and an explicit reselection after failure starts a fresh Scout session rather than silently falling back. The selection carries the **session anchor**, the one Scout session a `continue` resumes: a `fresh` call clears it and installs its own session; a `continue` runs alone, refused while a Scout job runs; and Scout turns that overlap — fresh jobs side by side, or a direct call beside a job — leave no anchor, because none of their sessions is the one investigation a `continue` would mean. A turn that ends after the user chose another Scout leaves the new choice's anchor alone. See `docs/adr/0044`. |
| **Probe** | A vendor's readiness check, which for a live-catalogue vendor also fetches the catalogue. Readiness means **able to do the work**, not merely installed and signed in: a vendor that could not execute a command is unavailable, however healthy its sign-in. Started for every vendor in the background by `forge.begin`; a vendor whose probe failed is unavailable and the interview does not offer it. |
| **Shell-less answer** | A Worker's answer from a turn in which it tried commands and the vendor started none. Not used: the act fails like any vendor failure, the Scout report and the ledger stay as they were, a review round is not spent, and the error and the Flow log quote the vendor. A turn that started one command, or tried none, is not shell-less. |
| **Requirement** | A numbered statement under the plan's `## Requirements` heading of what must be true when the run is done — `R1`…`Rn`, with the run's exclusions beside them. The interview's output, so it names no file and no symbol; every task cites the requirements it serves. |
| **Gate** | The check that would catch a requirement's violation: a command, or a condition someone can observe. A task's own gate ends the task; a `## Gates` entry — `G1`…`Gn` — belongs to no single task. **Executable** when code immediately follows the label: the server runs task gates after `forge.build.next`, and complete-plan gates after `forge.review.fix` in the default full mode. A targeted fix uses its Fix gate instead. Otherwise a **condition**, left to the Builder's word for tasks and to the Orchestrator for complete-plan checks. See docs/adr/0015 and docs/adr/0030. |
| **Fix gate** | One explicitly stated executable `**Fix gate:**` under `## Gates`, used by the server after each fix attempt that opts into targeted verification. It supplies short feedback and does not establish complete-plan verification. No union of task gates is inferred. |
| **Pending full verification** | A finding whose targeted fix passed its Fix gate but has not been closed by full host verification. The finding and its covering attempt remain visible in persistent ledger state, status, Critic input and the Flow log. A Critic assessment alone does not verify it; a full-check failure does not automatically choose findings to repair. |
| **Full-gate timing** | The interview asks once, before the first plan: full verification after each Builder fix turn (the default), or only at the end of the run (`gate=targeted`, `fullGate=final`). Targeted verification also supports an explicitly requested `beforeNextRound`, after the current round's fixes and before its next Critic. Both targeted full-check timings run all complete-plan gates on the Orchestrator's host. An omitted timing on later confirmation preserves the choice; an explicit change does not verify pending findings. Successful `hostVerified` decisions record evidence; a failure leaves fixes unverified and the Orchestrator chooses the next action. Final code approval while verification is pending is not full verification. See docs/adr/0030. |
| **Gate run** | The server's own execution of a gate command: `passed`, `failed` or `timeout` when it ran, `not_executable` when the gate is a condition, `not_run` when the builder was `blocked` after a verification that `failed`, when its turn ended with a Killed background task, or when no PowerShell was found. A `blocked` turn whose verification was `unavailable` **is** run: the builder is saying it did the work and could not prove it, and the host holds the environment that can. Travels as `build.result.gate` / `fix.gate`, as a `Gate:` line in the flow log, and as `gate.start` / `gate.finished` in the run log. |
| **Gate environment** | The variables a run's gate commands need — a connection string, a path to a sibling checkout — given to `forge.plan.confirm` as `gateEnvironment` and kept in the run state. Logged by name only. |
| **Builder roots** | Absolute paths outside the workspace, accepted by `forge.plan.confirm` as `builderRoots` and retained in the Run for compatibility. They do not control Builder access: Codex Builders run without a sandbox, and the other Vendors ignore them. |
| **Worker tools** | The MCP servers a Worker may call without being asked, named by server-name patterns in `workerTools` on `forge.begin` — `roslyn-*` when omitted, nothing when empty — and kept in the run state for every Worker role. Each launch looks the servers up in the vendor's own list and grants the ones that match: a claude worker by exact `--allowedTools mcp__<server>`, a codex worker by `default_tools_approval_mode` per server, under `plugins.<plugin>@<marketplace>` for a server a plugin declares. A pattern names a server by its key, so besides a whole name it matches the `<server>` of a plugin server claude lists as `plugin:<plugin>:<server>`, never its plugin. The run log records each launch's patterns and the servers they matched as `worker.tools`. Not Claude's rule syntax: `mcp__roslyn-*` is refused there, see below. |
| **Run instructions** | The user's own free text for a Run's Workers, one for the Critic and one for the Builder, recorded by `forge.instructions.set` and kept in the run state. They travel in the **act prompt** — the user turn — never in the **role instructions**, which are the role contract loaded from `prompts/<vendor>/<role>.md` and reach a vendor by its own channel (`developer_instructions` for codex, `--append-system-prompt` for claude). A Critic is fresh every round and is handed its text every round; a Builder is handed its text only by a call that starts a session, so a change made mid-session reaches the next session and not the running one. Every new plan task and code-review round starts such a session, and so do vendor switches, reopened plans, and re-approval after the Builder Brief changes. A code-review Critic is additionally shown the Builder's text as data, framed as context for judging the diff. Nothing but the secret guard checks them, and nothing checks that the text is the user's own rather than the orchestrator's — see `docs/adr/0019`. |
| **Build status** | What the builder says it **did** with a task: `done` or `blocked`, and where a gate ran the server writes the exit code over it in either direction — `gate_failed` when the command did not exit 0, `done` when it did, which is how a `blocked` turn the host proved still counts. The server also writes `background_killed` when the turn ended with a Killed background task, and then runs no gate. Only `done` is progress. A `blocked`, `gate_failed` or `background_killed` task remains the next task, so the run retries it rather than stepping over it — the distinction issue #58 proved was missing, when a machine that could run no command still walked the plan to its end. |
| **Killed background task** | A command a Worker started in the background that was still running when its turn ended, so the Vendor killed it with the process. The Worker never saw its result. Detected on claude only, where the stream reports it; named in the build result for a Builder, logged for a Critic. Not a "background job": a **Job** is a delegated act. |
| **Verification** | The builder's own account of whether it **proved** the work, separate from whether it did the work: `passed`, `failed`, or `unavailable`, always with evidence. Self-reported. `unavailable` also covers a Builder that ran no checks because the executable gate belongs to the server, with that reason in the evidence. The verdict where the gate is a condition; context where the gate is a command, because the gate run answers that. |
| **Capability profile** | What a given host can actually do. Two profiles were designed, `canvas` and `text`; only `text` is built — see below. |
| **Resume card** | What `planforge.exe --resume-card` prints for the Claude Code and Codex `SessionStart` hooks after a compaction: the Run of this Host session, where it stands, how many Decision records it holds and where, the next calls with their exact argument shapes, and the skill reference to read again. IDs, counts and shapes only. See below and `docs/adr/0040`. |
| **Skill core** | `skills/forge/SKILL.md`: the participants, the phases, the tools with their argument shapes, the rules that hold throughout. Everything else is a phase's **reference** under `skills/forge/references/`, read when the phase begins. The core stays under what the host re-injects after a compaction. |

## Tier asymmetry is a design-wide constraint

The roles are **not interchangeable in model strength**, and the old code did not express this — its
`ForgeRole.Reviewer` and `ForgeRole.Builder` chose models the same way. Here the tier is part of the
role's definition:

- **Orchestrator (strong)** holds the interview context, which is why it, and not a worker, revises
  the plan. A fresh reviser process does not have that context, so the revision cannot be delegated
  to a vendor.
- **Builder (cheap)** works against an already-hardened plan, where the decisions were made for it.
  The dependency runs both ways: the cheaper the builder, the deeper the plan — the orchestrator
  chooses the builder before drafting and calibrates task granularity to its model and effort.
- **Critic** judges; the user picks the tier, defaulting nearer the strong end.

The direct consequence for the tool surface: **neither review loop can live inside a single call**,
because a turn by the host LLM is mandatory between rounds. For plan review the orchestrator revises
the draft; for code review it filters the critique against the approved plan before the builder sees
it, because only it knows what the plan deliberately left out. The code-review loop used to be
sealed inside one call on the belief that the orchestrator was not needed there; running the flow on
this repository disproved it — see `docs/adr/0005`. A finding the orchestrator defers or rejects is
recorded in the Canonical decision ledger with its reason, so the next round's fresh critic reads it
as settled.

## The critic is fresh each round, but reads the canonical decision ledger

A persistent critic defends its own earlier assessment and normalises what it has already read,
degrading the exact capability it was hired for. A naively fresh critic oscillates: round 3 reopens
what round 1 accepted. But that oscillation comes from missing **information**, not missing memory —
so the fresh process receives one phase projection of the Canonical decision ledger. Plan review
gets entries currently active in `plan_review`. Code review gets settled plan decisions plus entries
active in `code_review`, including accepted cross-phase reopenings. Closed entries, plan findings
still unresolved in their original phase, and the repeated history of critiques, revisions and fixes
remain outside that projection; what a closed finding keeps is its Invariant and the fixer's claim,
in the Invariant register, which follows the projection. A Critic assesses each displayed unresolved ID once and may propose
reopening a displayed settled ID with evidence, but only the Orchestrator changes state. Prompt
growth follows current phase-relevant findings, not the number of rounds.

Interface consequence: `CanResume` is needed only by the Builder; the Critic is always stateless.

## The interview's decisions lived in the Orchestrator's context alone

The ledger carries what was settled about findings; nothing carried what was settled outside one.
Measured on the concurrency-hunter runs of 14–20 October 2026, the Orchestrator's host session was
compacted 3 (A10), 1 (B3), 5 (B3b) and 6 (B3c) times, mostly during plan review, and after each
compaction the interview survived as a 10–23 KB summary the Orchestrator wrote for itself: the B3c
summary kept one decision in one line out of 15 domain questions. What was written down went to
`forge.log` as free-text notes — run B3 has 13 `Interview D-n (user):` notes — which no Worker reads.
A run asks 24–40 questions, 14–17 of them domain decisions, and a Critic learned of a decision only
if the Orchestrator had written it into the plan; one that had not was re-raised and rejected
`by: user` a round late. Decision records put those answers in the Run, where `forge.status`, the
resume card and every review Critic read them back — see `docs/adr/0045`.

## The plan states its own intent, and the critic judges that too

The plan used to be the only statement of what a run was for, and the critic's bar was completeness
for implementation: `approve` when nothing is left for the implementer to guess at. A plan that is
detailed, internally consistent and aimed at the wrong thing clears that bar without a finding,
because nothing independent of it says what right would have been. The interview knew — and it lived
only in the orchestrator's context, where no worker and no later session can read it.

So the plan carries `## Requirements` above `## Approach`: numbered `R1`…`Rn`, what must become true
and what must not change, plus what the run deliberately excludes. Tasks cite the requirements they
serve, and `prompts/requirements-contract.md` — appended for plan review exactly as
`scope-contract.md` is for code review — puts the requirements themselves under review and asks for
coverage in both directions.

Handing a critic a fixed yardstick is the move `scope-contract.md` already makes, and there it is
meant to narrow: work the plan does not ask for is at most `minor`. Repeating that at plan stage
would turn the critic into a conformance checker and make a wrong requirement unfalsifiable, which
is worse than having no requirements at all. Hence the asymmetry — the requirements are open to
attack and only the **exclusions** are settled. Without that second half a critic invents
requirements the user ruled out in an interview it never saw, and every round is spent defending it.

The cost is that the plan-review loop is no longer purely between orchestrator and critic. A finding
that a requirement is missing can be a question only the user can answer, so it goes back to them
mid-loop rather than waiting for approval, where its answer would invalidate every round since.

Gates apply the same idea to verification. A task already ended with how it is verified; the `Gate`
label only makes that mandatory and findable, which is what lets the critic treat its absence as a
finding and the server run the exact command after the builder's turn. What had no home at all is a
check no single task owns — a test suite, a warnings-clean build, an invariant spanning the change —
so those go under `## Gates`; the server runs the executable ones after a full-mode fix, or only
the Fix gate after a targeted fix. The orchestrator runs all full G checks after the last task and
verifies targeted fixes at saved beforeNextRound or final timing, conditions included. Not the builder, whose turns are task-specific; and not
the critic, because a build writes `bin/` and `obj/` into the tree it is judging, and a vendor's
read-only guarantee, where it gives one, covers the agent's own edits, not the side effects of a
command it ran. A gate
that is a condition rather than a command is still a self-reported claim, so for those the rule below
stands: anything but `passed` is the orchestrator's to run itself.

Only the server runs an executable task gate, including on retries. The Builder receives the
parser's classification in its act prompt and may run separate targeted checks when useful, but
does not repeat the gate or a complete equivalent. A condition remains the Builder's to check.
A Builder that ran no checks because the gate is reserved for the server reports verification
`unavailable` with that reason; a completed implementation is still `done`. The task gate and
plan-wide gate schedules stay distinct. See `docs/adr/0027`.

None of this adds an artifact. The requirements live in the plan file, `PlanTasks` walks only what
is under `## Approach`, and both review acts already send the whole plan — `PlanReview` the draft,
`CodeReview` the approved copy — so requirements and gates reach both critics with no plumbing.

## A cap is a checkpoint, not a stop

Review rounds and code-review rounds are both capped for the same reason: an orchestrator left to
its own budget will keep spending it, since nothing about a `revise` verdict forces the loop to end.
The cap exists to catch that runaway, not to overrule a user who has read the critique and decided
the remaining risk is one they will accept — so it is the orchestrator that cannot raise it on its
own. Only the user's answer can: `userGrantedRound: true` on the next round call raises the cap by
exactly one and lets that round run past it. It is spent by the very round it unlocks, which is what
makes "ask every time" fall out of the mechanism instead of depending on the skill to remember —
there is no way to carry a grant forward, only to ask again.

A loop that is converging is not the runaway the cap is for, so the server lets it through without
the question: an Automatic round runs when the last round found fewer findings than the one before
it and left no blocker, and, in plan review, fewer than half of the last round's new findings were
Fallout, since a loop whose findings the revisions keep producing is not converging however its
count moves. It is decided by the server from what the rounds left and the labels the call carries,
which are the orchestrator's assertion as `approved` is, and it does not raise the cap, so the next round is decided again. The count bounds it
on its own: it has to fall every round and cannot fall below zero. The question was costing 20–40 minutes a round in the concurrency-hunter
runs of 12–20 October 2026, and once a whole night, while the findings were still falling.

## The codex CLI's exit code answers for the API, not for the work

Measured against `codex` 0.147.0 on 2026-09-03. The surface is `codex exec`; the App Server client
it replaced is `docs/adr/0012`.

**The exit code is silent about the work.** `codex exec` exits `0` when a command it ran was denied
by the sandbox, and `0` when that command exited non-zero. Only failures the API rejects — a bad
schema, an unknown model, an unknown effort — reach the exit code, as `1`; a malformed flag is `2`.
So the outcome of the work is read from the `--json` stream instead, where each command arrives as a
paired `item.started` and `item.completed` carrying the resolved command line, `exit_code`,
`aggregated_output` and a `status` of `completed` or `failed`. A vendor that trusted the exit code
would have called the total failure of issue #58 a clean run.

**`--output-schema` is enforced by the API, not by the prompt.** The CLI refuses a schema without
`additionalProperties: false` before a token is spent, and a prompt written to break the format came
back as a valid object anyway. That is what let the codex vendor drop the schema-in-the-prompt
retry loop.

**`developer_instructions` adds; `instructions` replaces.** Both are accepted configuration keys and
they look interchangeable. `-c developer_instructions=` prepends a block to the developer message
and leaves codex's own prompt intact, costing the tokens of the text itself. `-c instructions=`
goes to the API's top-level field and *displaces* codex's built-in instructions — measured as a drop
of roughly 2,700 input tokens on an otherwise identical run, with the text never appearing in
`codex debug prompt-input`. Only the first is the counterpart of the App Server's
`developerInstructions`.

Two properties carried over unchanged from the App Server, because `codex exec` is itself an App
Server client. `effort` is *not* validated locally: a bad level is accepted, printed in the header,
and rejected upstream. And resume needs the thread to have a recorded rollout, which exists only
after a turn has completed, so a Builder's resume token is worth storing only once its first task is
done; `--ephemeral` writes no rollout at all and makes resume impossible.

**Codex picks its own shell, and no key changes it.** On Windows it wants PowerShell, resolved from
its own process environment before `shell_environment_policy` is applied — measured, by handing it a
sanitised `PATH` through that key and watching it resolve the old value anyway. The only lever is
the environment of the process this server starts, which is what `docs/adr/0013` uses.

**An `error` in the stream is not a failure; `turn.failed` is.** Measured against `codex` 0.154.0
on 2026-09-17, with two runs and a reading of `codex-rs/exec/src/event_processor_with_jsonl_output.rs`
at tag `rust-v0.154.0`. The stream carries a failure-like message in two shapes, and neither one
ends the run by itself:

- `{"type":"item.completed","item":{"type":"error","message":…}}` is how exec reports the server's
  `Warning`, `ConfigWarning`, `DeprecationNotice` and `ModelRerouted` notices. A healthy run
  (exit `0`, result written, right answer) carried the skill-budget notice ("Skill descriptions were
  shortened to fit the skills context budget…") in this shape; a run with an unknown model also
  carried "Model metadata for `…` not found. Defaulting to fallback metadata" before it failed.
- `{"type":"error","message":…}` is the server's `Error` notification. Exec drops that
  notification's `will_retry` flag, so a retried stream error looks the same as a fatal one.
- A failed turn always ends with `{"type":"turn.failed","error":{"message":…}}`. The message is the
  turn's own error, or the last `error` line's message when the turn has none, so the reason is
  never lost. The unknown-model run (exit `1`) printed its 400 `invalid_request_error` as an `error`
  and then again as `turn.failed`. Every usage limit in this machine's `forge.log` files appears
  twice, about 10 ms apart, which is the same pair.

So the codex session treats only `turn.failed` as a failure: it becomes `vendor.failed` (level
`error`) and the reason a run with no result gives. Both `error` shapes are logged as
`vendor.warning` at level `warn`, so a notice can no longer replace the real cause in that message.
No `forge.log` on this machine held the skill-budget notice as `vendor.failed`: before this change,
an `error` item was not logged at all.

**`codex doctor --json` exits `1` for a check a worker never touches.** Measured against `codex`
0.154.0 on 2026-09-17: the report said `overallStatus: fail` and the process exited `1` because
`sandbox.helpers` alone was `fail` ("elevated Windows sandbox provisioning recorded a structured
failure"), while `codex exec --sandbox read-only` on the same machine exited `0` and answered. The
report is printed in full before the exit, so the probe reads its `checks` instead of the exit code:
`auth.credentials` must be `ok`, `installation` and `config.load` must not be `fail`, a failed
`sandbox.helpers` is ignored because no Worker runs in the sandbox, and any other failed check is
named in the readiness detail rather than withholding codex.

## A codex Worker can answer without starting a single command

Measured on 2026-10-12, in run `20261012-163837-6fc7b1` and two probes after it, against the npm
codex-cli 0.162.1 (installed 2026-10-11; the sandbox logs of every day before it, on 0.159.2, hold no
setup error). Every one of the 17 commands that run's codex Critics and Scouts tried failed before
it started — eleven of its twelve codex acts tried at least one — and every act still came back as
a successful critique or Scout answer.

- **A command codex could not start** completes as a `command_execution` item with `status`
  `failed`, `exit_code` `-1` and output opening "Failed to create unified exec process:" — here
  "…: helper_unknown_error: setup refresh had errors". codex pins that shape in its own
  `unified_exec_launch_failure_tests.rs`. The process still exits `0` and the result is written.
- **The cause was the sandbox setup, not the desktop app.** Before each sandboxed command codex runs
  a setup refresh, logged to `CODEX_HOME/.sandbox/sandbox.<yyyy-MM-dd>.log`. In 0.162.1 it validates
  the desktop app's `cua_node` runtime and failed with "open ACL target for root-only update … (os
  error 32)" on `node_repl.exe`. It failed the same way with the app quit and no process of it
  running, and with the `node_repl` MCP server disabled, while PowerShell could open the file
  exclusively. `codex sandbox -- cmd` passed the same refresh, because it does not validate the
  runtime, so it cannot stand in as a probe for `codex exec`.
- **A "good" answer had no shell either.** The answers that read files read them through the app's
  `node_repl` MCP server, which runs its own sandbox; none of their commands started.
- **`setup_error.json` describes the last refresh any codex ran.** codex writes
  `CODEX_HOME/.sandbox/setup_error.json` when a refresh fails and deletes it before every refresh and
  after a successful one (`codex-rs/windows-sandbox-rs/src/setup.rs` at `rust-v0.162.1`). doctor's
  `sandbox.helpers` reads it, with the code in `details["error code"]`. The desktop app's own
  successful refreshes delete it too, and nothing has failed at the start of a day, so the run's one
  probe, at 16:38, found it absent: a probe alone would not have named the fault.

So a Worker's own commands decide: a codex turn that tried commands and started none is a
Shell-less answer, whatever kept them from starting. See `docs/adr/0031`. No codex Worker runs in the
sandbox (next section), so the setup refresh above is on no Worker's path.

## Codex Workers run with the launching process's permissions

Every Codex launch — Builder, Critic and Scout — passes `--dangerously-bypass-approvals-and-sandbox`,
for both `exec` and `exec resume`. Codex CLI 0.159.2 advertised that flag for both commands, and
0.160.1 does on 2026-10-14. This is the fixed policy for every act, resumed retries and resumed Scout
turns included; see `docs/adr/0028` for the Builder and `docs/adr/0033` for Critic and Scout.

Codex's workspace-write restrictions on top-level `.git`, `.codex`, and `.agents` do not apply to
these launches. Builder access depends on the launching process's permissions. `builderRoots`
is still accepted and stored in the Run, but produces no writable-roots override and does not
restrict access. The Builder implements the approved plan's edits directly, while the server
continues to own executable gates.

## Claude keeps the Critic and Scout read-only; codex does not

The Critic judges and the Scout gathers evidence; neither may edit — including through any subagent
it spawns.
Nothing in this codebase enforces that; the guarantee is the vendor's, and codex gives none:

- **Codex** — nothing at launch. Critic and Scout run without the sandbox, as the Builder does, so
  their role contracts are all that keeps them from editing. See `docs/adr/0033`.
- **Claude** — `--permission-mode acceptEdits` and `--allowedTools Bash PowerShell` are passed only
  for a Builder, so the Critic's and Scout's edit and shell tools are simply never pre-approved.
  Every role gets the run's Worker tools, which is safe only while the granted servers are read-only; see
  `docs/adr/0017`. The builder's shell grant has no sandbox behind it: it is the price of a
  builder that can run the task's checks on a machine whose
  own settings approve nothing (issue #90).

## A headless claude asks nobody, so what its permission rules do not cover is refused

Measured on 2026-09-17 against Claude Code 2.1.273, `claude -p --output-format stream-json` with
`--model haiku`, after run `20260916-134641-21e3d5` whose claude builder was refused every Roslyn
call (issue #90). Each refusal arrives as a `system` message with subtype `permission_denied`, a
`tool_name`, and a `message` that is a string, which `ClaudeCliSession.Observe` logs as
`vendor.skipped-message`.

- **An MCP rule matches the server name exactly or not at all.** Against a server named
  `roslyn-mcp-plan-forge-flow`, `--allowedTools mcp__roslyn-mcp-plan-forge-flow` let
  `roslyn_search_symbols` through; `mcp__roslyn-*` and `mcp__*` were both refused exactly as with
  no rule. The documented forms are `mcp__<server>` and `mcp__<server>__*`: the `*` stands for a
  tool, never for part of a server name.
- **A `PreToolUse` hook is matched by regular expression, and its `allow` is a grant.** The same
  call went through with no allow rule when `--settings` carried a hook with matcher
  `mcp__roslyn-.*` whose command printed
  `{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"allow"}}`.
- **`acceptEdits` runs no command that needs approval, and a blanket `Bash` rule lifts the safety
  checks as well.** Under `--permission-mode acceptEdits` alone, `echo {a,b}`, `cd sub && git
  status` and `X=5; echo $X` were refused — as "Brace expansion", as a directory change ahead of a
  version-control command, and as "This command requires approval". With `--allowedTools Bash`, or
  a `Bash` hook answering `allow`, all three ran. The builder of the run above had only pattern
  rules, the `Bash(...)` entries of the user's own settings, and was refused on brace and simple
  expansion all the same: a pattern rule is not that grant.
- **A deny rule can name a parameter.** `--disallowedTools "Bash(run_in_background:true)"` beside
  `--allowedTools Bash` refused the backgrounded call with "Permission to use Bash with
  run_in_background:true has been denied." (`decision_reason_type: "rule"`).
- **Only codex lists its servers as data.** `claude mcp list` prints text and health-checks every
  server on the way (about 4 s here, with five of them refusing the connection); `codex mcp list
  --json` answers in about 3 s with `name`, `enabled` and `transport` per server.

The codex half, measured the same day against codex-cli 0.154.0 with `gpt-5.6-luna`, `-c
sandbox_mode="read-only"` and `--ephemeral`, against a server named `probe` that a project
`.codex/config.toml` declared with a `url` and no approval key:

- **A headless codex refuses an unapproved MCP call too**, and says so in the stream rather than in
  the exit code: the `mcp_tool_call` item completed with `status: "failed"` and
  `error.message` "MCP tool call requires approval, but approval policy is never", and the process
  exited 0. The same run with `-c mcp_servers.probe.default_tools_approval_mode="approve"` got its
  two results. That per-server key is what `roslyn-setup-repo` writes into the project config, which
  is why the codex critic of run `20260916-134641-21e3d5` could reach Roslyn on port 5053 while the
  claude builder beside it could not.
- **A `-c` key merges into the server it names, and invents one it does not find.** With the key
  above, `codex mcp list --json` still showed `probe` with its `url`; naming a server that no layer
  declared (`mcp_servers.nosuch.…`) stopped codex before it started — "failed to load bootstrap
  configuration … invalid transport in `mcp_servers.nosuch`". A grant may name only a server the
  list reported.
- **A project config is read from a linked worktree as well.** `codex mcp list --json` run inside a
  scratch folder of a worktree of `C:\Dev\CodexPlugins`, a trusted project, listed the server that
  folder's `.codex/config.toml` declared.
- **An MCP call is an item of its own**: `item.started` / `item.completed` with `item.type`
  `mcp_tool_call`, carrying `server`, `tool`, `arguments`, `result`, `error` and `status`.
  `CodexCliSession.Observe` read only `command_execution` and `agent_message`, so a codex worker's
  MCP calls, refused or not, never reached the run log.

End to end through this server's own launch, `WorkerToolsIntegrationTests` on the same day: a claude
critic and builder (`haiku`) and a codex critic and builder (`gpt-5.6-luna`, `low`) each called
`roslyn_search_symbols` successfully with nothing but the default `roslyn-*` grant — claude through
the six `roslyn-*` servers of this repository's local scope, none of them in the user's settings,
codex through a scratch project config that declared the server with no approval key. The
codex critic also called `cua_repl` unasked on the way — a server the user's own configuration
approves — which is the reach every worker already had, not one this grant added.

A server a plugin declares, measured on 2026-10-29 against codex-cli 0.165.0 and Claude Code
2.1.312:

- **Codex knows a plugin's server only under its plugin.** `codex mcp list --json` lists it by its
  `.mcp.json` key with no plugin id; only `transport.cwd` gives the plugin away,
  `<CODEX_HOME>\plugins\cache\<marketplace>\<plugin>\<version>` — with backslashes and a trailing
  `\.` for an installed plugin, forward slashes for a bundled one. `-c
  mcp_servers.cache-detective.default_tools_approval_mode="approve"` stopped even `codex mcp list`
  with "invalid transport in `mcp_servers.cache-detective`", as a server no layer declared does;
  `-c plugins.cache-detective@dlin10-ai-coding-plugins.mcp_servers.cache-detective.…` left the
  server as listed. Under `codex exec -s read-only` a find-files call failed with "MCP tool call
  requires approval, but approval policy is never" without the plugin-scoped key, and succeeded
  with it.
- **Claude lists it as `plugin:<plugin>:<server>`**, and the rule
  `mcp__plugin_<plugin>_<server>` that `ClaudeCliSession.ToolRule` makes of that name granted it
  under `--permission-mode acceptEdits`.

## A headless claude kills what it backgrounded, and its own harness steers it there

Measured the same day, same version, with the arrival time of every stdout line recorded; it is
what issue #91 is about. Run `20260916-134641-21e3d5` lost a ten-minute grid in task 8 because its
builder started it with `run_in_background`, answered, and exited — there is no next turn in `-p`.

- **A foreground `sleep` is refused by Claude Code itself**, before any permission rule:
  `sleep 90 && echo slept` came back as a `tool_use_error` telling the model to "use
  run_in_background: true" to wait for a command it started. The harness a worker runs in teaches
  the very habit that loses the work.
- **A backgrounded Bash call is killed about five seconds after the final result.** The stream
  carried `task_started` with `task_id`, `tool_use_id`, `description`, `task_type: "local_bash"` and
  `is_backgrounded: true` — the command itself is only on the `tool_use` that `tool_use_id` names —
  then a `tool_result` of "Command running in background with ID: …", the `result`, and 5.1 s
  later `background_tasks_changed` with an empty list, `task_updated` with
  `"patch":{"status":"killed"}`, and `task_notification` with `status: "stopped"`. The process
  then exited **0**. None of those messages has a `message` property, so `Observe` drops every one
  unlogged; that run saw them only in the stdout tail of task 11, which exited 1.
- **A foreground call is not silent.** A 90-second foreground command produced `task_started` with
  `is_backgrounded: false`, a `tool_progress` line with `heartbeat: true` and
  `elapsed_time_seconds` every 30 s, and `task_notification` with `status: "completed"`. Each
  heartbeat is a stdout line, so the 30-minute idle reaper in `StreamingProcess` never fires on a
  long foreground command; the Bash tool's own ceiling does — `BASH_MAX_TIMEOUT_MS`, ten minutes
  by default.

Documented, not measured: a background *subagent* keeps `-p` open until it finishes or has idled
for ten minutes (`CLAUDE_CODE_PRINT_BG_WAIT_CEILING_MS`), because its result is part of the output.

## Each vendor spells its Fast tier differently, and says differently what it served

Measured on 2026-09-25 with one trivial prompt per probe, against codex-cli 0.157.0 and Claude Code
2.1.282:

- **Codex** lists exactly one tier for every listed model in `codex debug models`:
  `service_tiers: [{"id": "priority", "name": "Fast", "description": …}]`, beside
  `additional_speed_tiers: ["fast"]`. The description is the only price hint and it differs by
  model — "2x speed, increased usage" for `gpt-6-astra`, a bare "1.5x speed" for `gpt-6-sol` and
  `gpt-6-luna`. `codex exec -c service_tier=fast` was served on the priority tier: the
  `response.completed` event carried `service_tier="priority"`. With `service_tier=default` the
  session configuration logged `Some("default")` and the same event carried no tier, so `default`
  is an accepted spelling of standard speed. The served tier appears only in the diagnostic log
  (`RUST_LOG=info`, on stderr), never in `--json`. An unknown tier such as `bogus` starts a turn
  without an error, and `flex` is refused by the ChatGPT backend with a 400.
- **A codex worker inherits `service_tier` from `~/.codex/config.toml`**, and the Codex desktop
  app rewrites that key when its Fast toggle changes. Without an override of its own, a worker's
  speed is whatever the desktop app was last set to.
- **Claude** reports `fast_mode_state` (`on` / `off` / `cooldown`) and, when off,
  `fast_mode_disabled_reason` on the `system`/`init` message — before the first API call — and
  again on `result`. Without an opt-in the reason is `sdk_opt_in_required`; the opt-in is
  `"fastMode": true` in the `--settings` JSON, where it merges with any other key. The answer
  depends on what the session can see. Under `--bare`, which skips the keychain, `init` judges the
  model alone: `opus` (claude-opus-5-5) came back `on`, `haiku` and `fable` `off` with no reason.
  With the account visible, all three came back `off` with `extra_usage_disabled`: fast is billed
  as extra usage, and this account's organisation has overage disabled (`rate_limit_event`:
  `overageStatus: rejected`, `overageDisabledReason: org_level_disabled`). Both answers arrive at
  `init`, so a session killed there costs no API call.

## A resumed session reports its running total, and each vendor totals something different

Measured on 2026-09-29 from the Runs' `telemetry.json` files against the vendors' own session
files, written by codex-cli up to 0.157.1 and Claude Code up to 2.1.284:

- **Codex** puts the thread's `total_token_usage` into `turn.completed.usage`, and `exec resume`
  restores that total from the rollout, so every counter keeps rising across a thread's turns. The
  rollout (`~/.codex/sessions/…/rollout-…-<thread>.jsonl`) carries a `token_count` event per model
  call with the call's own `last_token_usage` beside the running total. Summed over a turn, those
  equal the total's growth exactly: in run 20260927-212553-b98178 the review_fix round 3 turn
  reported 81,766,668 input tokens, its own calls came to 8,779,454. The Scout of run
  20260928-211855-673395 rose the same way, 4.3M → 9.3M → 18.2M → 22.4M → 27.1M → 33.2M.
- **A codex turn that ends without `turn.completed` still spends into the thread.** Build task 3 of
  run 20260923-111905-632662 was cancelled after an hour in which the thread's total grew by 40.4M
  input; the 1:15 retry that followed reported 69,995,168 against the 29,342,856 before it, while
  its own calls came to 0.2M. So the recorded growth of the next reported turn carries what the
  cancelled one spent, and a cancelled turn that is never followed takes its spend nowhere: the
  Scout turn the host interrupted after 3:32 in run 20260924-133053-da283a spent 1.65M input that
  no record shows.
- **Claude** puts two accounts on its `result` line: `usage`, the tokens of the process's main
  loop, and `modelUsage`, one entry per model with `inputTokens`, `cacheReadInputTokens`,
  `cacheCreationInputTokens`, `outputTokens` and `thinkingTokens`. `modelUsage` and
  `total_cost_usd` are the session's running totals: each process appends them to the session
  transcript as a `cost-state` entry, and `--resume` restores them from the latest one. A two-turn
  haiku probe showed it directly: the fresh turn's `modelUsage` equalled its `usage`, the resumed
  turn's held both turns while its `usage` held its own, and each `cost-state` entry equalled its
  result line. `thinkingTokens` equals `usage.output_tokens_details.thinking_tokens` and never
  exceeds `outputTokens` in any of the 179 `cost-state` entries on this machine, so it is the
  reasoning subset of output.
- **`modelUsage` counts what `usage` leaves out: subagents and context compaction.** On the builder
  of run 20260928-113803-31c1d7 the growth of summed `modelUsage` between consecutive `cost-state`
  entries equals the tokens recorded from `usage` on thirteen of fourteen turns, and the fourteen
  `totalCostUSD` equal its recorded costs, 4.60, 8.70, 21.62 … 89.79. The first build task 6 turn
  auto-compacted a 967,179-token context and grew `modelUsage` by 37,260,634 input tokens where
  `usage` held 36,293,602. A fresh Scout that ran four `Agent` subagents in run
  20260925-162135-46c10c held 381,084 cache-read tokens in `usage` and 23,647,257 in `modelUsage`.
  A subagent on another model gets an entry of its own; 9 of the 179 entries name two models. So
  claude's tokens are read from `modelUsage`, summed over its models.
- **A claude result from before any model call carries an empty `modelUsage`**, as the catalogue
  probe's expired sign-in did in run 20260927-101749-f2aa9c. That is no report rather than a total
  of zero, which on a resumed session would reset the count.

A resumed attempt therefore records what the running total grew by since the session's latest
recorded report, and keeps the total as `sessionTotal`. The record keeps the total rather than
leaving it to be summed from the growths because after one inconsistent report a sum stays wrong
for the rest of the session, while with the total kept the attempt after it counts from the latest
report. See docs/adr/0024.

## A host hands a worker what the user installed there, this plugin included

Measured on 2026-08-29, after a builder run whose every shell call failed without leaving a trace
in `forge.log`: the builder's prompt listed the `forge` skill, and the worker started a
plan-forge-flow MCP server of its own per task — four `planforge.exe` processes that outlived the
runs that spawned them, each holding a console window, because a server launched from one that has
no console gets a fresh one. What the host offers a worker is a property of the host, not of the
role, so `prompts/orchestration-contract.md` is appended to every role — it answers the half of
this we control, and the rest is the host's.

## A backgrounded vendor process has three reapers while the server lives

Read out of `StreamingProcess` rather than measured: the kill-tree lives in the enumerator's
`finally`, so it fires only while the server process is alive. On the one-call path the host's own
timeout reaches that block when it cancels the call, which is why a call cut short there leaves no
orphan.

A job is deliberately detached from its `tools/call` token; that detachment is the whole point, and
it removes the host as reaper. Three things remain while the server lives: `StreamingProcess` stops
each vendor attempt after 30 minutes without a stdout line, `forge.work.cancel` cancels the job after
an explicit user decision, and `ApplicationStopping` cancels every active job so a graceful exit
still reaps the children. There is no fixed wall-clock limit on a critic or builder. Every stdout
line resets the idle window, recognised event or not.

`forge.work.poll` and each of `forge.status.activeJobs` expose that distinction as `lastActivityAt`, the time
of the last stdout line, and `lastEvent`, a bounded description of the last recognised vendor event.
An unrecognised line advances only the first. Neither field is a licence to cancel automatically:
the idle reaper owns automatic stopping; the orchestrator cancels only when the user asks, or after
showing these fields and obtaining confirmation.

A server killed outright can still orphan a worker that is producing output: nothing here uses a
Windows job object, and on Windows a child does not die with its parent. For a critic that is
harmless, since every vendor keeps it read-only. For a builder it means edits landing in a workspace
whose run is already gone.

## The post-exit drain bounds this machine as well as the pipe

`StreamingProcess` ends a stream on the process's own exit rather than on EOF, because a server the
vendor spawned inherits the handle and can hold the pipe open for the rest of a run. What is left
after the exit is a bounded drain, and the bound was two seconds on the reasoning that everything
the process wrote is already in the buffer — true of the buffer, and silent about the reader.

CI run 33916817192, on a runner slow enough for one test to take fifteen seconds, is where that
showed: `git rev-parse HEAD` exited 0 and its single line never arrived inside the window, so
`Baseline.CaptureAsync` recorded an empty head, `Assert.NotEmpty(baseline.Head)` failed, and nothing
in the log said output had been dropped — an expired drain is indistinguishable, to the caller, from
a stream that ended.

The number is squeezed from both sides, which is the part worth keeping. Too short drops output
already written, as above. Too long rebuilds the wait that ending the stream on the exit was written
to remove — a spawned server holding the handle, and a critique delivered in two minutes read as a
twenty-minute timeout — and `DiagnosticLogTests` holds that to ten seconds. The stdout bound is five
seconds: several times any ordinary scheduling delay, and inside that guard. What makes it
survivable rather than lucky is that its expiry writes `process.drain.timeout` to the run log, so
the next machine slow enough to lose a line says so instead of returning a short stream. stderr
keeps the two-second bound and stays silent when it expires: what is lost there is a tail in the
log, not the answer, and a child holding both pipes would otherwise pay the stdout window at the end
of every process it outlives.

## Claude Code aborts a silent call, and the manifest timeout feeds both of its clocks

Measured on 2026-08-18 against Claude Code CLI 2.1.234, headless, with the same probe server:

- `tools/call` carries `_meta: { "claudecode/toolUseId": …, "progressToken": … }` — the token is
  sent, so a server-side `IProgress<>` would reach the wire.
- A silent tool call is aborted client-side: "sent no response or progress for 30s; aborting", with
  `CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT=15000` set (the abort came at 30 s, so treat the configured
  value as approximate). The abort message itself names the knobs: a per-server `"timeout"` in
  milliseconds in the server entry, or that global idle variable, `0` to disable.
- `notifications/progress` feeds the idle timer: the same 45-second tool emitting progress every
  5 s under the same idle setting ran to completion.
- The per-server `"timeout"` field is honored and is a hard wall clock that progress does not
  extend: with `"timeout": 20000`, the call died at 20 s despite progress every 5 s.

Hence `.claude-plugin/plugin.json` sets `"timeout": 3600000` on the server entry — the hour the
Codex host grants through `tool_timeout_sec` — raising the wall clock and lifting the idle floor
for this server alone. It is a host bound on the one-call surface, not a vendor-attempt timeout: a
continuously active worker can now outlive it, while a silent attempt is reaped by the server after
30 minutes. The field was measured through `--mcp-config`; the plugin manifest declares its server
with the same entry schema, which is the one assumption not yet measured end to end.

A worker did outlive it, in round 5 of run `20260917-111319-20e672` (issue #96). The manifest
value is now measured end to end and exact: `process.kill` with `reason: cancelled` landed
3600.11 s after `tool.call`, and `tool.cancelled` 1 ms later. Two things the measurement settles.

- The idle reaper was not involved and was not at fault: `tool_progress` heartbeats kept arriving,
  so the thirty-minute silence window never opened. What ends a one-call act on this host is the
  wall clock alone, and nothing the server does resets it.
- The two clocks nest badly. The builder's own `Bash` command hit **Claude Code's 1800 s** limit
  and was moved to the background at 02:08:12; the manifest's hour expired at 02:08:16. Half the
  call was spent inside a single tool use of the worker's, and the worker got it back with four
  seconds of budget left — after finishing its edits at 01:29, rewriting the baseline at 01:33 and
  running the plan's gates green at 01:37, all of which the act then lost whole.

So the hour is not a bound the server can plan around, and a cut-short turn is a normal ending
rather than an anomaly. `BuilderTurn` records one instead of discarding it: see "A turn the host
cut short is recorded, not lost".

## Verification is self-reported; where the gate is a command, the host's run of it decides

A builder that changed files but could not execute anything used to have no honest answer: `status`
was `done` or `blocked`, so it answered `done` and put the caveat in prose, which only a careful
orchestrator noticed (issue #24 — a Codex sandbox that could spawn no process at all). "Did the
work" and "proved the work" are orthogonal, so the contract carries them on separate axes:
`status` stays `done | blocked`, and a required `verification` reports `passed | failed |
unavailable` with evidence.

The report is the builder's word, and until 0.27.0 the server re-checked nothing: the only signal
it could have checked against — command exit codes in the vendor's event stream — exists reliably
for Codex alone, and a guarantee that varies by vendor is worse than none: a vendor that reports
its own shell's exit code rather than the command's would pass a build that never ran. That
reasoning still holds, and the event
stream is still not read for verdicts. What changed is that the server stopped needing the vendor's
signal: run `20260904-173914-9254ec` had a builder answer `passed` for six tasks whose named tests
it never wrote, because the suite it ran was green at the old count, so the server now runs the
gate command **itself**, on the host, and reads its own exit code — the same answer for every
vendor. See `docs/adr/0015`. The audit trail is still the run log, which records each vendor
tool's outcome for every vendor and now the gate run beside them. Reacting to a gate that is a
condition rather than a command, and so to `unavailable` or `failed` where nothing could be run,
still belongs to the orchestrator, for the reason it always did: a condition has no exit code, and
blocking the flow on a self-report would kill a run that can degrade gracefully.

Measured on 2026-09-05 against pwsh 7.6.5, which is what the gate runner leans on: a `trap` that
exits swallows the record it caught unless it writes it first, so the runner's trap writes to
stderr before exiting; `$PSNativeCommandUseErrorActionPreference = $true` under `Stop` makes a
native non-zero exit terminate the script with `$LASTEXITCODE` still set, so a two-line gate stops
on its first failing line and reports that line's code; and the Store install of pwsh is reachable
only through the zero-byte execution alias that `docs/adr/0013` strips from codex's PATH — the
runner keeps it, because this process runs as the user and the alias launches for it. Windows
PowerShell 5.1 is the fallback and has no native-error preference, so a multi-line gate there is
judged by its last line alone.

Measured on 2026-09-16 against pwsh 7.6.6, which is why the gate command is no longer passed as
`-EncodedCommand`: base64 of UTF-16 is about 2.7 characters of argument per character of script, and
`CreateProcess` caps the command line at 32,767, so launching from the Store path this box resolves
— 85 characters — with the runner's five other arguments and its wrapping, **a gate command of
11,930 characters starts and one of 11,931 does not**. The failure is `Win32Exception`, "The
filename or extension is too long", which the runner had been reading as a shell that would not
start: outcome `not_run`, which leaves the builder's self-report standing, so the task counts with
nothing checked. Run `20260915-143837-8bc4d1` lost a 14,824-character gate that way — 40,328
characters of base64 — and the same script passed when run by hand from a file. The number moves
with the length of the shell's path, so the ceiling is a range rather than a constant, and the gate
now travels as a temporary `.ps1` run with `-File`, where no length applies at all. That file is
UTF-8 **with** a BOM: Windows PowerShell 5.1 reads a BOM-less script as the system codepage, which
is what would mangle a gate naming a non-ASCII path or test. See `docs/adr/0015` and its amendment.

## A failed act used to leave no trace, so the run log is the server's own record

The Flow log and Canonical decision ledger record the **results of acts that reached their commit
point**, so an act that threw before it could commit may write neither. The run behind #19 left a
folder holding `state.json` and no record of whether the vendor CLI was spawned, with what
arguments, or how it died. Vendor sessions did emit `Started`/`Finished`/`Failed`, but only into an
unbounded channel that production never reads.

`forge.log` closes that. It is JSONL rather than prose because its interesting fields are
themselves multi-line — a command line, a stack trace, a tail of stderr — and one object per line
keeps them greppable without an escaping convention of our own. Long fields are cut, not dropped:
the head of a plan draft still says which draft it was.

Three things route into it, all through `RunLog.Current`, an ambient the tool wrapper sets for the
duration of a call:

- the tool surface — every call with its arguments, and its result, exception or cancellation;
- `StreamingProcess` — the executable, the full argument list, the working directory, the pid, the
  exit code, a killed process's reason (cancelled, idle, timeout, output cap), a stream trimmed to
  its head and tail, and a bounded stderr tail;
- `Microsoft.Extensions.Logging`, bridged by `RunFileLoggerProvider`, which is how the MCP SDK's
  own dispatch and transport entries survive a call that dies before any act writes anything.
  `ClearProviders()` used to discard them; stdout carries the protocol, so the run folder is the
  only sink available.

The ambient falls back to the last run this process served, because transport-level entries can
arrive on a context that never flowed through a tool handler and cannot carry a run id — which is
precisely the entry a timeout would otherwise drop.

The orchestrator writes through `forge.log.append` rather than by hand. A tool rather than a
documented licence to edit the file: the run id keeps passing the same containment check as every
other write, the format stays one thing rather than one per agent, and the skill's "do not
hand-edit anything under `.forge/`" rule survives intact.

## A failure explains itself only if it is an exception of ours

The run log records why an act died, but the caller was told nothing: the SDK replaces the message
of any exception that is not an `McpException` with a generic one, so that a server cannot leak
whatever a stray exception happens to carry. Measured against SDK 2.2.0 on 2026-08-29: the run in
#44 died on a prompt file that was never deployed, `forge.log` named the exact path, and what the
orchestrator received was `An error occurred invoking 'forge.plan.review'.` It retried, gave up, and
found the reason only by reading the log afterwards.

The twelve exception types declared in this assembly are all written for that reader — they name the
argument, the round, the cap, the path. `Mcp/ToolErrors.cs` answers those as a tool error carrying
their message and lets everything else keep the SDK's blank. It is a `CallToolFilters` filter rather
than a `try`/`catch` inside the tools because `RunDirectory.Open` runs before the run's log exists
and so before anything a tool could wrap: a non-absolute `workspaceRoot` or a lost `runId` fails
earlier than every other failure, and a wrapper inside the acts would have left exactly that class
mute.

An argument the server refuses is one of ours too, since 0.25.1. `WorkAct.ValidateArguments` and the
job-id checks threw `ArgumentException`, which belongs to the framework, so `userGrantedRound is not
used by build.next` was written for the orchestrator and then blanked on the wire — measured against
0.24.0 on 2026-09-03, both refusals arriving as `An error occurred invoking 'forge.work.start'.` The
in-process tests pinned the wording and passed, because nothing covered the trip; `ToolSurfaceTests`
now runs the validator through the filter. The null and whitespace guards stay as they were: they
answer a programmer, not a caller, and a blank message there costs nobody a retry.

A call the SDK cannot bind fails before any code of ours runs, and its exceptions are the
framework's. Measured against SDK 2.2.0 (Microsoft.Extensions.AI 10.8.3) on 2026-10-20, through an
in-process server: a missing required argument is an `ArgumentException` with `ParamName`
`arguments` that names only the first one missing; a value of the wrong JSON type, at the top or
nested, is a `JsonException` naming the .NET type and a path relative to the argument (`$`,
`$.decisionBatchId`), never the argument; and both arrived as `An error occurred invoking
'forge.plan.confirm'.` A run's orchestrator sent `forge.plan.confirm` and `forge.plan.show` without
`plan` and had to read the reason out of `forge.log`. `RequestContext.MatchedPrimitive` is already
set inside the filter, so the filter answers those two exceptions from the matched tool's published
schema. It explains after the binder has failed rather than checking before it, because the schema
is stricter than the binder about nulls: the binder passes a JSON null to any reference-type
parameter, and `forge.plan.patch`, whose edits are published as `object`, takes a null edit and
refuses it itself with the edit's index and the sizes it measures.

## Required and nullable is a contract with no encoding that works

A nullable parameter with no default is published as `required` with `"type": ["string", "null"]`,
which reads as "send it, null is fine" and is answered by the SDK's marshaller refusing the call
when the key is absent: `The arguments dictionary is missing a value for the required parameter
'revision'.` Measured on Claude Code 2.1.247 on 2026-08-29, the other half was uncallable too — the
host's tool-call serializer dropped the `null` literal and emitted `"revision": ,` on four separate
attempts, so the request never parsed. The only encoding that reached the server was a non-null
string, which pollutes the meaning of a field whose absence is the point.

So every nullable parameter on the tool surface carries `= null`, which is what keeps it out of
`required`; `ToolSurfaceTests` pins the rule by reflection and `build/package.ps1` pins it against
the published schema. Domain rules that depend on a value being present — a second review round must
carry a `revision` — stay where they always were, in the act, where they can say what is missing and
why.

## The `canvas` profile is built, and no supported host negotiates it; nor the Tasks extension

Measured on 2026-08-15 against a spike server built on the MCP C# SDK 2.2.0: Claude Code 2.1.233
negotiates protocol `2025-11-25` and reports `extensions: null` with no UI capability. Neither the
MCP Apps extension (the `canvas` profile) nor the Tasks extension (streamed progress) was negotiated.

A host this plugin no longer supports later negotiated the MCP Apps extension, advertising
`{"extensions":{"io.modelcontextprotocol/ui":{"mimeTypes":["text/html;profile=mcp-app"]}}}`, and
runs from it recorded `profile: "Canvas"` — see [docs/adr/0008](docs/adr/0008-render-the-plan-on-a-canvas.md)
and [docs/adr/0043](docs/adr/0043-remove-cursor-as-host-and-vendor.md). The detector only says
`Canvas` when `McpApps.GetUiCapability(...)` returns non-null. Claude Code still reports `Text`;
Codex has not been measured, because its sandbox refuses to spawn the spike.

So the `canvas` branch is written, and it is one tool wide: `forge.plan.show` renders the plan
through the `ui://planforge/plan.html` resource — see [docs/adr/0008](docs/adr/0008-render-the-plan-on-a-canvas.md).
Every other tool still delivers markdown in the tool result, which is also what a `Text` host gets
from `forge.plan.show` itself.

The Tasks half stands unchanged: nobody negotiates it, so progress is observable only at the
granularity of one tool call per unit of work, plus `forge.status` on demand.

## Surfacing the flow log is the orchestrator's act, and each host differs

No MCP mechanism lets this server make a host display a file — resources and notifications can
carry one, but nothing renders unasked — so showing `flow_log.md` belongs to the orchestrator,
and the skill instructs it per host.

The Claude Code desktop panel is documented to render a file on send and not to watch the disk, so
the skill's rule is: surface once, refresh after every worker call.

What the skill could not do on its own is make that happen early. Run `20260822-190108-fcccbd`
surfaced the path in its closing message and nowhere before it: the
instruction to show the file at the first plan-review result had fallen out of view long before the
result arrived, and no tool result carried the path, so nothing brought it back. Every act result
now carries a `documents` object — a path plus what to do with it, per file — from the moment each
file exists, which is the same remedy `forge.work.poll` got for its next call in 0.18.2 and the same
reason: an instruction that lives only in the skill does not survive an hour of a run.

## The plan was the one artefact the run withheld

`PLAN.md` was written by `forge.plan.confirm` and nothing else, and the skill told the orchestrator
to keep every draft to itself until the critic said `approve`. The two together made plan review a
sequence of verdicts about a document the user had never read — they had chosen the critic and the
builder, and then watched judgements on something invisible for up to five rounds.

The draft was already reaching the server every round as `planDraft`, and already reaching disk in
`forge.log` through the tool-call record, so nothing had to be gathered and nothing new was exposed
by writing it. Every round now writes it to `PLAN.md` before starting the critic — before, because
the round takes minutes and those minutes are when reading it is worth something — and the path
travels out beside the flow log's under `documents.plan`. The chat rule that replaces "keep the
drafts to yourself" is "link them, do not paste them": the old rule was right that five pasted
revisions bury the sixth, and wrong that a file the user can ignore does the same.

What it costs is that `PLAN.md` no longer means "the approved plan". Nothing ever read the file's
existence as approval — `Build` and `CodeReview` both gate on `approved` in the run state — but a
round run *after* an approval would have left that flag raised over text nobody approved. So a round
now withdraws it, dropping the builder session and the built tasks the new text reaches with it, and
says so in the flow log. See [docs/adr/0009](docs/adr/0009-the-plan-is-visible-from-the-first-round.md),
including the two orderings inside the act that the safety of this rests on, and
[docs/adr/0039](docs/adr/0039-a-patch-keeps-the-built-tasks-it-does-not-change.md) for which built
tasks a change reaches.

## The link to the plan waited for the critique it was meant to precede

Writing `PLAN.md` before the critic did not put it in front of anyone: the path travels only inside
`documents`, which `forge.plan.review` builds after the critique comes back. Measured in run
`20260904-173914-9254ec` (planforge 0.25.1, client claude-code 2.1.260, profile `Text`, critic codex
gpt-5.6-sol/high), local time, from `.forge/<runId>/forge.log` and the session transcript:

| | round 1 | round 2 |
|---|---|---|
| orchestrator starts streaming the draft into the tool call | ~18:14:28 | ~18:27:29 |
| `forge.plan.review` reaches the server; `PLAN.md` written | 18:19:40 | 18:32:35 |
| critique returned | 18:26:00 | 18:38:33 |
| orchestrator sends both files (`SendUserFile`) | 18:26:48 | 18:39:10 |

The draft was 52 KB as a tool argument in round 1 and 62 KB in round 2 — about five minutes of the
orchestrator typing, then six of the critic, with no link to the plan for either. The round-1 link
arrived seven minutes after the file existed.

Two calls now, not one: `forge.plan.write` writes the draft and answers with `documents` in seconds,
and `planDraft` is optional on `forge.plan.review` and on `forge.work.start`'s `plan.review`, so the
round reads the file instead of carrying the draft a second time. `WorkStartResult` and
`WorkPollResult` carry `documents` for the same reason. The flow log stays where it was: it is first
created when the first critique is appended, so `documents.flowLog` cannot honestly arrive earlier
than that critique. See [docs/adr/0014](docs/adr/0014-writing-the-plan-is-its-own-call.md).

## The orchestrator's turn is an act too, and the timeline used to skip it

The same run left four plan-review verdicts in `flow_log.md` with nothing between them. Three of
them said `revise`, so the plan changed three times, and none of those changes is anywhere in the
file — the server records what workers return, and revising the draft is the one act it does not
delegate. A reader of that timeline sees a critic contradicting itself rather than a loop
converging.

`forge.plan.review` now takes the answer with the round it answers into: `revision`, refused when
absent from the second round on, and an optional `deferred`. The split between them is the split
between the two logs. A revision is already visible to the next critic — it is holding the
redrafted plan — so it goes to the flow log alone, where nothing feeds it back. A deferral is
invisible in the draft, which is exactly why the same finding returns every round unless the review
log carries it as a decision; that is the mechanism `forge.review.fix` already used for code
review, and it applies unchanged here.

Both are written after the critique returns rather than before it, so a vendor timeout or a
restarted server — both of which this run hit — records the revision once when the act is retried
with the same arguments.

## A turn the host cut short is recorded, not lost

The same principle, one failure further out. Every act writes its logs and its state *after* the
worker returns, which is right for a worker that fails — the act is retried with the same arguments
and records once — but wrong for a worker the host takes away. Round 5 of run
`20260917-111319-20e672` lost an hour that way: the builder had finished its edits, rewritten the
baseline and run the plan's gates green, and `forge.review.fix` left behind no timeline entry, no
decision-memory update, no state, and no gate of the server's own. Only the edits survived, because they
were already on disk. The orchestrator read the tree by hand and wrote the round up through
`forge.log.append`.

`BuilderTurn` used to exclude cancellation from its salvage deliberately, on the grounds that the
host taking the call away "has no report to replace". It has no report; it has a tree. So a
cancelled turn now leaves as a `TurnCutShortException` carrying the files git says the builder
wrote, and `forge.review.fix` and `forge.build.next` record the turn on its way out:

- the flow log gets a `cut short` entry naming those files, so the timeline stops skipping the hour;
- findings sent to the Builder remain unresolved in the Canonical decision ledger, while deferred
  and rejected dispositions remain settled because they are the Orchestrator's decisions and the
  kill does not touch them;
- `state.json` keeps the builder's resume token — the vendor reports the session id on its first
  stream line, long before the answer that never came — so the retry continues the conversation
  instead of starting cold;
- `PendingGateFailure` carries a cut-short brief, spent by the next turn the way a killed-background
  brief is, telling the builder what is on disk and that the call ends at a deadline it cannot see.

Three things it deliberately does **not** do. No gate runs: the tree is mid-edit and the token that
would run one is already cancelled, so a verdict then would be about a tree nobody chose. Nothing
is reconstructed from `vendor.text` — at 01:37 that builder said its gates were green and then
changed files for another half hour, so its narration is mid-thought, and a salvaged `done` would
be the self-report the gate exists to replace. And the cancellation still travels as cancellation,
so the act fails and `tool.cancelled` is what the run log shows; what changes is that the failure
is no longer silent. Saving the work itself would mean detaching the worker from the request, which
is what `forge.work.*` already does and what the one-call surface deliberately still does not — see
issue #96.

## A declared elicitation capability is not a rendered one

Measured on 2026-08-15 against the Claude Code desktop surface, running the 0.7.0 server. Three
things are established by which code path executed, rather than by inspecting the wire:

- The client **declared** `elicitation`. The capability guard passed instead of refusing, and a
  refusal would have surfaced as an error rather than a result.
- The client **answered**. The answer-carrying branch is reachable only when `InputResponses`
  already holds the approval key, and that branch is what returned.
- The answer carried no accepted content, so it read as a refusal.
- The user was shown nothing at all.

What the host put in `action` is *not* known: the 0.7.0 code never read that field, and the version
that does was never run on that surface. It would not have helped — a host answering for the user
can send `decline` as easily as anything else.

The earlier measurement, that elicitation works including the full multi-round tool response cycle,
was taken on Claude Code 2.1.233, the terminal CLI. Both findings are true of the surface each was
measured on, and that is the whole problem: the guarantee is per-surface, it degrades silently, and
a server cannot tell which surface it is talking to. Approval by elicitation was removed in 0.8.0 —
see [docs/adr/0003](docs/adr/0003-approval-through-the-orchestrator.md).

## Documentation is outside the review boundary

The baseline, drift report, code-review diff and sensitive-path guard share one pathspec. It hides
every `CONTEXT.md` and every path under `docs/adr/` at any depth. The guard takes the same pathspec
deliberately: it covers exactly the set of paths whose contents are sent, so a sensitive *name* under
an excluded path — an ADR called `0005-token-rotation.md` — is not a leak and must not abort the run.

The baseline and drift report describe the working tree against the `HEAD` at the moment each is
read. The code-review window instead describes the final tree against the commit recorded by
`forge.begin`, while that commit remains an ancestor of the current `HEAD`; this is what keeps work
in view after the Orchestrator commits it mid-run. Both forms add untracked files as new-file diffs,
so staged, unstaged and brand-new files are visible without staging anything. The window was
narrower once: a bare `git diff`, blind to every new file, which is how round 1 of the run behind
issue #21 was spent on findings about code that existed on disk (issue #25). The run folder never
widens in, because `.forge/` ignores itself.

If history no longer descends from the recorded commit — after a rebase, amend or branch switch —
the review window falls back to the current `HEAD` and says so in both the Critic's prompt and the
returned summary. A missing or unresolvable baseline is broken Run state, not divergence, and stops
the review. Preparing the path list and content diff remains lock-free: both reads use the same base
commit and pathspec, but a concurrent edit may land between them.

Two limits come with the documentation boundary, both recorded rather than fixed. A third party's
edit to `CONTEXT.md` or an ADR is invisible to drift and to code review. And a vendor worker runs in
the workspace, so nothing here stops it reading an excluded file it was not sent; the pathspec
governs what is handed over, not what is reachable.

A code-review round may add its own paths to that pathspec with `excludePaths`, and they then leave
the changed paths and the content diff together, so the sensitive-path guard narrows with what is
sent. Git's diff headers name a tracked file from the repository root, while a pathspec and
`ls-files` are relative to `workspaceRoot`; the window therefore names tracked files without the
workspace's prefix, so that a name the size refusal lists is one `excludePaths` takes. The
Critic's diff keeps git's headers as they are.

## Codex refuses a turn over 1,048,576 characters; claude states no limit

Read from the codex-rs source on 2026-10-05 against codex-cli 0.159.2, after run
`20261004-093111-b79938` failed a code-review round with `turn/start failed: Input exceeds the
maximum length of 1048576 characters` and `actual_chars: 1056671`:

- **What codex counts.** `protocol/src/user_input.rs` sets `MAX_USER_INPUT_TEXT_CHARS = 1 << 20`.
  The app server's turn processor sums `text_char_count` over the turn's input items, which is
  `text.chars().count()` for a text item — Unicode scalar values, so a surrogate pair is one
  character where .NET's `string.Length` counts two — and refuses the turn above the limit.
  `codex exec` turns the prompt read from stdin into exactly one text item; `developer_instructions`
  and the `--output-schema` file are not input items and are not counted. Plan Forge writes stdin as
  UTF-8 without a byte-order mark, so the act prompt it measures is the text codex counts.
- **How close the run came.** Its telemetry records each attempt's Worker prompt size, which adds
  the role prompt and the schema and counts UTF-8 bytes: 1,086,196, 1,138,761 and 1,133,780 bytes
  for the three rounds that passed, 1,152,629 for the refused one. The window only grows over a run,
  so a run that passes round one near the limit meets it later.
- **Claude.** The `claude.exe` of Claude Code 2.1.286 holds no client-side character limit on its
  prompt. The only length refusal is the API's `prompt is too long`, counted in tokens against the
  model's context window, which no character count here can predict.

The server therefore measures the code-review prompt for codex only, before the critic starts; see
docs/adr/0029. Over the limit it gives the window by path rather than refusing it; see
docs/adr/0034.

## A codex critic reads the files it is pointed to

Measured on 2026-10-18 from run `20261017-180043-675224`'s `forge.log`, whose three code-review
rounds each gave a codex `gpt-6-astra` critic 30 untracked files by path: the `vendor.tooluse`
events of every round name all 30. A name in a command shows the file was opened, not that it was
read whole, and a search over a folder reads files it never names, so this is evidence for the
delivery, not a check the server could run. No claude critic has been given files by path: claude
states no limit, so it never is.

## The session is not the workspace, and only one host says where it is

`workspaceRoot` arrives as a tool argument and used to decide three unrelated things: where
`.forge/<runId>/` lives, the git window the baseline and the code-review diff are taken over, and
the working directory the critic and builder processes get. The orchestrator picks it from the shape
of the task, and on a monorepo it correctly picks the repository root — which is right for the last
two and wrong for the first. Measured on 2026-09-02, run `20260902-224201-7bf03b` on `claude-code`:
the session's directory was `plugins/cache-detective`, the plan also rewrote the root `README.md`,
`marketplace.json` and `.github/workflows/`, so `forge.begin` was called with the repository root and
`PLAN.md` landed two levels above the session. Claude Code renders a file reference as a link only
when the href is relative to the session's working directory, so the run's most-read document
arrived as unclickable text. See issue #53.

Pinning `workspaceRoot` to the session instead is the zero-code fix and silently shrinks the review.
`GitPathspec.WithoutDocumentation` opens with `"."`, and a `.` pathspec is resolved against git's own
working directory; `git ls-files --others` is working-directory scoped too. Measured in this
repository, `git -C . ls-files --others` names `plugins/cache-detective/CONTEXT.md` while
`git -C plugins/cache-detective ls-files --others` names `CONTEXT.md` — so the baseline, the drift
shown at approval and the diff handed to the critic would all have stopped at the subdirectory, and
the root files the same plan rewrites would have been invisible in exactly the way issue #25
describes.

So the session is asked for rather than passed in. Measured on 2026-09-03 by answering each host's
handshake with a server that records the `initialize` params and then requests `roots/list`:

| host | protocol | declares `roots` | answers `roots/list` |
|---|---|---|---|
| `claude-code` 2.1.258 | 2025-11-25 | `{"listChanged": true}` | the session's working directory |
| `codex-mcp-client` 0.147.0 | 2025-06-18 | no | `{"roots": []}` |

Codex answers a request it never advertised, which is why the declared capability decides whether
to ask at all rather than the shape of the answer. Only the host that declares it moves its run
folder; the other keeps the layout it had.

Roots is deprecated by the specification of 2026-07-28 (SEP-2577, which retires sampling and logging
with it) on the grounds of vague semantics and low adoption, and it names no successor — after it
goes, nothing in MCP tells a server where the user is sitting. Deprecated features stay functional
for a year of spec versions. The fallback is what makes that survivable rather than a deadline: a
host that stops declaring the capability reads as a host that never had one, and the run folder goes
back under `workspaceRoot`. The cost of the removal is the clickable link, not the run.

The decision that follows from all of this — and why the surface was not changed instead — is
[docs/adr/0011](docs/adr/0011-the-run-follows-the-session-not-the-workspace.md).

## A compaction leaves the Orchestrator its own summary; Claude Code and Codex run a hook after it

The Orchestrator's host compacts its context several times a run. Counted from the `compact_boundary`
entries of the orchestrator transcripts of the concurrency-hunter runs of 14–20 October 2026: A10
(`5c1f5a`) 3 at about 960k tokens, B3 (`675224`) 1 at about 960k, B3b (`7586e3`) 5 at about 470k,
B3c (`0d2a47`) 6 at about 470k — in plan review, the build and code review alike.

After one, Claude Code re-injects an invoked skill's body capped at 5,000 tokens per skill and
25,000 in all, keeping the start of the file, and re-reads at most five recently touched files. The
forge skill was 97 KB, about 25k tokens, so four fifths of it went, and the ToolSearch-loaded tool
schemas with it. Right after B3c's third compaction the Orchestrator called `forge.plan.show` and
`forge.plan.confirm` with argument shapes copied from its summary and got a bare error twice (see
`docs/adr/0038`), and from then on it declared no phase: B3c's `telemetry.json` holds one
`orchestrator.usage` record, `interview`, for an eleven-hour run.

Claude Code runs a plugin's `SessionStart` hooks with matcher `compact` after an automatic or
manual compaction and adds what they print to stdout to the compacted context, capped at 10,000
characters, by its documentation. A hook's `command` runs in Git Bash, or in PowerShell where the
hook says `"shell": "powershell"`; an exec-form command cannot start a `.cmd`. Measured on
2026-10-21 with `claude` 2.1.302 headless and a `SessionStart` hook of matcher `startup` asking for
PowerShell: the hook process had `CLAUDE_CODE_SESSION_ID` equal to the session's `session_id` — the
same variable the server reads for the Host session — and `CLAUDE_PROJECT_DIR`, and the card
`planforge.exe --resume-card` printed for a fixture Run reached the model, which quoted its
`planHash` line back exactly. The card's own text is ASCII, because PowerShell may decode a native
program's output in the console's code page.

Codex 0.162.1, read from its source at tag `rust-v0.162.1` and its embedded hook schemas: after any
compaction of a root session, manual or automatic, `SessionStart` hooks matching `source` `compact`
run before the next model request, and their stdout or `hookSpecificOutput.additionalContext`
becomes a developer message, spilled to a file past about 2,500 tokens. `PreCompact` and
`PostCompact` cannot add context. The hook gets no session variable: its stdin JSON carries
`session_id` — the root thread's id, which is the `_meta.threadId` the server records for the Host
session — and `cwd`. An invoked skill's body is dropped by compaction there too, while `AGENTS.md` is
put back. A plugin's hooks come from the file its manifest's `hooks` names, or from
`hooks/hooks.json` when it names none; on Windows the handler's
`commandWindows` runs, in the user's detected shell or `cmd.exe` without one, after `${PLUGIN_ROOT}`
is replaced. Installing a plugin does not trust its hooks: each runs only once the user has trusted
it through `/hooks`, and again after its command changes. Measured on 2026-10-21 with `codex exec`,
`--dangerously-bypass-hook-trust` and `model_auto_compact_token_limit=2000`: the hook's `session_id`
equalled the `thread_id` of `thread.started`, the production command ran under `pwsh` and got its
stdin through `cmd.exe` and the launcher, and after the mid-turn compaction the model quoted the
card's `planHash` line exactly. The same command fed the same stdin under `cmd.exe /C` printed the
card too.

So there are two hook files, each named by its host's manifest: `hooks/claude.pff.hooks.json` by
`.claude-plugin/plugin.json`, `hooks/codex.pff.hooks.json` by `.codex-plugin/plugin.json`. Neither is
`hooks/hooks.json`: measured the same day with a probe plugin under `claude -p --plugin-dir`, Claude
Code loads `hooks/hooks.json` as well as the file its manifest names, tolerates `commandWindows`,
and runs the `command` field. The Codex file's `command`, which only a non-Windows Codex would run,
is `exit 0`. Both installs copy the whole plugin folder, so each host holds the other's file and
ignores it. Measured with the files under these names: the probe plugin's Claude Code session ran
only `claude.pff.hooks.json`; and `codex plugin add` from a local marketplace in a throwaway
`CODEX_HOME` installed 0.56.0 with both files, after which a `codex exec` with only a `startup` hook
of its own printed the card after a forced compaction, once — the compact hook came from
`codex.pff.hooks.json` through `.codex-plugin/plugin.json`.

**A Codex that has not trusted the hook gets nothing from the resume card.** An Orchestrator there
recovers the way the skill core says for any host without a card — `forge.status` first, then the reference for the phase the run is in. The skill core's size
helps every host alike.

## An escaped letter cost the Orchestrator eight times the letter

Every tool result used to leave the server indented and with all non-ASCII as `\uXXXX`. Measured on
2026-10-21 from the Orchestrator transcripts of the concurrency-hunter runs A9–B3c: the context
growth from one turn to the next, on 452 turns whose only addition was a single tool result of
4 KB or more, regressed on the result's character classes (median error 4.3%, p90 16%), prices an
escape at 4.10 tokens, a raw Cyrillic letter at 0.50, any other character at 0.45 and whitespace at
0.18. On B3b and B3c, held in Russian, two thirds of the forge result tokens were escapes and
indentation; on the English runs about a tenth. Forge results were 10–24% of a run's context
growth, so the encoding was the one change to results worth most of a compaction window, and why
results now leave through one compacting filter — see
[docs/adr/0042](docs/adr/0042-tool-results-carry-what-the-orchestrator-acts-on.md).
