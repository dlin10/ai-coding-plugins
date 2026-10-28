# Plan Forge Flow releases

## 0.68.3

Codex now hands the server the Advisor variables, so a configured Jev or Microsoft-Decision-1 is
discovered there. Issue #205.

- Codex gives a STDIO MCP server only a small default environment plus the names its configuration
  lists in `env_vars`, and `.mcp.json` listed none. A key exported for the Codex host never reached
  the server, `advisors.configured` stayed empty and the Evidence check took the manual path.
- `.mcp.json` now forwards `PLANFORGE_JEV_API_KEY`, `PLANFORGE_JEV_MODEL`,
  `PLANFORGE_MSDECISION_ENDPOINT`, `PLANFORGE_MSDECISION_API_KEY` and
  `PLANFORGE_MSDECISION_DEPLOYMENT` by name; no value is stored. A test checks the list against
  every variable in the Advisor catalogue, read through the path the Codex manifest names.
- Claude Code is unchanged: it inherits the whole environment and declares its server in its own
  manifest. Advisor secrets are still removed from every child process.
- A variable must be in the host's environment before the server starts. A running server keeps
  the environment it started with: restart Codex (or Claude Code) after setting or changing one.

## 0.68.2

Bound the Windows launcher's unused binary cache and remove its abandoned downloads. Issue #203.

- Cache preparation uses a short-lived inbox PowerShell helper; cmd remains the server's parent.
  GUID download names and held handles isolate concurrent downloads. Failed promotion reuses the
  winning executable after removing only the current launcher's temporary file.
- Preserve discovered installations, running images, seven days of recent use and two older
  rollback copies. Skip uncertain inventories, busy files and reparse points. Custom installations
  are remembered on first launch; plugin folders and locks are never deleted.
- Clean only abandoned GUID downloads older than seven days. Legacy numeric download names remain
  untouched because their ownership cannot be established safely.
- Packaging runs the isolated Windows launcher regression suite and includes the cache helper.

## 0.68.1

A JSON or YAML file whose name only carries a secret keyword is read instead of refusing the whole
code-review round. Issue #200.

- The name rule (`secret`, `token`, `password`, `credential`, or an `id_` SSH-key name) refused any
  such file whatever it held, so concurrency-hunter's generated model of
  `Microsoft.Extensions.Configuration.UserSecrets` refused every round that touched it, and its
  generator hash changes with every engine edit. For `.json`, `.yaml` and `.yml` the round now reads
  the file's lines: the content rule as before, and a value that stands alone on its line, an array
  element or a list item with no key beside it, when it is a long hex run or has three kinds of
  character. `tokens.json` and `secrets/prod.json` are read the same way.
- A value with a key beside it is left to the content rule, which reads the key. A generated model
  carries hashes, GUIDs and member ids beside keys, 206 of them in the demo's 76 files and none
  standing alone, so withholding them would refuse nearly every model. The trade: a secret under a
  key that does not say so, in a file whose name does, is no longer refused by name.
- Still refused by name: every other extension, XML included, the files known to hold secrets
  (`secrets.json`, `appsettings.Production.json`, `.env`), key material and sensitive locations.
- A refusal by path now says how to go on: ask the user, then pass the path in `excludePaths`.

## 0.68.0

Edit text inside a plan target without resending the whole target. Issue #192.

- `forge.plan.patch` accepts `edit` with a unique exact `find` and a `replace` string, including an
  empty replacement. Edits apply in order, and a missing or repeated match refuses the patch with
  the target and match count. Whole-target `replace` remains available.
- The Flow log shows each edit as a truncated `find → replace` summary. Edits to built tasks keep
  the same rebuild boundary as whole-target replacements; plan-review guidance prefers `edit` for
  small changes.

## 0.67.0

Hand minor plan-review findings to the Builder without expanding the plan. Issue #191.

- A `builderNote` decision settles a minor plan finding for confirmation and names the tasks that
  receive its finding and Orchestrator reason. Blockers and majors cannot become Builder notes.
- A change to the plan's task list makes existing notes stale until the Orchestrator reopens and
  binds them to the current tasks. Code review shows notes as checks against the diff.

## 0.66.0

Offer a plan split before the first review when the first draft's Approach is large. Issue #190;
ADR 0048.

- `forge.plan.write` and `forge.plan.patch` return plan shape measurements for total and section
  characters, task sizes, R-items, and named test classes. The Flow log records each shape.
- An Approach of at least 22,000 UTF-16 characters triggers an advisory split offer before round 1.
  The Orchestrator names separable task groups and their sizes and requirements, while the user may
  choose to review the full draft as written.

## 0.65.0

Revisions read before they write, a major's closure carries evidence, and the Critic lists a class
whole. ADR 0047; ADR 0022 extended.

- *Writing a revision* in `plan-review.md` turns its checks into acts done before the patch. Each
  clause of a new rule names the test cell a wrong implementation fails. Every claim about the code
  is read with Read or a Roslyn query in that revision: a Scout answer is a pointer, and the
  Evidence check never sees the Orchestrator's own claims. The class is listed with `find_callers`
  or `find_references` before writing, and a question to the user carries only read claims.
- `addressedByRevision` takes `evidence` and requires it for a blocker or major: what the revision
  read, the class it listed and the failing cell. A batch missing it is refused once, naming every
  such closure. The Flow log prints it beside the closure. Batches without evidence keep their
  canonical bytes and digests.
- In the critic contract, the members a rule leaves out are one finding against that rule. It lists
  every member the Critic found and how it looked, or calls the list partial. Places that each need
  an edit of their own stay separate findings. A finding that needs a rule and its check states them
  apart.
- Measured in Run `20261024-105628-872ce5`: 15 of the 16 later findings that cited the code pointed
  at lines nobody had read when the fix they hit was written, and 7 of the 9 unread claims were
  false.

## 0.64.0

Check acceptance feasibility before the first draft. Issue #189.

- When acceptance is an outcome on a real target, the interview asks Scout what that outcome
  depends on and whether each dependency holds under the chosen mechanism. Unmet or unsettled
  dependencies reach the user before drafting: change the acceptance, change the mechanism, or
  choose a probe first.
- A probe is ordinary task 1, whose gate checks the acceptance outcome itself. Failure or inability
  to verify it stops the run before automatic retries or later tasks.
- When the user continues without Scout, the Orchestrator makes the same feasibility check.
  Refactors and documentation changes without an outcome on a real target do not need it; the
  Impact pass still checks the drafted approach.

## 0.63.0

The Evidence map judges each item once and prices every call. ADR 0046; ADR 0036 amended.

- The systemone request body is written as UTF-8 instead of escaping every non-ASCII character as
  `\uXXXX`, and is measured that way. A Cyrillic letter had counted as six bytes, so a Russian plan
  reached the state-plus-question bound at a third of its real size: in Run
  `20261024-105628-872ce5` every pair with a task was withheld as `too_large` from the third map on,
  and up to 1,607 of 1,902 items came back unchecked. Jev and Microsoft-Decision-1 both change;
  cache keys do not.
- A complete map leaves the items it judged out of every later call by the same Advisor and
  requested model. Their marks accumulate in `evidence-map.json` with the number of Scout answers
  read; a later call asks only about the items of new Scout answers and new implications, and the
  result and Flow log report the rest as `alreadyMapped`. A revised plan, changed requirements or
  reworded Change points no longer send a judged item again. Another Advisor or model, or a map
  written by an earlier version, judges every item once more. In that Run the old rule re-asked
  every item on nine maps for USD 12.74, and eight of them raised nothing new.
- `evidenceMapWarning` now names a Scout answer that arrived after the last complete map, instead
  of a plan changed after a review round. The skill maps after the first draft and after each later
  Impact pass, not before every round.
- Every map result carries `estimatedCostUsd`, what its packed requests measure before sending at
  the Advisor's price; `costUsd`, its reported input tokens priced; `runCostUsd`, the Run's Advisor
  spending so far; and `budgetUsd`, the Run's Advisor budget, 5 USD until the user names another.
  New optional argument `budgetUsd` (above 0, at most 1000) sets it, and the Run keeps it. A call
  whose estimate would take the Run past its budget sends nothing and answers
  `stoppedBy: "budget"`; the skill then asks the user. `advisor.usage` records carry `costUsd`, and
  the Flow-log entry the estimate, the cost and the Run's spending against the budget.
- Measured before deciding: Jev's reported input tokens were 1.03–1.05 times the measure on four
  real maps, so a call can end a few percent above its estimate. Replayed through the tool, the
  Run's first two maps cost USD 0.82 and 0.29 instead of 0.86 and 1.51. Dropping the requirements
  and exclusions from the state saved 23–25% but moved 28–38%
  of item statuses, against Jev's own 6–8% between two runs of one map, and raised 36–54% more
  flags without recalling more of the findings the Orchestrator had accepted; it is not shipped.

## 0.62.0

`forge.evidence.map` maps in one call. Issue #186; ADR 0036 amended.

- A call no longer stops starting requests at 40 seconds and cancels the running ones at 45, a limit
  that served Cursor's 60-second tool calls after 0.59.0 had removed Cursor. No request starts after
  3000 seconds, the bound `forge.work.poll` already waits to; a request already running finishes
  under the Advisor's own timeout, and only the caller's cancellation cancels one. A map that used to
  take several calls takes one, with one Flow-log entry and one `advisor.usage` record.
- The map stays resumable and its result is unchanged: judgments are stored as they arrive, and
  `complete`, `remaining`, `checkedThisCall` and `stoppedBy` report a call that ends early. The
  tool's description and the skill call again only when `complete` is false, and not after a call
  whose `checkedThisCall` is 0.
- Microsoft-Decision-1 requests still waiting for quota are no longer cancelled at the end of a call
  and asked again by the next one: the call waits for the quota. One call of 150 requests on a
  60-a-minute deployment took 100 s, with no 429.

## 0.61.0

The run's decisions are a Run artifact with identity. Issue #178, ADR 0045.

- New tool `forge.decision.record` appends one decision to `decisions.json` and a
  `## Decision D-nnnn` entry to the Flow log: the Orchestrator phase, the question as asked, the
  options offered with the recommended one marked, the answer as given, `by` (`user` or
  `orchestrator`), what it bears on, and the earlier decision it supersedes. The server numbers it
  from `D-0001`; the same content again is a `no_op` answering with its ID; a superseded decision
  stays recorded and cannot be superseded twice. Every field passes the secret guard before the
  call is logged. The server now exposes twenty-two tools.
- Every plan-review and code-review critic is shown the run's current decisions as a `# Decisions`
  section after the ledger projection, one line each, superseded answers left out; beyond 12,000
  characters the oldest are left out and named. The critic contract says a decision is not a gap.
  A code-review round over its vendor's limit reports the section's size.
- `forge.status` carries `decisions`: the count and the last five, one line each. Every result's
  `documents` carries `decisions` once the file exists. The resume card names the file and the
  count, and shows the call's shape.
- The skill records every answer that settles something the moment it is given, instead of an
  `Interview D-n` or `(user):` note in `forge.log`, and shows the user the decision count beside the
  plan at approval.
- A `decisions.json` that does not read back stops the call that reads it and is left as it is.

## 0.60.0

Scout runs beside the Orchestrator's own work. Issue #171, ADR 0044.

- `forge.work.start` runs `scout` again, on every host, with `question` and `sessionMode` and none of
  the Builder's arguments; the vendor, model, effort and Fast come from the persisted Scout
  selection. A fetched Scout job carries the same `scout-answers.json` entry, Flow-log outcome and
  `documents.scout` as `forge.scout.run`. `plan.review` and `review.code` stay refused.
- A Run can have several running jobs: fresh Scout jobs run side by side, while a `continue` Scout
  job and a Builder act each run alone. A start that may not run answers `started: false` with every
  running job in `activeJobIds`. `forge.status` reports them all as `activeJobs`, replacing
  `activeJob`.
- Scout turns that overlap — fresh jobs side by side, or a direct call beside a job — leave no
  session anchor; a later follow-up is `fresh`, naming the answers it builds on. A direct `continue`
  is refused while a Scout job runs.
- A Scout turn writes the state as it stands when the turn ends rather than the copy it read when it
  started, so what the Orchestrator wrote meanwhile — instructions, a new Scout choice — survives,
  and answers that finish together are numbered in the order they finish instead of the second one
  being refused and lost. A turn that ends after the user chose another Scout leaves the new
  choice's session and failure alone.
- The skill starts the orientation question as a job right after the Scout selection and asks the
  opening questions — the change's boundary, what stays out, the verification timing — while it
  runs. The Impact pass starts as a job once the approach is sketched and its matrix check made;
  while it runs the Orchestrator asks the Critic and Builder questions and writes the requirements,
  gates and tasks, then fetches the pass and folds it in before the first `forge.plan.write`. An
  Impact pass naming more than six Change points is split by area into fresh jobs run side by side.
  `planning` now begins with the approach sketch.
- The resume card for a run with no plan yet points at `forge.status`'s `activeJobs` for Scout jobs
  started before the compaction.

## 0.59.0

Cursor is no longer supported, as a host or as a vendor. ADR 0043.

- The plugin ships no `.cursor-plugin/plugin.json` and is gone from the monorepo's Cursor
  marketplace; `build/package.ps1` refuses a bundle that carries a Cursor manifest or
  `prompts/cursor/`. An installation made from that marketplace keeps the version it has.
- `cursor` is no longer a vendor: `forge.models` reports claude and codex, and an act or Scout
  selection that names `cursor` is refused as an unknown vendor. `prompts/cursor/` and the
  schema-in-the-prompt path that only Cursor used are gone; both remaining vendors hand their schema
  to the CLI.
- `forge.work.poll` holds every client to 1–3000 seconds and no longer reads the client's name; one
  that names no wait still waits 45.
- `forge.work.start` runs the Builder acts alone, `build.next` and `review.fix`, for one that may
  outlast the host's hour. `plan.review`, `review.code` and `scout`, which only the Cursor routing
  sent through a job, are refused as unknown acts, and their arguments — `planDraft`, `deferred`,
  `revision`, `userGrantedRound`, `question`, `sessionMode`, `excludePaths` — are gone from its
  schema. Every other act goes through its one-call tool, as the skill already sent it.
- `forge.plan.show` keeps the canvas for a host that negotiates MCP Apps.
- The monorepo validator lets a Claude catalog entry be missing from the Cursor catalog when its
  plugin ships no Cursor manifest.

## 0.58.0

Tool results carry what the Orchestrator acts on, compact and unescaped. Issue #176, ADR 0042.

- Every JSON tool result leaves through one call-tool filter that writes it without indentation and
  with non-ASCII as written instead of `\uXXXX`. On the measured runs held in Russian this was two
  thirds of the forge result tokens; nothing the Orchestrator reads changes but its spelling.
- `forge.work.fetch` carries the act's payload as the `result` object instead of a JSON string.
- `forge.status` reports the run state the Orchestrator decides from, without the server's own
  bookkeeping: `taskChanges`, the builder session id, vendor and scope, `workerTools`,
  `builderRoots`, `startedAt` and `baselineHead` are gone. `run.gateEnvironment` is the sorted
  variable names: the values, often connection strings, no longer reach the result or `forge.log`.
- `forge.plan.confirm` answers with `approved` and `taskCount`; drift is read from `forge.status`
  before asking.
- A `forge.build.next` whose task passed cleanly — `done`, verification `passed`, gate `passed`,
  directly or through a Job — answers with those outcomes, the gate's exit code and seconds, and the
  counters. The Builder's summary, evidence and files and the gate's command and output stay in the
  Flow log; every other result goes out whole.
- A `forge.work.poll` carries `documents` only when they changed since the job last answered with
  them. `forge.plan.patch` no longer echoes `baseHash`, the applied edits or their sizes, which the
  Flow log and `forge.log` record. `forge.models` drops each model's vendor description.

## 0.57.0

Plan review gets an end the Critic can reach, and a converging review no longer waits for the user
at the cap. Issue #172, ADR 0041.

- `prompts/critic-contract.md` defines severity by the implementer the plan names: `major` when,
  following the material as written, that implementer would produce a wrong result or leave a
  stated goal unmet; `minor` when it says too little but the implementer would still do the right
  thing, and for wording, counts and gaps a gate already catches. The verdict is `approve` when no
  `blocker` or `major` remains among the new findings and the unresolved ones still present; minors
  still reach the ledger and the Builder. Six rated examples from the 2026-10-06 study mark the
  major/minor line.
- At the cap, `forge.plan.review` and `forge.review.code` (and `forge.work.start` for either) run
  the round without `userGrantedRound` when the last round's finding count — its new findings plus
  the unresolved ones its Critic still found — is below the round before it and none is a
  `blocker`, for as long as the count keeps falling. The cap is not raised; a flat or rising count
  or a blocker is refused with the existing cap error.
- The Flow log records `## Plan review — round continued automatically` (or `Code review`) with
  `continued automatically: 4 → 2`, and the round's result carries `continuedAutomatically`.
- The run state gains `autoContinuedReviewRounds`, `autoContinuedCodeReviewRounds`,
  `reviewTallies` and `codeReviewTallies`, which `forge.status` reports beside the granted rounds;
  the resume card prints `+ N automatic`. A run begun before 0.57.0 has no tallies and asks at the
  cap as before.
- The skill's *Rounds, revision, and caps* asks the cap question only when the tool refuses, and
  announces an automatic round in one line with its counts.

## 0.56.0

The Orchestrator comes back from a context compaction knowing where its run stands. Issue #170,
parts 1 and 2; ADR 0040. Part 3, smaller tool results, is issue #176.

- A resume card on Claude Code and Codex. A `SessionStart` hook with matcher `compact` runs
  `planforge.exe --resume-card`, a third entry point that serves no MCP. It prints, for the Run whose
  open phase names this session, the run and its folder, approval, tasks built, rounds and caps, the
  saved `fullGate`, the gate environment's names, the critic's, builder's and Scout's selections,
  the finding IDs by disposition and those pending full verification, where the run stands, the
  next calls with their exact argument shapes, and the skill reference to read before the next
  call; it says when the declared phase lags the run. IDs, counts and shapes only, in ASCII, about
  2.7 KB for run B3c. Nothing for a session with no open Run.
- Claude Code's hook is `hooks/claude.pff.hooks.json` and hands the card the project directory and
  `CLAUDE_CODE_SESSION_ID`. Codex's is `hooks/codex.pff.hooks.json`, and the card reads Codex's hook
  input from stdin: `session_id`, the root thread id, and `cwd`. Each host's manifest names its file.
  Codex runs it only once you trust it through `/hooks`. Cursor runs no hook.
- The forge skill's core is 13.8 KB, under the 5,000 tokens Claude Code re-injects after a
  compaction, down from 97 KB: the participants, the phases and the file each reads, every tool's
  arguments, the `decisions` shape, and the rules that hold throughout. Every other section moved
  verbatim into `skills/forge/references/` — `asking.md`, `interview.md`, `scout.md`, `planning.md`,
  `plan-review.md`, `ledger.md`, `approval.md`, `build.md`, `code-review.md`, `tools.md`.
- `build/package.ps1` ships the references and both hook files, reads its skill markers across all
  of the skill's files, checks that the core names every reference and stays under 18 KB, allows
  `hooks/` to hold exactly the two hook files, and runs the published binary's `--resume-card` in
  both forms.

## 0.55.0

A plan patch after approval keeps the built tasks it does not change. Issue #168, ADR 0039.

- `forge.plan.patch` compares the plan's tasks, as the build parses them, before and after the
  patch. The build goes back only to the first task whose text changed: a patch that rewrites,
  inserts or removes task k, or adds prose after task k, keeps tasks 1 to k − 1 with the files they
  changed for the next task's hand-over and drops a pending gate failure. A patch to the title,
  Requirements, Gates or a task not yet built keeps every built task and the pending gate failure.
  Approval is withdrawn and the Builder session forgotten either way. Concurrency-hunter run B3c
  re-ran 42 built tasks, 2.4 h of Builder time, across 8 such reopenings.
- The patch answers with `tasksKept` and `firstTaskOwed`, and the `## Plan reopened` Flow entry names
  the tasks kept and the task the builder will start from.
- `forge.plan.write` and a review round handed a `planDraft` still reset the progress to zero, now
  also on a run an earlier patch left unapproved with built tasks. A round over the plan on disk
  keeps what the last write or patch kept.
- `forge.plan.confirm` takes an optional `rebuildFrom`, a built task, with `approved: true` only,
  for when the user says a change outside the tasks invalidates a built one.
- The forge skill tells the user how many built tasks a revision and a re-approval keep.
  `build/package.ps1` asserts `rebuildFrom` in the `forge.plan.confirm` schema.

## 0.54.0

`forge.work.poll` waits as long as the host lets a call run. Issue #169.

- `forge.work.poll` takes an optional `waitSeconds`. Omitted, a poll waits 45 seconds as before and
  its result is unchanged. On a Cursor client — any client name containing `cursor` — it is held to
  1–45; on any other client to 1–3000, ten minutes inside the hour Claude Code and Codex give a call.
  A value outside the range is clamped, and the result's `waitClamped` says what was asked and what
  was waited. A long poll still returns the moment the job ends.
- The forge skill: on non-Cursor hosts a Builder act expected to run long — a task whose gate runs
  the suite, a fix batch — may go through `forge.work.start` with `waitSeconds: 3000` polls, instead
  of a one-call tool the host cuts at the hour with nothing recorded. Cursor keeps the 45-second
  loop. Concurrency-hunter run A10 spent 1,437 polls and about 2.4 h of Orchestrator model time on
  that loop.
- `build/package.ps1` asserts the `forge.work.poll` schema.

## 0.53.0

Microsoft-Decision-1 in Azure AI Foundry is the second Advisor.

- `microsoft-decision` is configured by `PLANFORGE_MSDECISION_ENDPOINT`,
  `PLANFORGE_MSDECISION_API_KEY` and `PLANFORGE_MSDECISION_DEPLOYMENT`, all required. Requests go
  to the configured https resource endpoint with an `api-key` header; Microsoft Entra ID is not
  supported. A project endpoint (`/api/projects/…`), a query, a fragment or user info makes the
  endpoint invalid, so the Advisor is reported as partly configured and never called.
- Both Advisors speak TypeSafe's systemone protocol, so its request, response, retry and size
  rules moved from Jev into one shared helper under `Advisors/`. Jev's requests and status mapping
  are unchanged; the helper now also reads a 422's body, so a Jev 422 whose body never arrives is
  a transient failure instead of `rejected`.
- Microsoft requests are paced to the deployment's quota, learned from the
  `x-ratelimit-*-requests` headers of each response, and shared by every Microsoft Advisor in the
  server process. A 429 empties the estimate, and only elapsed time refills it.
- A Microsoft response is a persistent failure only when its body blames the request:
  `unsupported_request_argument` is `rejected` and a token-limit 422 is `too_large`. A 404, a bare
  413 or 422 and any other refusal is `unavailable`, so a wrong deployment, endpoint or gateway is
  never cached against an item.
- `PLANFORGE_MSDECISION_API_KEY` is a secret: refused in gate environments and stripped from
  child processes like Jev's key.
- The opt-in live evaluation is one test per Advisor, each run with its own filter.

## 0.52.0

Approval takes the plan already on disk, named by its hash, and a call the SDK cannot bind says why.
See `docs/adr/0038`.

- `forge.plan.show` and `forge.plan.confirm` no longer require `plan`. Omitted, they show and
  approve `PLAN.md` as `forge.plan.write` and `forge.plan.patch` left it, and confirm does not
  rewrite the file; a missing or blank `PLAN.md` is refused. A plan passed works as before.
  Run 5d B3c lost the argument when a context compaction dropped the skill and the tool schemas, and
  then had to read its 76 KB plan back and send it whole.
- `forge.plan.confirm` takes an optional `planHash`, the hash of the plan the user was shown, and
  refuses a plan with any other hash before it applies a decision or writes anything.
  `forge.plan.show` answers with `planHash`; `forge.plan.write`, `forge.plan.patch` and
  `forge.plan.outline` already did. The forge skill sends no `plan` and passes the hash.
- A call the MCP SDK cannot bind now says why. A required argument missing, or a value of the
  wrong JSON type at any depth, came back as `An error occurred invoking '<tool>'.` on every tool,
  and only `forge.log` held the reason. The answer now names every missing argument
  (`forge.plan.write requires planDraft`) or where a value sits and what the published schema
  expects there (`forge.plan.confirm requires decisions.decisionBatchId to be string or null, not
  number`), and `forge.log` records it as `tool.unbindable` with the SDK's exception. Only a call
  the SDK has already refused is explained; a call it accepts still reaches the tool.

## 0.51.0

The Evidence map says when it has fallen behind the plan, the skill says how a contradiction is
settled, and the map's Flow-log entry counts every status.

- Once a Run has used the map, `forge.plan.write`, `forge.plan.patch` and `forge.plan.review`
  answer with `evidenceMapWarning` when the plan has changed after a plan-review round that no
  complete map call has followed. A round that runs so also writes a `## Evidence map not current`
  Flow-log entry. Patches made in answer to a map before its round draw no warning, and nothing
  is refused.
- A complete map records the plan hash it judged and the review rounds run by then in
  `evidence-map.json` (`lastCompletePlan`). A file written before 0.51.0 has none, so its Run is
  warned until its next complete map.
- The forge skill settles a `contradiction` flag with evidence: the task that changes what the
  fact describes, the code it names, or a probe — never a plan sentence that the code works as
  before.
- The `## Evidence map` Flow-log entry takes each status's total from the map result's `counts`.
  It used to count the flags, which hold only flagged items, so `covered`, `context_only` and
  `excluded` printed 0 whatever the map found. Only the flagged statuses keep their
  `(new N, carried M)` split.

## 0.50.0

An optional, shadow-mode Evidence map advises the Orchestrator after its manual Evidence check.
See `docs/adr/0036` and `docs/adr/0037`.

- `forge.evidence.map` is the twenty-first tool. Code selects every candidate judgment, validates
  typed Choice answers and settles statuses, retaining contradictions and accounting for unchecked
  items. Bounded calls resume from content-addressed `evidence-map.json`; flags distinguish new
  issues from those carried from the last complete map. The manual check remains mandatory and
  neither the map nor the Scout report is sent to the Critic, Builder or Scout.
- Advisors are external typed-question services, separate from Vendors and Workers. Jev is first,
  configured by `PLANFORGE_JEV_API_KEY` and optional pinned `PLANFORGE_JEV_MODEL` (default
  `jev-1.13.0`). `forge.begin` and `forge.status` report configured and partly configured Advisors
  without credential values. With none, no external request is made; with several, the Orchestrator asks once.
- Configuring an Advisor consents to sending Scout-item and implication text, plan units and Change
  points, which may quote code, from every repository forge runs in on that machine. Jev requests
  go to TypeSafe's standard, non-zero-retention service. Locator windows are checked and hashed
  locally, never sent, and external URLs are never fetched.
- The Scout report is only `scout-answers.json`: complete numbered answers with stable item IDs and
  locator checks. `SCOUT.md` is no longer written; the server never reads, changes or removes an
  earlier version's file. Scout tool results still return the complete answer, unclipped.
- Advisor secrets stay in the server environment: no tool arguments, results, exception messages,
  logs, telemetry or persisted Run content. Secret gate-environment names are refused ignoring
  case, and child environments strip them after overlays. State written before 0.50.0 is untouched.
- Each accepted map call attempts one content-free Flow-log entry and one `advisor.usage` record,
  even on cancellation or failure. `telemetry.json` has three kinds: Worker `vendor.usage`,
  Orchestrator `orchestrator.usage` and Advisor `advisor.usage`, with Advisor models, counts,
  tokens, cache hits and latency recorded without content.
- The skill places map calls after the manual check and before plan reviews, with stable Change
  points, explicit not-indexed-answer handling and manual checks for new unchecked flags. Sanitized
  fixtures run without network by default; an opt-in Jev evaluation reports quality, latency and
  cost without quality thresholds. README documents the disclosure, configuration and invocation.

## 0.49.0

Plans can be revised by snapshot targets between review rounds. See `docs/adr/0035`.

- `forge.plan.outline` returns the plan hash and document-order targets with IDs, kinds, labels,
  hashes and character sizes. `forge.plan.patch` applies local edits against that exact snapshot,
  checks the complete result, and returns the new outline, change summary, sizes and documents.
- `forge.plan.write` keeps the whole-draft route and also returns the outline read back from the
  written plan, or null for blank plan text.
- A replay after a lost patch answer conflicts as stale. Read the outline and redo the edits against
  its current hash; equal text is never proof that a patch was applied. The hash check takes no lock.
- A successful patch uses the whole-write lifecycle: approval is withdrawn, build progress and the
  Builder session reset, and the complete plan is persisted without a Worker, gate or review round.
  The reopening Flow entry names its cause: review round, whole-draft write or patch.
- A reproducible comparison test covers small and large plans: a patch and a whole-draft update
  produce identical plans, the submitted patch payload is smaller, and answers carry no plan text
  beyond labels. Payload, supplied, plan-before, removed, written and plan-after sizes are measured
  in UTF-16 characters and UTF-8 bytes; no host-token or latency saving is claimed.

## 0.48.0

A code-review round too long for its critic's vendor is given by path instead of refused. See
`docs/adr/0034`.

- Over the limit — codex's 1,048,576 characters — the server gives the untracked files by path,
  then, if that is not enough, writes each tracked file's diff under the run folder's
  `review-round-<N>/` and lists those paths, deleting them when the round ends. The critique's
  summary, and so the flow log, says how many files went by path and why.
- Only a round still over the limit with every file given by path is refused; its message lists
  the plan, the ledger projection and the file list, and advises `excludePaths`.
- `untrackedByReference` is removed from `forge.review.code` and `forge.work.start`. Nothing
  replaces it: the round no longer needs the user's answer to fit.
- The secret guard reads the prompt with every file embedded before any delivery is chosen.

## 0.47.1

`telemetry.json` records stand in the order they opened. See `docs/adr/0032`.

- A record is written after the last element whose `at` — its opening — is no later, instead of at the
  end of the file. A phase closes when the next is declared, so `plan_revision` round N used to follow
  the `plan_review` round N+1 that began inside it; it now stands between rounds N and N+1.
- An element without a text `at` stops the walk and an element already in the file is never moved, so
  a file written by an earlier version keeps its order and a file this server did not write is safe.
- No field changes. `at` was already the opening for both kinds; sum `duration`, not `wallDuration`,
  across Orchestrator and Worker records, as a phase's `wallDuration` includes the Workers inside it.

## 0.47.0

Codex Critics and Scouts run without Codex's sandbox, as Builders already did. See
`docs/adr/0033`.

- Every codex launch, fresh or resumed, passes `--dangerously-bypass-approvals-and-sandbox` instead
  of `-c sandbox_mode="read-only"`. A codex Critic or Scout is read-only by its role contract alone,
  with the launching process's permissions.
- `forge.models` no longer reports `degraded`, and the forge skill no longer withholds a vendor from
  Critic or Scout on that ground: no Worker runs in the sandbox, so its setup blocks none of them.
- A codex turn that tried commands and started none still fails, quoting codex's launch diagnostic
  without the sandbox log.

## 0.46.0

The Orchestrator's own time and tokens reach `telemetry.json`. Until now the file recorded Workers
only, so a run's cost had a hole the size of its most expensive participant. See `docs/adr/0032`.

- `forge.log.append` takes an optional `phase`: `interview`, `planning`, `plan_revision`,
  `approval`, `build`, `code_review`, `wrap_up` or `done`. `forge.begin` opens `interview`; each
  declaration closes the open phase and opens the next, and any other value is refused. The open
  phase is kept in the run folder's `orchestrator-phase.json`, so a later server process closes it.
- Each closed phase appends one `orchestrator.usage` record beside the `vendor.usage` records: the
  round or task the run was at, the host and its session, the phase's wall time less the time inside
  Plan Forge's own tool calls, and the tokens the Orchestrator used in it — or `usageMissing` with
  the reason, and `usageIncomplete` when they may be short.
- Tokens come from the host's OpenTelemetry export when the user's Claude Code `settings.json` or
  Codex `config.toml` sends OTLP/JSON logs to a loopback `/v1/logs` endpoint, and from the session's
  transcript otherwise; the export also carries Claude's cost. Cursor gives the server no session,
  so its records carry time only. The README shows both configurations.
- `planforge.exe --telemetry-collector --port <n>` is the Telemetry collector the export reaches: a
  second mode of the same binary, started detached by the first server that needs it, keeping only
  the usage fields of the sessions servers registered. Under Claude desktop, Codex desktop and
  `codex exec`, whose job objects refuse breakaway, it runs inside the host's job object and the next
  phase opening restarts it after the host ends it.
- Nothing about this changes a tool's result: a failing source, collector or phase file leaves the
  record short or absent and writes one `warn` entry to `forge.log`.
- The forge skill declares each phase where it begins, again on every return to it, and `done`
  wherever the run ends or stops.

## 0.45.0

A worker whose commands never started no longer passes for one that read the repository. In run
`20261012-163837-6fc7b1` codex-cli 0.162.1's sandbox setup failed before every command the codex
Critics and Scouts tried, and their blind critiques and answers went into the ledger and `SCOUT.md`
as ordinary results. See [issue #147](https://github.com/dlin10/ai-coding-plugins/issues/147) and
`docs/adr/0031`.

- A codex turn that tried commands and started none fails instead of answering. A review round is
  not spent and nothing reaches the ledger; the error and the flow log quote codex's launch
  diagnostic and, for a Critic or Scout, the sandbox log's newest setup failure, which names the
  blocking file. A Scout fails as `no_shell`, carrying only codex's error code.
- `forge.models` reports `degraded` on a vendor whose Critic and Scout cannot start a shell, from
  doctor's `sandbox.helpers` and from what this server's own Critics and Scouts showed since; codex
  stays available for the Builder. A degraded probe is not cached. `sandbox.helpers` no longer
  appears as a warning in `detail`.
- The forge skill offers a degraded vendor for the Builder only, and on a shell-less failure asks
  the user to repair the vendor or choose another rather than retrying.

## 0.44.1

- The forge skill asks each worker role's vendor question on its own, and only then the call with
  that role's model-and-effort, Fast and instruction questions, the critic's first. The
  model-and-effort options come from the chosen vendor's catalogue alone. Asked in one call with
  the vendor, they had to span every vendor, so a user who chose codex was still offered Claude
  models.

## 0.44.0

Code-review fixes can run a short, explicit Fix gate while full plan verification is deferred to the
Orchestrator. The interview asks once whether to keep full checks after each fix or use targeted
checks and verify the complete plan at the end. See [issue #144](https://github.com/dlin10/ai-coding-plugins/issues/144).

- `forge.review.fix` and background `review.fix` accept `gate=full|targeted`, with full as the
  default. Targeted requires one executable `**Fix gate:**` in the approved plan; retries preserve
  the attempt's mode and exact finding IDs.
- `forge.plan.confirm` saves `fullGate=beforeNextRound|final`. The Orchestrator runs all full-plan
  gates and conditions at that time. A failed full check leaves findings pending for the
  Orchestrator to assess, without automatic attribution or reopening.
- A passed targeted check keeps findings unresolved and pending full verification. Status, the
  Critic projection and Flow show their covering attempts; only successful full verification
  closes them. Final-mode review may approve while fixes are pending, but the run cannot finish
  successfully until full verification is recorded.
- Decision ledger v3 persists gate modes and pending findings, reads v2 without rewriting it and
  migrates on mutation. Direct and background calls share validation, replay and retry rules;
  invalid modes and damaged saved timing are refused without changing run state.

## 0.43.0

The forge skill gives the orchestrator six checks to walk before it writes a revision of a plan, and
asks it to label every later plan-review finding the way code review already does. Over thirteen
concurrency-hunter runs, about half of the plan-review findings after round 1 sat in text a revision
had written — two thirds from round 6 on — and the plans that took longest to review grew by two
thirds over their rounds, against a third for the ones that converged fastest.

- *Writing a revision* in the forge skill: a rule arrives with its gate rows and tests; a changed
  term, count or decision changes wherever the plan states it; claims about the code pass the
  Evidence check; a finding that names one member of a class is answered for the class; a new
  mechanism goes to the user before it is written; the revision changes what the findings name and
  what the checks reach.
- From the second plan-review round on, the orchestrator labels every new finding `own`, `sibling`,
  `same-class` or `older` and records one line per round with `forge.log.append`, so later runs
  can measure the share a revision caused.

## 0.42.0

A code-review prompt longer than the critic's vendor accepts is refused before the critic starts,
and the orchestrator has two ways to narrow the round. In concurrency-hunter run
`20261004-093111-b79938`, a fourth round the user had granted reached codex at 1,056,671
characters, and codex refused it at its limit of 1,048,576 after the process had started: the round
came back as a vendor failure carrying only codex's stderr, and the orchestrator moved the plan's
gate script out of the repository for the length of the review to make it fit. See
[docs/adr/0029](docs/adr/0029-refuse-an-oversized-review-prompt-and-let-the-orchestrator-narrow-it.md).

- Each vendor states the most characters its CLI accepts as one prompt, counted as Unicode scalar
  values: 1,048,576 for codex, which counts the stdin prompt alone; none for claude, whose limit is
  the model's context in tokens, or for cursor-agent. A code-review prompt over it is refused with
  its size, the limit, what it holds — tracked diff, untracked files, plan, ledger projection — and
  its ten largest files. Nothing is counted, a user's grant still stands, and the flow log records
  the round as not sent.
- `forge.review.code` and `forge.work.start` for `review.code` take `excludePaths`: paths or git
  pathspec patterns, relative to `workspaceRoot`, that leave this round's window. The changed paths,
  the content diff and the sensitive-path guard share them, and the critic and the critique summary
  name them.
- Both take `untrackedByReference`: the window's untracked files are listed with their line counts
  for the critic to read from the working tree instead of embedded. Their contents still pass the
  secret guard. Replayed over run `20261004-093111-b79938`'s tree, it took the prompt from 1,026,655
  characters to 572,430.
- The review window names tracked files relative to `workspaceRoot`, as untracked files and
  pathspecs already were, so a name the refusal lists can be handed back to `excludePaths`. The
  critic's diff is unchanged.
- The forge skill asks the user how to narrow an oversized round, recommends
  `untrackedByReference` when the untracked files are the run's work, keeps `excludePaths` for what
  is not, and rules out moving files out of the repository.
- The run-wide gates a review fix is checked against run for up to 40 minutes; a task's gate keeps
  its 20. In concurrency-hunter run `20261005-204040-0c34b6` the plan's final gate took 25 to 31
  minutes, so all three review rounds' fixes came back as a gate timeout and were verified by hand.
  The call that carries the fix keeps its one-hour limit, so a long builder turn and a long gate can
  still outlast it together.

## 0.41.0

A code-review fix batch is framed by the rules its findings break, so a domain's axes reach the
Builder from the one participant that knows them. Over nine measured runs, 53% of the findings after
the first code-review round were caused by an earlier round's fix. The generic "When you fix review
findings" section of 0.38.0 lifted the recall of later-found places in an offline replay from 32% to
57% for claude and from 23% to 46% for codex, and cost codex new defects, 0.17 to 0.75 per turn.
The orchestrator ran the interview, wrote the plan and read every critique: in concurrency-hunter
run `20261004-093111-b79938` its later fix notes stated numbered rules with their sources and axes,
and one wrong note — it had the Builder drop a guard that was a requirement's own rule — showed why
a rule has to cite where it comes from.

- The forge skill forms fix batches by rule. One rule's findings never split across batches; a
  batch may carry several rules, and rules whose failure should not hold the others back are kept
  apart, since every fix call runs the plan's `## Gates`. A finding another batch's fix covered is
  closed with `hostVerified` and its evidence, because `duplicateOf` needs a canonical ID still in
  the ledger.
- The first call of every fix attempt carries a `note`. For each rule: the rule with its source — a
  requirement, a quoted task sentence, since a fix prompt carries no task text, or a decision the
  user made during the run; its owner, which every other place calls; and its axes, taken from the
  plan's matrix check and owner tasks first, then from the kinds a plan tends to miss, to be found by
  references and callers rather than by searching for a word. Once for the batch: what must not
  change and when to stop. A part that does not apply says so, and the note does not repeat the
  findings. A retry's note says what changed and restates only a rule it corrects; a retry sent to
  another vendor gets the whole note again. The skill shows one example in a neutral domain.
- After each code-review round the orchestrator records every new finding as `own`, `sibling`,
  `same-class` or `older` with `forge.log.append`. Over the 150 findings of the nine measured runs,
  `own` and `sibling` — the fallout — came to 53%, and with `same-class` to 77%; these lines are the
  measure for the runs that follow.
- `forge.review.fix` and `forge.work.start` describe `note` by those parts, and so does the
  glossary's Fix note. The server still requires no note and checks none of it.

## 0.40.0

A rule the plan introduces or changes is checked for a matrix while the plan is drafted, rather than
after rounds of review have found its combinations one at a time, and the orchestrator asks the user
one question at a time, each explained. In one measured plan review the rounds ran 28, 17, 15, 11,
12, 14 and 9 findings, and from the third round on nearly every finding was a combination of one
rule's axes; the matrix arrived after the fifth round, and the next critic found an error in its
expectation table.

- Before the Impact pass and the first `forge.plan.write`, the forge skill lists every rule the plan
  introduces or changes with the axes its answer depends on, and puts a matrix task into the first
  draft when the answer depends on two or more axes with about six values between them, the plan
  lists more than about ten of its cases, a neighbouring rule already has a matrix, or a missed cell
  would be a silent fault. The first time the plan is shown, one line says which rules were checked
  and what was decided.
- A matrix task is a task of its own with a test class in the ordinary suite. It names its axes,
  its excluded combinations with their reasons, an expectation table written from the requirements,
  and the cells known to be wider with their reasons, each of which becomes an open question in the
  project's documents; its tests check unsafe narrowing, every expected answer and every axis value.
  It replaces the one-by-one cases of the same rule elsewhere in the plan.
- From the second round on, when more than half of a round's new findings are combinations or edge
  cases of one rule that has no matrix, the orchestrator adds one in that revision and says so.
- The plan-review critic reports the combinations of one rule's axes as one finding that lists them
  all and recommends a matrix task, rather than a finding per combination.
- Every question to the user is one question per call, in dependency order, overriding the
  `grilling` rule to ask the whole frontier in one round. Each comes with an explanation in the
  chat — the problem in plain words, an example from the repository for a question about code
  behaviour, and every option's effect, advantages, drawbacks and cost — and the question lists the
  recommended option first as "(Recommended)". Two batches are made unasked, one per worker role:
  the critic's vendor, model-and-effort, Fast and instruction questions share one call, and the
  builder's another, both built from the catalogue fetched before the first. Each model option names
  its vendor and the Fast question the models that offer it; answers that do not fit together get
  one follow-up question. Gate values are asked one variable at a time; a user who asks for a batch
  gets one.

## 0.39.3

- Codex Builders run without the Codex sandbox or approval prompts on fresh and resumed turns,
  including implementation and review fixes (docs/adr/0028). Critic and Scout retain `read-only`.
- `builderRoots` remains accepted and persisted for compatibility, but no longer produces a Codex
  writable-roots override. The forge skill no longer requires the Orchestrator to perform edits
  that the Builder's sandbox would refuse.

## 0.39.2

- Worker telemetry records process `wallDuration`, the union of matched tool-call intervals as
  `toolDuration`, and their difference as `duration` (issue #135). Missing or incomplete boundaries
  leave `toolDuration` null and retain wall time as `duration`. `at` is now local process send time
  in `yyyy-MM-dd HH:mm:ss`; old files are not migrated. The meanings of `duration` and `at` change.

## 0.39.1

Executable task gates run only on the server, avoiding a duplicate run by the Builder (issue #134,
docs/adr/0027).

- The Builder receives the server's classification of the task gate and leaves executable gates,
  including retries, to the server. Separate targeted checks are optional and do not prove the gate
  passed. When no such checks run, verification is `unavailable` with an explicit reason.
- Condition gates remain the Builder's responsibility, with failed or unavailable checks handled by
  the Orchestrator. Server gate failures retain the existing retry behavior, and the schedule for
  plan-wide gates is unchanged.

## 0.39.0

The Builder no longer keeps one session for the whole Run (issue #129, docs/adr/0026). On claude
that session grew past 900k tokens, compacted the Builder Brief into a summary, and cost about 38%
more than a session per task would.

- Each plan task and each code-review round starts a fresh Builder session, with the Builder Brief
  first and the user's instructions last. Only a retry inside the same task or round resumes: a gate
  failure, a `blocked` answer, a cut-short or killed turn, and a round's later fix calls. The rule is
  the same for every vendor.
- A fresh task session is told which files each earlier task changed, by task number. The run state
  records them as `taskChanges` and the session's scope as `builderSessionScope`; `forge.status`
  shows both. A reopened plan clears them with the progress.
- `forge.instructions.set` says that changed instructions reach the next task or round.
- A run begun on an earlier version starts a fresh session at its next builder turn.

## 0.38.0

About half of the code-review findings after the first round were fallout of the previous round's
fixes, measured over nine runs: a rule written at the one place a finding named, a sibling place
never updated. A finding is now handled as the rule it breaks (docs/adr/0025).

- The Builder contract gains "When you fix review findings": name the rule, list every place that
  answers the same question by reference search, fix them all in the turn through one function,
  test one input the finding did not name and one the rule must leave alone, and report each place
  as `fixed`, `already agreed` or `left: <reason>`. "The minimum needed" stays the rule for plan
  tasks.
- `prompts/roslyn-contract.md` is appended to the Builder too, reworded for every role; builders
  without it made no Roslyn call in 38 fix turns.
- `forge.review.fix` and `forge.work.start` take an optional `note` for a fix attempt. The Builder
  reads it after the verbatim findings under "From the orchestrator", before the user's
  instructions, and the Flow log records it verbatim. A note without fix IDs is refused.
- A code-review decision batch may carry `raises`: findings the orchestrator puts into the ledger
  itself, such as a place a fix left behind. Each takes the next finding ID, and the next Critic
  sees it marked `raised` and has to assess it. The call answers with `raisedFindingIds` on both
  paths. Plan review refuses raises, and a batch with raises carries no fix attempt. A batch without
  raises keeps its old canonical bytes and digest, and existing ledgers read unchanged.
- The forge skill turns a Builder's `left:` places into raises, stops asking for rounds when half a
  round or more is fallout and sweeps the touched rules' axes first, and asks a plan task that changes
  a rule several places decide to name its owner, those places and its input axes. The plan-review
  Critic holds the plan to that, and the Impact pass asks for the places.
- The fix prompt no longer carries its "Fix these review findings" heading twice.

## 0.37.2

A resumed Builder or Scout turn recorded everything its session had spent so far rather than its
own usage, and a claude turn left out what its subagents and context compaction used (noticed
while measuring issue #129, docs/adr/0024).

- Codex reports every token counter for the whole thread, and Claude its `total_cost_usd` for the
  whole session. A resumed attempt's `vendor.usage` record now carries what that total grew by
  since the session's latest recorded report: a codex fix turn once recorded as 81,766,668 input
  tokens is recorded as the 8,779,454 its own calls used.
- Claude's tokens come from the result line's `modelUsage`, summed over its models, instead of its
  `usage`, which covers the main loop alone. A Scout that ran four subagents is recorded with
  24,602,476 input tokens instead of 415,302, and a turn that auto-compacted with the compaction's
  967,032 more. `modelUsage` is a running total too, and a result that names no model records no
  tokens rather than zeros.
- A resumed codex or claude record adds `sessionTotal`, the running total the Vendor reported, so
  the next attempt counts from it.
- A running total below the previous report is named in `malformedUsageFields` and omitted.
- A codex turn cancelled before it reported still spends into the thread, and the next reported
  turn of that thread carries it. Cursor's counters on a resumed chat are unmeasured and recorded
  as reported.

## 0.37.1

A source file whose name merely contained a secret keyword, or began with `id_`, refused the whole
code-review round — the false positive issue #88 fixed for content lines, on paths.

- The keyword rule (`secret`, `token`, `password`, `credential`) no longer applies to
  programming-language source files, so `CancellationTokenExtensions.cs`, `PasswordPolicyTests.cs`
  and `token_cache.py` pass. Their lines still go through the content check, which reads a secret
  the way code writes one. Every other file keeps the rule, a keyword in a directory included:
  `tokens.json`, `api-token` and `secrets/prod.json` are still refused.
- The SSH key rule `id_*` takes the same exemption: `id_generator.py` and `id_mapping.cs` pass,
  while `id_rsa`, `id_ed25519`, `id_github`, `id_rsa.bak` and `id_rsa.pub` are still refused.
- What stays refused on purpose: a data, documentation, project or script file named with a keyword
  or starting with `id_`, such as `id_mapping.json` or `docs/token-rotation.md`.

## 0.37.0

The Scout finds what a plan breaks, and its whole answer reaches the orchestrator (issue #117, docs/adr/0021).

- Every plan gets an Impact pass before its first `forge.plan.write`. The skill supplies a Scout
  question template and an Evidence check, and recommends the strongest catalogue combination at
  high effort.
- Scout's contract requires a sixth category, `dependentsAndPinnedBehaviour`, with a sourced
  "none found" item when a location has no dependents. Names must appear at their cited location,
  and the answer must include every relevant fact read. The shared Roslyn contract, used by Scout
  and Critic, directs usage claims to reference and caller queries.
- `forge.scout.run` and Scout background jobs return the complete answer. The bounded digest and
  `truncated` field are gone.
- `SCOUT.md` accumulates numbered answers with their questions. Numbers come from the new
  `run.scoutAnswers` state field. A call that fails before appending leaves the report unchanged;
  cancellation no longer claims in the Flow log that the report was replaced.
- Scout questions can be up to 8,000 characters long.

## 0.36.0

A worker can run at its vendor's Fast tier (issue #116, docs/adr/0023).

- Every tool that takes `effort` takes an optional `fast`: `forge.plan.review`, `forge.build.next`,
  `forge.review.code`, `forge.review.fix`, `forge.work.start` and `forge.scout.select`. A Scout
  selection keeps its speed with it.
- `forge.models` lists, per model, the efforts it offers at Fast speed (`fastEfforts`), the
  vendor's price hint (`fastHint`, codex), and why the account will not serve it
  (`fastUnavailable`, claude). Codex reads the tier from `codex debug models`. Claude's probe asks
  each alias's `init` under `--bare` and the account once, killing both before any API call.
  Cursor's `-fast` ids become their own axis rather than efforts such as `high-fast`.
- A Fast request the catalogue does not confirm is refused before any worker starts. That covers a
  model it does not list and a vendor whose probe failed. Claude also refuses at its session's
  `init` when the account will not serve Fast. A Claude turn that falls back to standard speed
  part-way still counts: its result carries `speedWarning`, and the flow log and run log say so.
- Every turn names its speed. Codex is sent `service_tier="default"` when Fast is off, so the
  desktop app's Fast toggle in `~/.codex/config.toml` no longer reaches forge workers. Claude is
  sent `"fastMode": false` in the same `--settings` payload.
- A cursor model or effort that already carries `-fast` is read as a Fast request.
- `telemetry.json` records the requested `fast` for every attempt and claude's served
  `fastModeState`.
- The skill asks a separate Fast question per role, only where the catalogue offers it, and names
  the price hint.

## 0.35.4

A decision can be the user's whatever its action (issue #113).

- `by: "user"` is accepted on `addressedByRevision`, `duplicateOf`, `hostVerified` and `accept`, as
  it already was on `defer`, `reject` and `decline`, in every tool that takes decisions. `by` names
  who made the choice; the Orchestrator is still the only one that sends it, and the server still
  refuses any value but `user` and `orchestrator`.
- A reopening the user accepted is stored and loaded with `user`, and the next Critic reads it that
  way. Ledgers written by 0.35.3 load unchanged.
- The skill says when a decision is the user's: when their answer settled that finding or how it
  was addressed. Approving the plan as a whole does not count.

## 0.35.3

Worker telemetry records what a Claude attempt cost.

- A Claude worker's `vendor.usage` record carries `costUsd`, the `total_cost_usd` the CLI reports on
  its result line. Codex and Cursor report no price, so their records omit the field.
- A cost that is not a non-negative number is named in `malformedUsageFields`; the token counters
  are kept.

## 0.35.2

Run files are readable by a person again.

- `decision-ledger.json` moves to schema version 2: an applied decision batch keeps only the SHA-256
  digest of its canonical decisions and its result, instead of base64 canonical bytes. Loading no
  longer re-verifies batches against their payload. Version-1 ledgers are not read, so finish an
  in-flight run on the previous version.
- `flow_log.md` lists each decision as `- F-0001 defer (orchestrator): reason` instead of a raw
  canonical JSON line; a conflict shows the saved batch's IDs. The conflict error no longer carries
  the saved decisions.
- `decision-ledger.json`, `state.json` and `forge.log` write non-ASCII text as written rather than as
  `\uXXXX` escapes.

## 0.35.1

Worker telemetry now gives `inputTokens` the same total-input meaning for Claude, Codex and Cursor
(issue #108).

- Claude and Cursor add cache reads and cache creation to their uncached input counters, while Codex
  preserves its provider-reported total instead of subtracting cache counters.
- Cache counters remain optional subsets of `inputTokens`. Missing, malformed, overflowing and
  inconsistent source values retain independently valid counters without producing a negative or
  wrapped total.

## 0.35.0

Critic convergence now uses a canonical Run-local decision ledger instead of replaying a growing
review transcript (issue #101).

- `decision-ledger.json` assigns monotonic finding IDs, persists only unresolved, deferred and
  rejected dispositions, and records typed decisions, reopenings, closures and retryable fix
  attempts with atomic replacement and strict validation.
- Each fresh Critic receives only the bounded projection for its current phase. The Orchestrator is
  the sole authority for dispositions and reopening decisions, while the Builder receives exactly
  the ledger findings selected for one fix attempt.
- Direct and background review paths share idempotent `decisionBatchId`, `fixAttemptId` and
  `fixFindingIds` contracts. Exact retries are safe, conflicting retries are refused, and successful
  host gates close only the findings belonging to that attempt.
- `review-log.md` is removed. Flow remains the user-facing audit, while status, prompts, tool
  schemas, documentation and package validation reflect the ledger model.

## 0.34.0

Builders now receive the approved plan context above `## Approach` as a Builder Brief once per
session, before the task or review findings. A fresh session receives the Brief and the user's
instructions again after a Vendor switch, a reopened plan, or direct re-approval after a changed
Builder Brief; resumed sessions continue from their existing context.

- The Brief is guarded separately for sensitive input, while task and findings guards keep their
  existing source labels and line locations. Transient interview notes, rejected alternatives,
  secrets, and host values stay out of the Vendor-bound context.
- Tasks remain standalone, change-specific deltas with their own gates and requirement references;
  they no longer need to restate stable plan context, and the Brief is context rather than work or
  authority to revise the approved plan.

## 0.33.1

Codex workers installed through npm on Windows now launch `node.exe` with the package's `codex.js`
entry point instead of forwarding prompt-bearing arguments through `codex.cmd`. This prevents
`cmd.exe` from interpreting prompt characters such as `<` before Codex starts. Native `codex.exe`
installations continue to launch directly.

## 0.33.0

Plan Forge can now delegate broad repository reconnaissance to a lazily selected, read-only Scout
without moving requirements or planning out of the Orchestrator (issue #102).

- New `forge.scout.select` and `forge.scout.run` tools keep Scout's Vendor, model and effort
  independent of the Critic and Builder, or record an explicit decision to continue without Scout.
  A failed call never silently changes Vendor or model.
- Every Scout question is bounded and must explicitly choose a fresh session or continue the
  existing investigation. The Orchestrator makes that choice; the Scout may use the internet and
  the Run's granted read-only Worker tools, but cannot edit, plan, judge, or settle requirements.
- Successful calls return a compact sourced digest and atomically replace `SCOUT.md` with the full
  report. Repository evidence cites paths and lines or symbols, external evidence cites URLs, and
  the existing sensitive-input checks cover both questions and reports.
- Claude, Codex, and Cursor all support the role through their existing read-only launch boundary.
  Scout also participates in background jobs, status, flow logging, and Worker usage telemetry;
  the MCP surface now contains eighteen tools.

## 0.32.0

Every Critic and Builder process launch now leaves a human-readable usage record in the Run's
`telemetry.json` (issue #99). The indented JSON array identifies the Act, task or review round,
requested Vendor selection, session and turn, outcome, duration and Plan Forge-authored prompt
bytes, followed by whatever terminal token counters the Vendor actually reported.

- Claude, Codex and Cursor terminal events are normalized into non-overlapping uncached input,
  cache read, cache creation and output counters, with reasoning retained as an optional subset of
  output. Missing values stay absent; malformed values are omitted by safe source path while valid
  independent counters survive.
- A Plan Forge `turnId` groups process attempts, including Cursor's structured-output retry, while
  the Vendor's optional `sessionId` continues to name its resumable conversation. Outcomes keep
  successful, failed, invalid-output and cancelled attempts distinct.
- Duration is monotonic `hh:mm:ss`; the terminal timestamp uses local time and its UTC offset.
  Telemetry stays best effort, contains no prompts or output, and never changes the Worker result.
- The file is updated under a process-local gate and atomically replaced. An existing malformed
  file is preserved, with a safe `telemetry.write.failed` entry in `forge.log` instead of data loss.

## 0.31.0

The user can now tell this run's workers something of their own. Until now there was no channel but
the plan: a critic saw the plan or the diff, the review log and its role contract, and a builder saw
`# Task N of M` and nothing else, so "answer in Russian" or "use the ponytail-net skill" had to be
copied into every task or written into `prompts/` — which is a per-project knob only in a checkout,
never in an installed plugin (issue #95).

- New tool `forge.instructions.set` records one free text for the critic and one for the builder,
  asked for at the end of the interview and kept in the run state. An omitted argument leaves that
  role alone; an empty string clears it. Both come back in `forge.status`, so an orchestrator whose
  context was compacted can read them again.
- The text travels in the **act prompt**, not the role prompt: every vendor carries the user turn on
  every call, while a resumed codex thread keeps the `developer_instructions` its thread started
  with. `PromptLibrary` and the three vendor implementations are untouched.
- A critic is fresh every round and is handed its text every round. A builder is handed its text by
  the call that **starts** its session — no resume token, which a vendor switch and a reopened plan
  also produce — and never by a resumed one. Setting builder instructions while a session is running
  answers with a note saying that session will not see them, rather than refusing the call.
- A code-review critic is additionally shown the builder's text as data, framed as context for
  judging the diff: a builder told "the simplest solution that works" should not draw findings for
  the abstractions it left out on purpose. Plan review never carries that section.
- Both texts are guarded for secrets before anything is recorded, and both go verbatim into
  `flow_log.md` and `forge.log`. Nothing checks that the text is the user's own rather than the
  orchestrator's — the hole `approved` has, and the reason the timeline entry exists. See
  `docs/adr/0019`.
- A run that sets no instructions composes exactly the prompts it composed before.

## 0.30.3

A builder turn the host cuts short is now recorded instead of vanishing. Round 5 of run
`20260917-111319-20e672` measured the loss: `forge.review.fix` was cancelled 3600.11 s in — the
hour `.claude-plugin/plugin.json` sets on itself — after the builder had finished its edits,
rewritten the baseline and run the plan's gates green. Nothing was written: no timeline entry, no
review-log entry, no state, no gate of the server's own. Only the edits survived, because they were
already on disk, and the orchestrator had to read the tree by hand (issue #96).

- `forge.review.fix` and `forge.build.next` now write the turn down before the cancellation travels
  on: a `cut short` entry in the flow log naming the files git says the builder wrote, the round's
  fixes in the review log under a `— cut short` heading telling the next critic to judge them
  against the tree, the builder's resume token in `state.json` so the retry continues the
  conversation instead of starting cold, and a cut-short brief for the next turn saying what is on
  disk and that the call ends at a deadline the worker cannot see.
- A cut-short task is not counted, and no gate runs for either act — the tree is mid-edit and the
  token that would run one is already cancelled. Nothing is reconstructed from the worker's
  narration: that builder called its gates green and then changed files for another half hour.
- The cancellation is still a cancellation. The act fails, `forge.log` still records
  `tool.cancelled`, and the work itself is still lost — saving that needs the worker detached from
  the request, which is open in issue #96. `docs/adr/0006` is amended where it said the ceiling
  "belongs to Cursor alone".
- `forge.log` gains `builder.cut-short` with the file count and names.

## 0.30.2

A working codex is no longer reported unavailable because `codex doctor --json` exited non-zero.
Measured against codex 0.154.0: the doctor exits `1` when any check fails, and on this machine the
only failure was `sandbox.helpers`, while `codex exec` answered normally.

- The codex probe now judges the doctor's `checks`: `auth.credentials` must be `ok`, and
  `installation` and `config.load` must not be `fail`. Those still make codex unavailable.
- Any other failed check leaves codex available and is named in the readiness detail, for example
  `5 models; codex doctor warns: sandbox.helpers failed: …`.

## 0.30.1

A codex `error` line no longer counts as a failure. Measured against codex 0.154.0: warnings and
notices (such as the skill-budget notice on a run that succeeded) come as an `item.completed` of
type `error`, and a top-level `error` looks the same whether or not the server will retry. A real
failure always ends with `turn.failed`, which repeats the last error's message when the turn has
none of its own.

- Only `turn.failed` is now `vendor.failed` in `forge.log`, and only it names the reason when codex
  writes no result. A usage limit is therefore logged once as `vendor.failed` instead of twice, and a
  notice arriving after it can no longer replace it in that message.
- Both `error` shapes are logged as `vendor.warning` at level `warn`. The item shape used to go
  unlogged.

## 0.30.0

Run `20260916-134641-21e3d5` showed two ways a headless worker loses work without saying so: its
claude builder was refused every Roslyn call because nothing had granted it (issue #90), and it
started a ten-minute grid in the background, answered, and the session killed the grid with it
(issue #91).

- `forge.begin` takes an optional `workerTools`: patterns naming the MCP servers every critic and
  builder of the run may call without being asked, `roslyn-*` when omitted, `[]` for none. Each
  launch lists the vendor's servers from the worker's directory and grants the matching ones by
  exact name — `--allowedTools mcp__<server>` for claude, `-c
  mcp_servers.<server>.default_tools_approval_mode="approve"` for codex — because claude refuses a
  pattern in a server name and codex refuses to start on a server it does not know. Cursor's
  `--approve-mcps` already grants everything. Every launch writes `worker.tools` to `forge.log`
  with the patterns and the servers granted; a lookup that fails is logged and the worker starts
  without grants. See `docs/adr/0017`.
- A claude builder is now granted `Bash` and `PowerShell` outright. Under `acceptEdits` alone a
  headless builder on a machine whose own settings approve nothing could run no check at all, and a
  pattern rule does not lift claude's safety checks the way a blanket rule does. A critic still gets
  no shell.
- A claude session now follows the tasks claude runs for the model. A turn that ends with one still
  running in the background comes back as `background_killed`, naming the command in `gate.detail`;
  no gate runs, the task is not counted, and the next attempt is told what was killed and to run it
  in the foreground. A critic that does the same keeps its critique, and the flow log says what it
  never saw the end of. `vendor.task` entries in `forge.log` record every start, kill and finish,
  which a run whose process exited 0 used to drop unlogged. Detection rather than a deny rule,
  because backgrounding inside a turn is legitimate and Claude Code itself steers the model there —
  see `docs/adr/0018`.
- Every claude worker runs with `BASH_MAX_TIMEOUT_MS` at 30 minutes, so a long command has a
  foreground way to run; a foreground call emits a heartbeat every 30 s, so the idle reaper does not
  interfere. The builder and critic contracts now say that the session ends at the final answer.
- A codex worker's MCP calls reach `forge.log` as `mcp__<server>__<tool>` tool events, with the
  refusal text when codex refused one.
- `CONTEXT.md` records the measurements behind all of this, against Claude Code 2.1.273 and
  codex-cli 0.154.0.

## 0.29.1

A gate longer than roughly 12,000 characters never ran at all, and the task it guarded was counted
anyway on the builder's own verification — the one outcome `docs/adr/0015` exists to prevent.

- The gate command now travels as a temporary `.ps1` run with `-File`, in place of `pwsh
  -EncodedCommand`. Base64 of UTF-16 is about 2.7 characters of argument per character of script and
  Windows caps a command line at 32,767, so a gate command of 11,930 characters started and one of
  11,931 did not. Run `20260915-143837-8bc4d1` lost a 14,824-character gate that way, reported as
  `gate.outcome = "not_run"` with "the filename or extension is too long", which leaves the
  builder's self-report standing. Nothing of the gate reaches a command line now, so no length
  applies and no quote, dollar sign or newline can break it either.
- Everything the exit code depends on is unchanged: the `Stop` preference, the native-error
  preference, the trap that writes before it exits, the final `$LASTEXITCODE`, the working
  directory, the environment, the timeout and every `gate.start` / `gate.finished` field. The script
  is UTF-8 with a BOM, which is what Windows PowerShell 5.1 needs to read a gate naming a non-ASCII
  path or test, and it is deleted after the run.
- A temp directory that cannot be written is now its own `not_run`, logged as `gate.no-script`,
  instead of being reported as a shell that would not start. A gate now also sees `$PSCommandPath`
  and `$PSScriptRoot`, which were empty under `-EncodedCommand`.
- `CONTEXT.md` records the measured ceiling, and `docs/adr/0015` carries an amendment covering what
  the base64 decision cost and why a gate the host cannot run still leaves the builder's word
  standing — which is what turned this defect from a failed gate into a false `done`, and is left
  open deliberately.

## 0.29.0

Critics and builders that were still making progress stopped after a fixed 20-minute vendor window,
while a truly silent worker gave the orchestrator no evidence to distinguish slow work from a hang
(issue #85).

- Every Codex, Claude, and Cursor worker attempt now has no fixed wall-clock limit and is stopped
  only after 30 minutes without a stdout line; every line resets the idle window, recognised or not,
  and the diagnostic kill reason is `idle`.
- `forge.work.poll` and `forge.status.activeJob` report `lastActivityAt` and a bounded `lastEvent`,
  and terminal job persistence keeps both fields.
- New `forge.work.cancel` requests cancellation without blocking, treats a terminal job as a
  successful no-op, and persists a cancelled running job as failed. The skill reserves it for an
  explicit user request or a confirmed decision after showing the liveness fields.

## 0.28.2

A codex builder in a run over a plugin marketplace updated the Claude and Cursor catalogues and could
not write `.agents/plugins/marketplace.json`, so the orchestrator added that entry itself — after the
builder's turn, and without the plan having said anyone would.

- The `forge` skill now tells the orchestrator that a codex builder cannot write `.git`, `.codex` or
  `.agents` at the top of the workspace, that `builderRoots` does not change it, and that such an
  edit is planned as the orchestrator's: made on the host before the task's `forge.build.next`, so
  the gate runs against it, and recorded with `forge.log.append`.
- `CONTEXT.md` records the measurement against codex 0.153.2: the three names are refused only at
  the top of the workspace, naming one in `writable_roots` leaves it refused, and the same names
  deeper in the tree, and `.codex-plugin` anywhere, stay writable.

## 0.28.1

A code-review round compared the working tree with `HEAD`, so an Orchestrator that committed a
finished phase mid-run moved that work out of the Critic's view. A clean tree then came back
`approve` with `nothing to review`, even though the run's committed work had never been judged.

- Code review now compares the commit recorded by `forge.begin` with the current working tree while
  that commit remains an ancestor of `HEAD`. Committed, staged, unstaged and untracked work therefore
  reaches the Critic as one final-state diff, still excluding `CONTEXT.md` and `docs/adr/**`.
- A rebase, amend or branch switch that makes a valid baseline unrelated falls back to a resolved
  current `HEAD`; a missing baseline stops with an instruction to begin a new run. Both the Critic's
  prompt and the returned summary identify the actual window and warn when it is a fallback.
- The sensitive-path guard and content diff now come from one `ReviewWindow`, so both use the same
  base commit and pathspec. The preparation remains lock-free, preserving the run's tolerance for
  concurrent working-tree movement. See
  [docs/adr/0016](docs/adr/0016-review-the-final-tree-from-the-run-baseline.md).

## 0.28.0

Every Codex and Claude critic and builder inherited the host's Plan Forge Flow plugin, including
its MCP server and `forge` skill. The prompt told a worker not to orchestrate, but its capability
surface still let it start a run inside its parent run.

- Both Codex roles now disable the complete canonical
  `plan-forge-flow@dlin10-ai-coding-plugins` plugin for every turn. Other plugins, MCP servers and
  user configuration remain available to the worker.
- A Codex critic now runs with `--ephemeral`, so its deliberately fresh review rounds do not leave
  resumable sessions in the host's history. A builder remains persistent and resumes across tasks.
- Both Claude roles disable the same complete plugin through a one-process settings override while
  inheriting every other capability. A critic uses `--no-session-persistence`; a builder remains
  persistent and resumes across tasks.
- Cursor has no per-process plugin-disable flag, so both roles mark their child environment and the
  self MCP launcher exits before starting `planforge.exe`; unrelated plugins and MCP servers remain
  untouched. The explicitly invoked `forge` skill is now manual-only rather than advertised to
  worker models.

## 0.27.2

A prompt audit of everything this plugin puts in front of a model. Three of the fourteen tools
described themselves in a single sentence while the other eleven carried their whole contract, so
the rules that separate a rejoin from a retry lived only in `SKILL.md` — fine for an orchestrator
reading the skill, invisible to one that is not.

- `forge.work.start` now says it is the Cursor route, that the act's result arrives from
  `forge.work.fetch` rather than from it, that `started: false` means rejoin the returned `jobId`
  instead of starting a second worker against the same run, and that a job id does not outlive the
  server process that issued it.
- `forge.work.fetch` names what it returns and says it must follow a poll that came back in a state
  other than `running`.
- `forge.status` lists what it actually carries — rounds against their caps, `approved`,
  `tasksCompleted`, the profile, `driftedFiles`, and any `activeJob` — because the skill tells the
  orchestrator to read those numbers from it before asking the user to approve or to grant a round.

Text that had outlived its own change is gone from the prompts. `prompts/roslyn-contract.md` opened
with two lines about replacing two drifted per-host copies, and `PromptLibrary` appends that file to
every critic prompt, so a note about this repository's prompt refactoring reached a worker whose job
is judging C# semantics. In `skills/forge/SKILL.md`, "the old rule" and "as it was before" were
written as a diff against the skill as it stood before the host ran the gates, and pointed at a rule
the reader has never seen. The reasoning stays where it belongs, in `docs/adr/` and `AGENTS.md`.

No behaviour changed: the descriptions are what a host reads, and the prompt edits removed only
sentences about the documents themselves.

## 0.27.1

A builder turn came back as `claude.exe exited 1: ` — an exit code and an empty colon. The claude CLI
blocked by an individual spend limit says so on stdout and exits 1 with nothing on stderr, and
`StreamingProcess` hands stdout to its caller line by line without keeping any of it, so the one
sentence that explained the run was gone by the time the failure was reported. Learning it cost a
reproduction of the vendor call by hand (run `20260905-144900-e42174`); the codex CLI had shown the
same shape earlier.

- A non-zero exit whose stderr is blank is now reported with the tail of what the process wrote on
  stdout, and names which stream it is quoting. Where stderr speaks it still wins alone, because a
  caller holding a copy of stdout — the gate runner — would otherwise be handed its own output a
  second time. Both tails are bounded now; the stderr one used to reach the message whole.
- `process.exit` and `process.kill` carry a `stdoutTail` beside `stderrTail` in `forge.log`, so a
  builder turn killed at its timeout leaves a record of what it was writing rather than only the
  fact that it stopped.
- This covers all three vendors. Claude, codex and cursor reach their CLIs through the one runner
  and none of them inspected a non-zero exit itself, so none needed a change of its own.

The same run stalled a second way. A task could not finish once its gate had failed and its builder
then answered `blocked`: the gate ran only for a `done` report and the stored failure cleared only on
a gate that passed, so a builder that fixed the fault but could not prove it — the codex sandbox
cannot reach SQL Express, and three of task 13's nine gated tests need it — was handed the superseded
failure on every attempt and blocked again, the last time changing no files at all. The gate an
orchestrator eventually ran by hand passed, after four turns that counted nothing.

- The host now runs the gate for a `blocked` report whose `verification` is `unavailable`, and the
  exit code decides in both directions: a gate that passes rewrites the status to `done` and counts
  the task, a gate that fails is `gate_failed` as before, and either way the next attempt is briefed
  from this run rather than an older one. `verification` still carries what the builder could not
  check, and the flow log prints it beside the gate. A `blocked` report whose verification `failed`
  is untouched — there the builder ran the check itself and watched it fail. See
  [docs/adr/0015](docs/adr/0015-the-host-runs-the-gate.md).

## 0.27.0

A builder answered `done` and `verification: passed` for six tasks whose gates named the tests each
had to add, and wrote none of them: the suite it ran was the old suite, green at the old count. The
gates that needed the host — a SQL Express connection string, a sibling checkout, a `NuGet.Config`
the sandbox may not read — came back `unavailable`, and the skill's "run it yourself" ran once, after
the last task, which is where every defect of tasks 3 to 6 surfaced (run `20260904-173914-9254ec`).

- After every `forge.build.next`, the server runs the task's gate command itself: the inline span or
  fenced block right after `**Gate:**`, in PowerShell, from `workspaceRoot`, with the environment
  the approval carried, under a twenty-minute bound. Its exit code decides the task. A non-zero exit
  comes back as status `gate_failed` with the command, exit code and output tail under
  `build.result.gate`; `tasksCompleted` does not move, and the next call retries the same task with
  the gate's own words in front of the builder. A passing gate counts the task whatever the builder
  said about its own verification. A gate that opens with prose is a condition, not a command: the
  builder's word stands as before and the flow log says `Gate: not executable`.
- After every `forge.review.fix` with findings, the same happens with the executable entries of the
  plan's `## Gates` section, run as one script in order and reported under `fix.gate`.
- `forge.plan.confirm` takes `gateEnvironment`, the variables the gate commands need, and
  `builderRoots`, absolute paths outside the workspace the builder may write to. Both are kept in the
  run state — only the variable names are logged — and a re-approval replaces them. A codex builder
  receives `builderRoots` as `-c sandbox_workspace_write.writable_roots=[…]`; claude and cursor
  ignore it.
- The flow log carries a `Gate:` line beside `Verification:`, with the output tail when the gate did
  not pass; the run log carries `gate.start` and `gate.finished`.
- The skill now writes every task gate as one PowerShell command line placed first after
  `**Gate:**`, and asks for the gate environment and builder roots at approval. For a task that adds
  tests it recommends proving they exist by name — `dotnet test --list-tests` piped through
  `Select-String` with an expected count — because "the suite is green" cannot tell "the test passed"
  from "the test was never written". See
  [docs/adr/0015](docs/adr/0015-the-host-runs-the-gate.md).

## 0.26.1

The 0.26.0 release did not publish: its own CI run failed on
`BaselineTests.An_untouched_tree_has_not_drifted`, where `git rev-parse HEAD` exited 0 and returned
nothing, so a baseline was captured with an empty head. The runner was slow enough for that one test
to take fifteen seconds, and the post-exit drain in `StreamingProcess` allowed the pending read two.

- The stdout drain after a process exits is five seconds rather than two. It exists to bound an
  inherited handle holding the pipe open, but it bounds the machine's own scheduling too, and two
  seconds was inside what a loaded runner can take to deliver a line that had already been written.
  Five is the compromise: several times any ordinary scheduling delay, and still inside the ten
  seconds `DiagnosticLogTests` allows a process whose child keeps the pipe — the guard on the
  twenty-minute wait that ending the stream on the exit was written to remove.
- An expired drain now writes `process.drain.timeout` to the run log. A truncated stream is
  otherwise indistinguishable from a complete one, which is how an empty `rev-parse` reached a
  baseline as an answer.
- stderr keeps the two-second bound and stays silent: what expires there costs a tail in the log
  rather than the answer, and a child holding both pipes would otherwise add the stdout window to
  the end of every process that leaves one behind.
- `ProcessDrainTests` runs a process that exits at once and leaves a child holding its stdout: the
  reader still ends the stream on the bound rather than on the child, and the expiry is in the log.

## 0.26.0

The plan was written before the critic started and its link arrived after the critic finished. In
run `20260904-173914-9254ec` the round-1 draft reached the server at 18:19:40, and the user got the
path at 18:26:48 — seven minutes later, with the round already over — because the path travels
inside `documents`, which `forge.plan.review` builds after the critique. Issue #63.

- `forge.plan.write` is a new tool, the fourteenth: it writes the current draft to `PLAN.md`,
  starts no worker, and answers with `documents` alone. Call it before each round, surface the path
  it returns, then run the round.
- `planDraft` is now optional on `forge.plan.review` and on `forge.work.start`'s `plan.review` act.
  Omitted, the round reviews the file `forge.plan.write` left, so a 50–90 KB draft crosses the wire
  once per round instead of twice. A round with no draft written and none passed is refused, before
  a background job is started rather than inside it.
- `forge.work.start` and `forge.work.poll` results carry `documents`, so on Cursor the plan's path
  arrives with the started job rather than with the fetch that ends the wait.
- A write over an approved plan takes the approval back, resets `tasksCompleted` and drops the
  builder session, exactly as a review round does — the plan file never changes under a raised
  `approved` flag.
- `flow_log.md` is unmoved: it is first created by the first critique, so `documents.flowLog` still
  arrives with that critique and never earlier. The skill now says the plan when it is written, the
  flow log with the first critique. See docs/adr/0014-writing-the-plan-is-its-own-call.md.

## 0.25.1

An argument `forge.work.start` refused reached the orchestrator as `An error occurred invoking
'forge.work.start'.` The server had written the reason — `userGrantedRound is not used by
build.next` — and thrown it as `ArgumentException`, which is a framework type, so the SDK blanked it
on the wire the way it blanks every exception that is not one of ours. Issue #59.

- Every argument rejection now throws `ArgumentRejectedException`, a type of this assembly, so its
  message passes through `ToolErrors` unchanged: an unknown act, an argument an act does not take, a
  missing `planDraft` or `findings`, and a malformed `jobId` on `forge.work.poll` and
  `forge.work.fetch`. The wording did not change; only the type did.
- `ToolSurfaceTests` covers the trip: a `forge.work.start` rejection is asserted through the same
  filter the server runs, naming the argument and the act. The in-process tests pin the wording as
  before.
- The null and whitespace guards are unchanged. They answer a programmer, not an orchestrator, and
  a blank message there costs nobody a retry.

## 0.25.0

Issue #58 opened as a machine fault: every command a Codex builder tried to run failed with
`CreateProcessAsUserW failed: 5`, the build reported `blocked`, and the working tree was never
touched. Chasing it down the App Server surface turned up a second defect underneath it, and fixing
that exposed a third that had been hiding behind every blocked attempt.

- The Codex vendor now runs through `codex exec` instead of the App Server it used before: its
  structured reply is enforced by the provider's strict output schema rather than negotiated
  through the prompt and a retry loop, and the hand-written app-server client, its session, and its
  vendor are gone. The catalogue and the sign-in check move to `codex debug models` and
  `codex doctor --json`. See docs/adr/0012-reach-codex-through-exec.md.
- On a machine where PowerShell 7 came from the Microsoft Store, codex resolved `pwsh` to an
  execution alias that a restricted token cannot traverse, so every command it tried to run failed
  with access denied. The PATH handed to the codex process now drops the Store alias directory, and
  the probe reports the vendor unavailable, naming the reason, when no real PowerShell can be found
  either way. See docs/adr/0013-strip-the-store-alias-from-the-codex-path.md.
- A build task the builder reported `blocked` used to advance the task counter anyway, so the plan
  moved on as if the task had been built. The counter now advances only on `done`; a blocked task
  stays the next task, so the following call retries it.

## 0.24.0

A reached review cap used to be a dead end: the tool refused, and the only way past it was to start
a new run. That was wrong whenever the user had actually read the critique and decided the remaining
risk was one they would accept — the refusal gave the orchestrator nothing to do with that answer.

- The refusal now names the way out. `ReviewCapReachedException` and `CodeReviewCapReachedException`
  say to ask the user whether to run another round and to pass `userGrantedRound: true` if they say
  yes.
- `forge.plan.review`, `forge.review.code`, and `forge.work.start` (for `plan.review` and
  `review.code` only) take a new `userGrantedRound` argument. At the cap it raises this run's cap by
  exactly one and runs the round past it; below the cap it does nothing. It is spent by the call that
  carries it, so the round after it needs a fresh answer — `build.next` and `review.fix` have no cap
  and refuse the argument outright.
- A granted round is recorded in `flow_log.md` as an "extra round granted" entry, ahead of that
  round's critique, and counted in the run state — `grantedReviewRounds` and
  `grantedCodeReviewRounds` — which `forge.status` reports beside the caps they raised.
- `skills/forge/SKILL.md` now tells the orchestrator to check `forge.status` before asking, so the
  question carries the round count, the cap, and the last verdict.

## 0.23.1

`PLAN.md` is the run's most-read document and it was landing where the user could not click it. On a
monorepo the orchestrator correctly names the repository root as `workspaceRoot` — the plan touches
files across it — and the run folder followed, two directory levels above the session. Claude Code
linkifies a path only when it is relative to the session's working directory, so the chat fell back
to a backticked absolute path that renders as plain text.

- `workspaceRoot` was doing three unrelated jobs, and the run's own state is the one that never
  belonged to it. The run folder now goes to the directory the **host** names through MCP's roots
  capability, and only falls back to `workspaceRoot` when the host declares none. The git window —
  baseline, drift, the code-review diff — and the workers' working directory stay on `workspaceRoot`,
  so a run started against the repository root still reviews the whole repository. Passing the
  session directory as `workspaceRoot` would have been the zero-code fix and would have silently
  shrunk the review instead: a `.` pathspec resolves against git's own working directory. See
  issue #53.
- No tool changed shape. `SessionRoots` is bound from the container the way `JobRegistry` and
  `CatalogCache` already are, so it never reaches the published schema — `forge.begin` still takes
  `workspaceRoot` and nothing else, and `build/package.ps1` now asserts exactly that against the
  real handshake.
- Measured on 2026-09-03 by answering each host's handshake with a server that records it:
  claude-code 2.1.258 declares `roots` and answers with the session's working directory;
  codex-mcp-client 0.147.0 declares none and answers `[]`; Cursor 1.0.0 declares none and answers
  `-32601 Method not found`. Both of the latter keep the layout they had, and neither is ever sent
  a request it did not advertise.
- `forge.begin` records `sessionRoot` in the run log beside `client` and `profile` — null for a host
  that declares no roots, which is the one thing the run path alone cannot tell you.
- Roots is deprecated by the specification of 2026-07-28 (SEP-2577) and names no successor.
  Deprecated features stay functional for a year of spec versions, and the day a host stops
  declaring the capability the fallback restores today's layout: the removal costs the link, not the
  run. Existing runs are unaffected either way — a run folder under `workspaceRoot` is still found
  by workspace root and run id.

Two flaky tests came with it, and 0.23.0 never shipped because of them: the release workflow died
before packaging while the identical suite passed, on the same commit, in the build workflow beside
it. Nothing in the server was wrong, and both are fixed here rather than re-run.

- Four tests read a run's `forge.log` with `File.ReadAllLines` or `File.ReadAllText`, which cannot
  open a file an `AtomicFile.Append` handle is holding: the append shares `Read`, and a reader
  sharing only `Read` refuses to coexist with the writer's `Write` access. The appender is a
  *different* test class — `RunLog.Current` falls back to the last log any tool call served, so a
  parallel class spawning a process writes into whichever run folder that was. They now read through
  `AtomicFile.Read`, which asks for `ReadWrite | Delete` and retries, and is the reader the run
  folder has had all along.
- `JobRegistryTests.Wait_returns_on_completion_without_waiting_for_the_timeout` gave a wait a
  one-second budget to prove it had ended on the completion rather than on its ten-second timeout.
  Five seconds proves the same thing and does not lose to a loaded runner.

## 0.22.4

The claude vendor could not run the builder role at all. Every `forge.review.fix` and
`forge.build.next` on `vendor: claude` died the moment the builder called a tool, and the two
things that made it expensive were not the crash.

- `ClaudeCliSession.Observe` read `message.content` without checking that `message` was an object.
  It is on every event the session parses, but not on every event the CLI emits, and the one that
  carries it as a string threw `InvalidOperationException` straight out through `RunAsync`. Both
  that read and the three like it one frame deeper — the first property read of a `content`
  element — now survive a value of the wrong kind, and a `message` that is not an object reaches
  the log as `vendor.skipped-message` with its payload. Which events carry the other shape is
  still unmeasured: two captures of a builder calling `Bash` did not produce one, so the next
  occurrence is meant to name itself rather than cost a second post-mortem.
- A builder writes files and then reports what it wrote, so a turn that died while reporting left
  work on disk that no caller heard about, and the orchestrator reasonably read a failed act as an
  act that changed nothing. Both builder acts now run through `BuilderTurn`, which takes the tree
  as it stands going in and, when the turn does not come back, names the files the turn had
  already written. The whole diff rather than the file list, because a fix round edits files
  earlier rounds already changed and a name-only comparison calls that no change at all.
- That failure now reaches the caller as a `VendorException`, so the orchestrator is told what
  went wrong and what is on disk instead of `An error occurred invoking 'forge.review.fix'` and a
  trip to the run log. The SDK only passes through the message of an exception this assembly
  declared, which is why the fix is to stop letting a foreign one escape the vendor seam rather
  than to widen what `ToolErrors` will surface.
- Vendor stdio is now read and written as UTF-8 explicitly. Left unset, those streams follow the
  console code page, and a server an MCP host starts has no console to speak of: the same run
  decoded every vendor's output as CP437, so an em dash reached the run log, the critic's findings
  and the builder's evidence as `ΓÇö`. ASCII survived it; Cyrillic did not survive it at all.

## 0.22.3

The interview offered claude's four model aliases and could not say what any of them stood for. The
aliases themselves were never the stale part — `opus` and `sonnet` resolve to the newest model of
their family on their own — but nothing told the user whether `fable` meant 5.1 or 5, and the
catalogue was labelled `declarative` precisely because the CLI publishes no list to check against.

- The claude probe now resolves every alias through the CLI and serves the model id it resolved to
  as the catalogue's `displayName`. One `--bare` process per alias reads the id out of the
  `init` event and is killed there: `--bare` skips hooks, MCP servers and the keychain, `init`
  precedes any API call, and the alias table is local, so the whole list resolves offline in about
  four seconds. An alias the CLI echoes back unchanged did not resolve and is not offered — the CLI
  answers an unknown model that way and only fails at the API forty seconds later.
- One further process, without `--model`, is the probe's only billed turn and does three jobs: it
  proves sign-in, so a signed-out CLI makes the vendor unavailable exactly as codex's probe does;
  its own `init` names the model the CLI picks by itself, which becomes `isDefault`; and its answer
  names the model families, so a family released after this plugin shipped can still be offered.
  Only the family names are read from that answer, shape-checked before they can reach a `--model`
  argument, and unioned with the four this repo remembers. The ids it offers are discarded: the
  system-prompt block it reads them from said `claude-fable-5-1` on 2026-09-02 while the CLI
  resolved `fable` to `claude-fable-5`. A discovery that fails for any reason other than sign-in
  leaves the remembered aliases standing and says so in the probe's detail.
- `forge.models` reports `source: "resolved"` for claude instead of `declarative`, which no vendor
  is any more. Its catalogue is ordered newest-first by the version parsed out of the resolved id,
  by the same code that already orders cursor's families — now shared as `ModelVersion` rather than
  living inside the cursor vendor. See
  [docs/adr/0010](docs/adr/0010-resolve-claude-aliases-through-the-cli.md).

## 0.22.2

Every critique was written to disk four times. `review-log.md` carries it because the next round's
fresh critic reads it, `flow_log.md` because the user does, and `forge.log` records it once more as
the tool call's result — and `critiques/round-NN.json` was a fourth copy nothing ever opened: no act
read it back, no tool handed its path out, and the only code that named the folder was the write
itself.

- `forge.plan.review` and `forge.review.code` no longer write `critiques/round-NN.json`, and a run
  folder no longer carries a `critiques/` directory. Round numbering is untouched: code review still
  continues the plan review's count, because the number is what keeps two rounds from arriving under
  the same heading in the log the next critic reads. Folders left behind by earlier runs stay where
  they are — nothing cleans them up.

A host starts the server for every session whether the flow is used in it or not, and the launcher
it starts stays alive as the server's parent until that session ends. The launcher was PowerShell:
around 50 MB of working set sitting beside a 33 MB server, doing nothing but waiting, once per
session and per host.

- `bin/planforge-launcher.cmd` is what the three manifests start now, with `/d` so that a user's
  `AutoRun` cannot write to the stdout the MCP protocol owns. It resolves the executable itself —
  the local build, else the cached download for the manifest version — and names the prompts folder
  in `PLANFORGE_PROMPTS`, which is the half of that contract `build/package.ps1` asserts against.
  A session now costs under 10 MB for the launcher rather than around 50, and starts faster for
  not loading PowerShell.
- `bin/planforge-launcher.ps1` is gone rather than reduced to a fetch step: the batch file reads the
  version out of the manifest with `findstr` and downloads the release asset with the `curl.exe`
  Windows has shipped since 10 1803, so the plugin now carries no interpreter of its own. A version
  it cannot read is refused rather than guessed at, and the download lands beside its destination
  before being moved into place, so a killed session leaves no half-written executable behind for
  the next one to run.

## 0.22.1

A cursor builder spent four tasks reporting `verification: unavailable` because every shell command
it ran came back with no exit status — and `forge.log` recorded none of it. The session read the
final text out of the stream and dropped the rest, so five failed calls, among them a bare
`echo hello` the builder ran to test the shell, reached the log as a clean run. The same run had the
host hand the worker this plugin's own surface: cursor-agent started the forge MCP server for the
builder and offered it the forge skill.

- `CursorAgentSession` reads `tool_call` messages the way the Claude session already reads
  `tool_use` and `tool_result`: a started call logs its command, a completed one logs whether it
  succeeded, its exit code, and the tail of its result. The result is a one-of, so anything that is
  not `success` is logged as an error — a failure shape this vendor has not shown yet still lands
  in the log as a failure rather than as silence.
- Every role prompt carries a new shared `prompts/orchestration-contract.md`: the forge skill and
  the `forge.*` tools belong to the orchestrator, and neither a builder nor a critic may call them
  or follow it. It is appended the way the Roslyn contract is appended to critics, so it lives once
  rather than in six vendor files.

## 0.22.0

The plan was the one thing a run withheld. `PLAN.md` was written by `forge.plan.confirm` and by
nothing else, and the skill told the orchestrator to keep every draft to itself until the critic
returned `approve` — so after choosing a critic and a builder, the user watched up to five rounds of
verdicts about a document they had never seen.

- `forge.plan.review` writes the draft it is handed to `PLAN.md` before the critic starts, and
  rewrites it every round. Before rather than after, because the round takes minutes and those are
  the minutes when having the plan to read is worth something. The write is a whole-file
  replacement, so a retried round writes the same bytes; no new guard precedes it, because the same
  draft already reaches disk in `forge.log` through the tool-call record.
- The `flowLog` object on every act result is replaced by `documents`, holding `flowLog` and `plan`
  — each a `path` plus what to do with it, each `null` until its file exists. **Breaking**: a host
  reading `flowLog` off a result finds nothing there. The skill ships with the change.
- The skill's "keep the drafts to yourself" rule becomes "link them, do not paste them". The plan's
  text still stays out of the chat; the file is surfaced and refreshed the way the flow log already
  was.
- A review round run against an already-approved plan takes the approval back: `approved` is
  cleared, `tasksCompleted` returns to zero, the builder session is dropped, and a `## Plan reopened`
  entry lands in the flow log naming the task count. The plan has to be approved again before the
  builder runs, and it starts from the first task. `PLAN.md` therefore means "the plan as it
  currently stands", not "the approved plan" — approval was always `approved` in the run state, and
  still is. See [docs/adr/0009](docs/adr/0009-the-plan-is-visible-from-the-first-round.md).

## 0.21.1

Moving the executable to a release asset in 0.21.0 left the prompts behind. The launcher downloads
one file, the prompts ship in the plugin package, and `PromptLibrary` finds them by walking up from
the binary — which from a cache under `%LOCALAPPDATA%` walks past nothing at all. Every worker act
died on its first prompt on every machine that installed from the marketplace rather than building
locally, and the tool result said only that an error had occurred
([#44](https://github.com/dlin10/ai-coding-plugins/issues/44)).

- `bin/planforge-launcher.ps1` names the prompts folder in `PLANFORGE_PROMPTS`, and `PromptLibrary`
  takes that as the root when it is set. The prompts stay in one place — the plugin package — so
  editing them without a rebuild keeps working, now on the download path too.
- The nullable parameters of `forge.plan.review`, `forge.build.next`, `forge.review.code`,
  `forge.review.fix` and `forge.log.append` are declared optional rather than required. Required
  and nullable together had no encoding that worked: omitting the key was refused server-side, and
  a host that dropped the `null` literal while serializing sent JSON that never parsed. The domain
  rule that a second review round must carry a `revision` is unchanged — it was always the act's
  check rather than the schema's.
- A failure now says why. An exception of this server's own is answered as a tool error carrying
  its message, so the orchestrator can act on the reason instead of finding it later in `forge.log`;
  anything else keeps the SDK's generic answer.

## 0.21.0

Every fresh checkout pulled a 17 MB executable through Git LFS — each plugin install, each
per-commit Cursor cache, each CI run — and GitHub meters that bandwidth: the account's monthly
quota ran out, which blocks the very downloads the marketplace depends on. Release assets are not
metered, and a release workflow already publishes one per version, so the executable now travels
that road instead of the repository.

- `planforge.exe` is no longer committed, and nothing in the repository is LFS-tracked any more.
  The manifests launch `bin/planforge-launcher.ps1`, which runs a locally built
  `bin/win-x64/planforge.exe` when one exists and otherwise downloads the executable for the
  manifest version from the matching GitHub release, cached once per version under
  `%LOCALAPPDATA%\plan-forge-flow` — so per-commit plugin caches never re-download it.
- The release workflow uploads the bare `planforge.exe` next to the bundle zip, giving the
  launcher an asset to fetch. The zip still carries the executable, so a local-marketplace
  install keeps working offline.

## 0.20.1

A builder in an MCP-heavy workspace died on its first task, twice, and the log blamed an
output-cap kill that never happened ([#41](https://github.com/dlin10/ai-coding-plugins/issues/41)).
The real failure was a stdout line that parsed as JSON but not as an object — asking it for a
property crashed the session, disposing the stream killed the still-running vendor, and the kill
reason was inferred by elimination, so a consumer fault wore the cap's name and the surfaced error
sent the reader hunting a schema bug that did not exist.

- `StreamingProcess` now tells the two kills apart: `output-cap` is logged only when the cap
  actually threw, and a consumer that stopped reading — a fault or an early exit — is logged as
  `abandoned`.
- The Claude and Cursor sessions skip a line whose JSON root is not an object instead of crashing
  on it, and log the skipped payload as `vendor.skipped-line` — what a CLI emits outside the
  protocol is exactly what the next post-mortem needs.

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
