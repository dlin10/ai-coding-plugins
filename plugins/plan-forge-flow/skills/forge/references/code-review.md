# Forge reference: the code-review loop

Read it when the last task's gate passes, before you run the plan's gates and the first `forge.review.code`.

## The code-review loop

When the last task's gate passes, declare `phase: "code_review"` before you run the gates below.
Declare `phase: "code_review"` again each time a review round's critique arrives — `forge.review.code`
returning — so each record holds one round: your filtering, the fixes and their checks up to the
next critique.

After the last task and before the first review round, run the plan's `## Gates` entries yourself,
in your own environment — all of them, conditions included. The server runs the executable ones
only after a full-mode fix, so at this point nobody has: they are the checks no single task owned, no
builder ran them, and the critic must not — it judges the diff, and a build writes into the very
tree it is reading. Record what you ran and what it showed with `forge.log.append`. A failing gate
is not a code-review finding: stop there and decide with the user, exactly as with a task whose gate
failed.

Then the loop is yours to run, exactly as with plan review: `forge.review.code` runs one critic
round against the approved plan and `forge.review.fix` hands kept findings to the builder — through
start → poll → fetch only for a fix batch expected to outlast the host's hour. The same routing
applies to Scout: start a job only for a question you have your own work to do beside, and otherwise
call `forge.scout.run` directly, with an explicit `sessionMode` either way. Repeat until the
verdict is `approve` or the cap refuses. The critic and the builder never talk directly — you
are between them because you are the only participant who knows what the plan deliberately left
out.

Between the two calls, classify every identified finding by ID:

- **Fix** unresolved defects the diff gets wrong. Put exactly those IDs in `fixFindingIds`, allocate
  one `fixAttemptId` for that logical execution, and let the server render their verbatim ledger
  findings for the Builder. Batch them by rule and frame each batch with a note, as "Fix batches and
  their notes" below describes.
- **Defer or reject** what the approved plan excludes or the user already decided differently, with
  asserted `by` and a reason in one decision batch. This is a decision, not a deletion.
- **Close a duplicate** when a new ID is semantically the same as a current ID: keep the canonical
  ID and close the redundant ID with `duplicateOf` plus the reason.
- **Answer reopening proposals** with `accept` or `decline`. Only acceptance changes the existing
  settled ID; the Critic cannot reopen it itself. A plan-origin ID accepted here keeps its origin and
  becomes active in code review.
- **Host-close** an active code ID only after your own verification, using `hostVerified` with
  evidence in a decisions-only `review.fix` call.

The bias to hold: when unsure whether to settle a finding, fix it. Deferral and rejection are for
decisions the plan or user settles, never for findings that are inconvenient. `review.fix` may carry
decisions and an independent fix attempt together, but the same ID cannot be both decided and fixed,
and a batch with `raises` travels without a fix attempt.

The Builder reports each fix as a rule and its places: `rule:`, then one line per place with
`fixed`, `already agreed`, or `left: <reason>`. Read that report before the next round:

- A `left:` place the approved plan excludes needs nothing more; it is out of scope, as it would be
  for a Critic. Raise every other one in a decisions-only `forge.review.fix`, then fix it in a later
  call by the ID the raise returned, or leave it for the next Critic, which has to assess it. A place
  left only in a summary comes back as a Critic finding a round or two later, at a round's price.

After the current round's targeted fixes, read status for the saved timing, current pending IDs and
covering attempt IDs. With `beforeNextRound`, run all full-plan G1..Gn checks, conditions included,
once before the next Critic. The server refuses that review while pending remains. With `final`,
continue review with pending visible to the Critic, then run all G1..Gn after its approval. At either
timing full verification is required before reporting a successful run. Do not rerun a successful
full check without intervening edits: a full-mode fix's passing gate result can supply evidence for
executable checks, while conditions must be confirmed separately.

Record each full-check outcome through `forge.log.append`: command (or condition), covered attempt
IDs and finding IDs, exit code and output tail, including failure, timeout, cancellation or inability
to run. Only successful full verification permits a decisions-only `forge.review.fix` batch of
`hostVerified` closures with explicit evidence covering those pending findings and attempts.
The server trusts this host assertion. Do not close pending from short success or Critic assessment.

A full-check failure leaves the fixes unverified. Choose existing findings to repair, raise a new
finding, or repair the environment; do not automatically attribute the regression or reopen findings.
Pass the failing command and output tail to the Builder in the new fix attempt's `note`, or the
new raise's `what`/`reason`: the server's gate-failure summary does not contain Orchestrator checks.
A final regression requiring code changes returns to the ordinary cycle: fix, new Critic, full
verification. When that round is refused at the cap, first ask the existing question about one
additional round. An
environment-only repair repeats full verification without another Critic. If beforeNextRound is
blocked, repair the environment, explicitly reconfirm `fullGate=final`, or consciously defer/reject
with reasons and an honest account of unverified fixes; changing timing alone verifies nothing.

If the user stops or declines another round, declare `phase: "wrap_up"` and end the run as
unfinished with explicit pending IDs and covered attempts; do not force checks against that stop.
Approval with pending is not successful completion. Finished or unfinished, declare `phase: "done"`
once the outcome is reported to the user. If saved fullGate is malformed or unknown (including non-string JSON), leave the run
unchanged, record the failure with the still-available `forge.log.append`, and begin a new run.
Never suggest manually editing `.forge`. Auxiliary models/log/poll/cancel/fetch remain available
without added state checks; fetch retains its historical snapshot.

When the verdict settles — or the cap is reached and the user chooses to stop — declare
`phase: "wrap_up"`, and the deferred findings go to the user with the outcome. They are real findings about real gaps; the plan is the
only reason they were not fixed here, and they are candidates for the next run.

### Fix batches and their notes

A batch is the set of IDs one fix attempt fixes. Form batches by the rule the findings break, not by
file or severity:

- Never split one rule's findings across batches. Two deciders of one question in two batches — the
  effect of a write and the value it stores, the cheap filter and the precise check — get one turn
  that fixes half the rule and another that finds it half fixed.
- A batch may carry several rules. Keep rules that share an owner or a subsystem together, and keep
  apart rules whose failure should not hold the others back: each batch costs a Builder turn and
  its selected gate (all executable G entries in full, only Fix gate in targeted). A finding whose rule no other finding
  shares is a rule of its own.
- When a fix also covers a finding outside its batch, close that finding afterwards with
  `hostVerified` and the evidence: the test, the `file:line`, the gate run. `duplicateOf` cannot do
  it, because it needs a canonical ID still in the ledger, and the gate's closure has removed the
  batch's IDs in full mode. Targeted findings remain present and pending; wait for full host
  verification before host-closing the batch or an additionally covered finding.

Targeted is unsuitable without a meaningful short check or when one rule spans many classes that
need joint regression coverage. For a separate batch, explicitly choose `gate=full` with a new
attempt and explain why. Use the same simple exit when the Fix gate is wrong or repeatedly fails;
never change a bound attempt's mode or silently edit the approved plan. A necessary plan change
uses ordinary revision/review/approval, which keeps or rebuilds built tasks as *Rounds, revision,
and caps* in `plan-review.md` describes. Preserve `gate`,
attempt and exact IDs on every retry; only a new legal attempt can choose another mode.

The first call of every fix attempt carries a `note` that frames its batch by the rules its findings
break. The Builder already has the findings verbatim, so do not paste them or the critique back; the
note adds only what the ledger lacks. For each rule:

- **Rule** — one sentence: the property the code gets wrong, not the example that showed it, with
  the IDs it covers and where it comes from. Cite the requirement, `R5`: the Builder holds the
  requirements in its Brief. A fix prompt carries no task text, so when the rule rests on a task's
  wording, quote that sentence. When it rests on a decision the user made during the run, say so,
  and record the decision with `forge.log.append` if you have not.
- **Owner** — the one place that decides the question: the owner the plan names, an existing
  function, or one to create. Say that every other place calls it instead of deciding again.
- **Axes** — every kind of place where the same question is decided, listed. Take them from the
  plan first: the rules the matrix check listed with their axes, a matrix task's axes, the places
  and axes a task names for the rule's owner. Then check the kinds a plan tends to miss: the forms
  that reach the same operation (an invocation, an object creation, a delegate call); the channels
  a value enters or leaves by (a result, an `out` or `ref` parameter, a store, a value kept for
  later, a delegate's input); the executions it runs in (setup, the call, an enumeration, a startup
  or unknown execution); the kinds of input or object; the direct path and the one through a
  helper, a `ref` or an alias; the other arms of the same switch; and twin deciders — the cheap
  filter and the precise solver, the producer and each consumer, the code and a document that
  restates the rule. Tell the Builder to find them by references and callers of the data that
  carries the fact, not by searching for a word: a search finds a spelling, not a place that decides
  without it.

Once for the whole batch, and again under a rule only where that rule has its own:

- **Must not change** — recorded floors and snapshots, the fixes of other findings, and the scope
  the plan excludes.
- **Stop condition** — when to stop and report instead of widening: a floor would fall, the rule
  would contradict the plan, a place has no source for what the rule needs.

A part that does not apply says so in a few words — `Axes: none, the count is stated only here` —
rather than being left out.

A note steers the Builder as firmly when it is wrong as when it is right. In one measured run a note
told the Builder to drop a guard that was a requirement's own rule, and the next attempt had to undo
the unsafe narrowing that followed. The cited source is what lets the Builder check a rule against
the plan before it acts on it.

A retry of the same attempt resumes the Builder session that already holds the first note, so its
note says what changed since the last call — what is on disk, what failed, what is left — and
restates in full, with its source, only a rule it corrects. A retry sent to another vendor starts a
fresh session: send the whole note again.

An example, in a neutral domain — copy the form, not the words:

```text
Rule 1 (F-0031, F-0034, F-0040) — a request's tenant is the one its access token names, never one
the caller supplies (R3; task 2: "every query is scoped to the tenant of the authenticated caller").
Owner: `TenantResolver.FromToken`. Every place that needs the tenant asks it; none reads `tenantId`
from the route, the query string or the body again.
Axes: every way a request is served — HTTP handlers, background jobs run for a user, message
consumers; every way the tenant leaves a call — repository filters, cache keys, outgoing calls, the
audit log; the twin deciders — the authorization filter and the repository's own query filter. Find
them by references to the tenant value and their callers, not by searching for `tenantId`.

For the batch — Must not change: the recorded performance baseline, the schema (the plan excludes
migrations), this round's fix of F-0029.
Stop condition: a job with no token to read the tenant from — report the job rather than inventing
a source.
```

### When a round is mostly fallout

Measured over nine runs, about half of the code-review findings after the first round were fallout
of the previous round's fixes: a rule an earlier fix wrote was wrong, or a place answering the same
question was left behind. After each round, label every new finding and record the labels with
`forge.log.append`, one line per round:
`Code review round 3: own F-0150; sibling F-0152; same-class F-0149; older F-0151`.

- `own` — something an earlier fix wrote is wrong: the rule it keyed on, or anything else it changed;
- `sibling` — a place that answers the same question as an earlier fix was left behind;
- `same-class` — an older instance of a class an earlier fix covered only where a finding named it;
- `older` — anything else the review predates.

`own` and `sibling` are fallout. When half a round or more is fallout, or its findings walk one axis
of a rule case by case — field, then local, then `ref` — ask for no further round yet. Sweep instead:
list the axes of each rule the fixes touched, probe every combination against an oracle (a second
path that must agree), fix the root causes, then spend one round to confirm. Hold the fixes you make
on the host to the Builder's own rule — fix the rule, not the place — and record each with
`forge.log.append`.

### When a round is too large for the critic's vendor

The review window only grows over a run, and codex accepts at most 1,048,576 characters in one
prompt. The server fits a round itself: over the limit, it gives the critic the untracked files by
path, then the tracked files' diffs as files in the run folder, and the critique's summary says so.
Nothing leaves the review that way, and there is nothing to ask.

Only a round still over the limit with every file given by path is refused before any critic
starts: nothing was sent, the round was not counted, a grant the user gave for it still stands, and
the flow log says the round was not sent. The refusal lists what the prompt still holds. Show the
user those sizes and ask how to narrow the round, as "Asking the user" in `asking.md` describes, then start the
same round again with `excludePaths`, which takes paths or patterns out of the round, named from
`workspaceRoot`. Only for what is not the run's work — a plan's gate script, generated output such
as an eval snapshot. A deliverable the plan asks for left out is one the critic cannot judge.

`excludePaths` is not remembered: pass the same paths on every later round, and say in your
narration what the critic was not handed. Do not move files out of the repository to make a round
fit — a gate that runs a moved file fails, and the window would then differ from the tree the gates
check.
