# Forge reference: plan review rounds and revision

Read it before the first `forge.plan.review`, and again whenever plan review resumes.

## Rounds, revision, and caps

Before round 1, settle the first-draft split offer in `planning.md` when the first substantive
`forge.plan.write` returned `shape.splitSuggested: true`. Do not send the Critic until the user
chooses the narrower draft or **review as is**.

After round 1, a `forge.plan.write` or `forge.plan.patch` returns `splitSuggested: true` only when the
plan has grown since the last offer. `splitReason` is `crossed` when Approach has reached 22,000
characters from below, or `doubled` when it has reached twice `splitCheckpoint`, the size it stood at
when the last offer was owed. Ask the split question before the next round, in the form `planning.md`
gives the first-draft offer: name the separable tasks or task groups with their sizes and R-items, say
what the growth since `splitCheckpoint` added, and offer **continue as is** as the alternative. Ask
once per trigger: the checkpoint has already moved to the current Approach, so another offer needs
another doubling. A false `splitSuggested` needs no question, and a split is never automatic.

Each `forge.plan.review` call runs exactly one round and returns a verdict of `approve` or `revise` plus findings. On `revise`, declare
`phase: "plan_revision"` as soon as the verdict arrives — after every `revise`, not only the first —
then address the findings in the plan yourself — *Writing a revision* below says how — and run the
next round. The
Critic is a fresh process each round and receives only the current plan-phase ledger projection, so
it converges on current findings without being anchored by the transcript.

A revision that introduces a Change point no Impact pass has checked gets its own Impact pass and Evidence check before its review.

From the second round on, sort each critique's new findings by the rule they are about. When more
than half of them are combinations or edge cases of one rule and the plan has no matrix task for
it, add one in this revision — do not wait for the cap — and say so in `revision`. Each of those
findings is then addressed by its cell in the table, not by a case of its own.

Every round after the first carries your answer to the one before it. The tool refuses the call
without `revision`:

- `revision` — what you changed in the plan, in a sentence or a short list, in the findings' own
  terms. It goes to the flow log and nowhere else, so the user can see your turn between the
  critic's; no worker ever reads it. Say so plainly when a round changed nothing and you are
  re-running for another reason.
- `decisions` — the typed identity-keyed changes for this logical decision set. Use `defer` or
  `reject` only with a reason and asserted decision-maker; use `builderNote` for an unresolved minor
  with a reason and explicit `forTasks` task numbers; use `addressedByRevision` only after the
  revised plan actually addresses that ID, and for a blocker or major with `evidence` (*Writing a
  revision* says what goes in it). Partial subsets are valid during review. The final
  `forge.plan.confirm(approved: true)` is the postcondition that requires every active plan ID to
  be settled or closed.

Prefer `builderNote` when a competent Builder would infer a minor finding's remedy while implementing
the named tasks. The note reaches only those tasks, carrying the finding's `where` and `what` and
your reason. Name tasks explicitly even for a finding in Requirements or Gates; do not send it to
every task by default. A blocker or major still belongs in the revised plan, not in a Builder note.
If a later plan write or patch changes the task list, the note is stale: accept reopening the ID,
then give it a new `builderNote` decision with current task numbers before confirmation.

The optional `deferred` narrative remains Flow-only compatibility text. It never settles an ID and
never reaches the Critic; authoritative deferrals and rejections are typed `decisions`.

The critic judges the requirements too, and those findings are not all yours to fix. One the
interview already settles — two requirements you wrote that contradict each other, a condition
stated too vaguely to check — you revise and carry on without stopping. One it does not — a
requirement covering a question nobody asked, or an answer that would move the scope — goes to the
user before you revise, as a scope question when it widens the run (*Scope questions* in `asking.md`),
and its answer goes into `forge.decision.record`. Ask the moment it comes up
rather than saving it for approval: a scope question answered late invalidates every round that ran
after it. When one critique brings several, ask them one at a time, the one the others depend on
first.

Review rounds are capped, and so is the code-review loop. At the cap a round still runs without
asking when the last round found fewer findings than the one before it — new ones plus unresolved
ones the critic still finds — left no blocker, and, in plan review, fewer than half of its new
findings were fallout (*Labelling fallout, and the sweep* below), for as long as the count keeps
falling. Its result then carries `continuedAutomatically`, for example `"4 → 2"`: say so in one line
with those counts and carry on. Otherwise the tool refuses, and only then is the cap question asked.
Before asking, call `forge.status` and show the user how many rounds have run, what the cap is, what
the last verdict said, how much of the last rounds was fallout (`fallout` of `new` for each labelled
round in `reviewTallies`; a refusal for fallout quotes the share of the round it was about), how far
the plan has grown since its first draft (*Scope questions* in `asking.md` says where to read it),
and how many widenings the run has taken since the interview (`decisions.widenings`), so the
question carries its numbers. Say in plain words what the open findings are, and what one more
round would cost against stopping here. Offer the sweep and a split — narrow this run to what is settled and move the
rest to a follow-up run, in the form *Scope questions* gives — beside one more round, and recommend
the sweep while half a round or more is fallout. On a yes to a round, pass
`userGrantedRound: true` on the next round tool — `forge.plan.review` or `forge.review.code` — which
raises the cap by exactly one. The grant is spent by
that round, so the round after it needs a fresh answer, and never pass the argument without having
asked.

A round whose error says the critic could not start a shell spent nothing: no round was counted and
no finding reached the ledger, because a critic whose commands never started judged without reading
the repository. Do not call again as if it were a passing fault — the same vendor fails the same way
until it is repaired. Tell the user what the error quotes, and ask whether to repair the vendor and
retry, or to run the critic on another vendor. A code-review round, and a builder act whose error
says the same, are handled the same way.

Link the drafts, do not paste them. Each round is `forge.plan.write` with the complete draft or
`forge.plan.patch` with the revision, then the round itself with `planDraft` omitted — either
writer puts the plan at `<runPath>/PLAN.md` and hands you its path, and the round then reads it
from there. Surface that path in the same turn as the write or patch, before the round starts, so
the user reads the plan during the minutes the critic takes rather than after them; refresh it after each
round. What stays out of the chat is the plan's *text*: five revisions pasted into the conversation
bury the one version that matters, which is why the file exists.

Revising an already-approved plan takes the approval back: whichever call changes the file —
`forge.plan.write`, `forge.plan.patch`, or a round you handed a draft to — clears `approved`, forgets
the Builder session, and records it in the flow log. What happens to the built tasks depends on the
call:

- `forge.plan.patch` keeps every built task before the first task whose text it changes. A patch to
  the title, Requirements, Gates or a task not yet built keeps them all; a patch that changes,
  inserts or removes task k — or adds prose after task k, which the build reads as part of it —
  keeps tasks 1 to k − 1 and rebuilds from k. An `edit` within task k counts as a text change.
  Its answer says how many it kept in `tasksKept` and
  which task the builder will run next in `firstTaskOwed`.
- `forge.plan.write`, and a round you handed a draft to, reset the build progress to zero: a whole
  draft says nothing about which tasks it left alone. Use them only for genuine rewrites.
- A round over the plan on disk changes no text and keeps what the last write or patch kept.

That is not a silent housekeeping detail — say it in the chat, because the next `forge.build.next`
will refuse until the user approves again. If tasks were already built, tell the user how many the
revision keeps and how many are about to be rebuilt before you make it. Declare
`phase: "plan_revision"` when you begin revising an approved plan, before its new review round,
whatever phase the run was in.

### Writing a revision

Measured over thirteen runs, about half of the plan-review findings after round 1 sat in text a
revision had written, rising to two thirds from round 6 on: a rule that arrived without its gate, a
change made in one place and left standing in another, a claim about the code nobody had checked, a
mechanism invented to answer one finding. In a later ten-round run the Critic's claims held every
time they were checked and the revisions' did not: 15 of the 16 later findings that cited the
code pointed at lines nobody had read when the fix they hit was written, and 7 of the 9 claims a
revision wrote without reading anything turned out false. So each check below is an act done
before the patch — a read, a query, a search, a question — not a recollection. Use
`forge.plan.patch` when the revision changes some targets, and `forge.plan.write` when it rewrites
the plan. For a small change within a target, prefer `edit` with a unique `find` over replacing the
whole target; use `replace` when the target's text changes throughout. Before either call that
answers a critique, walk every change through these checks over
the whole plan, including after a patch; the draft is ready when every change passes all six:

- **Gate with the rule.** A rule, requirement or step the revision adds or changes arrives with its
  gate rows, its tests and the task steps that build it, in the same revision. For each clause of
  the rule, name the test cell where a wrong implementation gives a different answer from a right
  one: a test both would pass is no gate, and a negative clause — never, only, except — needs a
  cell of its own.
- **Every mention.** A term, rule, count, name or decision the revision changes is changed wherever
  the plan states it — requirements, tasks, gates, test lists, counts. Search the whole plan for
  every mention, not only the targets you edit.
- **Read before you write.** Every claim the revision makes about the code — what exists today, a
  name, a reader list, a count, a behaviour, what a fixture does — is read in the code during this
  revision, with Read or a Roslyn query, before the patch. A Scout answer says where to look; read
  the lines it cites when the fix depends on them. The Evidence check weighs the plan against
  Scout's answers, and a claim no answer holds passes it unseen, so it does not stand in for this.
  A question you put to the user carries only claims you read: they choose on what its options say.
- **The class, before the patch.** A finding that names one member of a class — one reader of a
  value, one decider of a question, one call form, one case of a rule — is answered for the whole
  class: before you write, run Roslyn `find_callers` or `find_references` on the symbol that
  decides it, on a server that serves this workspace — a search for every use where none does —
  list every member, and cover each one or narrow the requirement in words. A rule the
  revision adds or changes gets the same query, because its twin deciders are where the next round
  looks. Name the list and the query in `revision`.
- **Mechanisms are the user's.** When the answer to a finding is a new mechanism — a pass, a state,
  a wrapper, a restart, a fixpoint — ask the user before writing it, with the simpler alternatives
  beside it and the split that keeps the work already settled (*Scope questions* in `asking.md`).
- **The findings' scope.** The revision changes what the findings name and what the checks above
  reach. A neighbouring case enters the plan when a critic raises it.

Close each finding the revision answered with `addressedByRevision`. A blocker or major also carries
`evidence`: one line saying what you read, the class you listed and the cell that fails for a wrong
implementation — `read Ledger.cs:940-964; callers of Ledger.Close: Review, Confirm, Fix; cell
major × no evidence`. The server refuses a blocker or major closed without
it, and the Flow log prints the line beside the closure.

On a stale-base refusal, read `forge.plan.outline` and redo the edits against its current hash;
never resend the refused patch unchanged. A replay after a lost answer is a stale-base conflict,
and equal text is never proof that the patch was applied.

### Labelling fallout, and the sweep

A revision is the main source of the next round's findings: in the longest measured run, 20 of the
27 valid findings of four consecutive rounds were fallout of the revisions before them. So from the
third round on, `forge.plan.review` carries `fallout` whenever the previous round raised new
findings: their IDs, each under exactly one label, read against the revisions made before that
round, as *When a round is mostly fallout* in `code-review.md` reads them against fixes:

- `own` — something an earlier revision wrote is wrong: the rule it keyed on, or anything else it changed;
- `sibling` — a place that answers the same question as an earlier revision was left behind;
- `sameClass` — an older instance of a class an earlier revision covered only where a finding named it;
- `older` — anything else the plan's first draft already held.

Label by what the finding names, not by how it is worded: a finding is `older` only when the first
draft already held the text it names. The tool refuses the call, naming the IDs, when a new finding
has no label, a label names an ID the previous round did not raise, or an ID has two. The server
writes the Flow-log line, so do not also write it with `forge.log.append`, and keeps the round's share
in `reviewTallies`: `new` findings, of which `fallout` are `own` or `sibling`.

When half a round or more is fallout, or its findings walk one axis of a rule case by case, run no
further round yet. Sweep instead: for each rule the revisions touched, list its axes, search the
whole plan for every mention and name its one owner; settle the rule at its owner and answer the
other places from it; then spend one round to confirm. Hold the sweep to the checks of *Writing a
revision*, and say in `revision` that it is a sweep, with the rules and axes it covered.
