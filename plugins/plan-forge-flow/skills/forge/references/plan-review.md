# Forge reference: plan review rounds and revision

Read it before the first `forge.plan.review`, and again whenever plan review resumes.

## Rounds, revision, and caps

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
  `reject` only with a reason and asserted decision-maker; use `addressedByRevision` only after the
  revised plan actually addresses that ID. Partial subsets are valid during review. The final
  `forge.plan.confirm(approved: true)` is the postcondition that requires every active plan ID to
  be settled or closed.

The optional `deferred` narrative remains Flow-only compatibility text. It never settles an ID and
never reaches the Critic; authoritative deferrals and rejections are typed `decisions`.

The critic judges the requirements too, and those findings are not all yours to fix. One the
interview already settles — two requirements you wrote that contradict each other, a condition
stated too vaguely to check — you revise and carry on without stopping. One it does not — a
requirement covering a question nobody asked, or an answer that would move the scope — goes to the
user before you revise, and its answer goes into `forge.log.append`. Ask the moment it comes up
rather than saving it for approval: a scope question answered late invalidates every round that ran
after it. When one critique brings several, ask them one at a time, the one the others depend on
first.

Review rounds are capped, and so is the code-review loop. At the cap a round still runs without
asking when the last round found fewer findings than the one before it — new ones plus unresolved
ones the critic still finds — and left no blocker, for as long as the count keeps falling. Its result then
carries `continuedAutomatically`, for example `"4 → 2"`: say so in one line with those counts and
carry on. Otherwise the tool refuses, and only then is the cap question asked.
Before asking, call `forge.status` and show the user how many rounds have run, what the cap is, and
what the last verdict said, so the question carries its numbers. Say in plain words what the open
findings are, and what one more round would cost against stopping here. On a yes, pass
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
  keeps tasks 1 to k − 1 and rebuilds from k. Its answer says how many it kept in `tasksKept` and
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
mechanism invented to answer one finding. Use `forge.plan.patch` when the revision changes some
targets, and `forge.plan.write` when it rewrites the plan. Before either call that answers a
critique, walk every change through these checks over the whole plan, including after a patch;
the draft is ready when every change passes all six:

- **Gate with the rule.** A rule, requirement or step the revision adds or changes arrives with its
  gate rows, its tests and the task steps that build it, in the same revision.
- **Every mention.** A term, rule, count, name or decision the revision changes is changed wherever
  the plan states it — requirements, tasks, gates, test lists, counts. Search the whole plan for
  every mention, not only the targets you edit.
- **Evidence for claims.** Every claim the revision makes about the code — what exists today, a
  name, a count, a behaviour — passes the Evidence check before it is written.
- **The class, not the instance.** A finding that names one member of a class — one reader of a
  value, one call form, one case of a rule — is answered for the whole class: list its members, by
  references and callers where the class is code, and cover each one, or narrow the requirement in
  words. Name the list in `revision`.
- **Mechanisms are the user's.** When the answer to a finding is a new mechanism — a pass, a state,
  a wrapper, a restart, a fixpoint — ask the user before writing it, with the simpler alternatives
  beside it.
- **The findings' scope.** The revision changes what the findings name and what the checks above
  reach. A neighbouring case enters the plan when a critic raises it.

On a stale-base refusal, read `forge.plan.outline` and redo the edits against its current hash;
never resend the refused patch unchanged. A replay after a lost answer is a stale-base conflict,
and equal text is never proof that the patch was applied.

From the second round on, label every new finding with the labels of *When a round is mostly
fallout* in `code-review.md`, reading *revision* for *fix* — `own`, `sibling`, `same-class`, `older` — and record one
line per round with `forge.log.append`:
`Plan review round 3: own F-0021; sibling F-0024; same-class F-0019; older F-0022`.
