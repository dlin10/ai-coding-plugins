# Plan Forge Flow releases

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
