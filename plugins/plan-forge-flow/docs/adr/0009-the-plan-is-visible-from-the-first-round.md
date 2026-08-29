# Write the plan from the first review round, and let a round take an approval back

Reverses two things at once: `PLAN.md` was written only by `forge.plan.confirm`, and
`skills/forge/SKILL.md` told the orchestrator to keep every draft to itself so that "the plan
reaches them exactly once, when the critic returns `approve`". Both were deliberate. Together they
made the whole of plan review invisible: the user chose a critic and a builder, and then watched a
timeline of verdicts about a document they had never seen, for as many rounds as the cap allowed.
The one artefact the act exists to produce was the one artefact withheld until the act was over.

`PlanReview.ReviewAsync` now writes the draft it was handed to `PLAN.md` before the critic starts,
every round, and every act result carries the path out under `documents.plan`. Before the critic
rather than after it, because a round runs for minutes and those minutes are exactly when having
the plan to read is worth something. The write is a whole-file replacement, so a round that dies
and is retried with the same arguments writes the same bytes — unlike the log appends beside it,
which have to wait for the critique to avoid recording a revision twice. No guard precedes the
write: the same draft is already on disk in `forge.log`, written by the tool-call record, so this
adds no surface a secret could reach.

**The rule that replaces "keep the drafts to yourself" is "link them, do not paste them."** The old
rule's reasoning was sound about the chat and wrong about the file. Five revisions pasted into a
conversation do bury the version that matters; a file the user can open, ignore, or leave open in a
tab does not. So the plan travels the same road the flow log already travels — a path plus an
instruction, refreshed after each act — and the chat keeps its one line of narration per call.

**The cost is that `PLAN.md` stops meaning "the approved plan".** It means the plan as it currently
stands. Nothing read the file's existence as approval — `Build` and `CodeReview` gate on
`state.Approved` — so the builder was never at risk from the file appearing early. It would have
been at risk from the file *changing* late: a round run after approval would have left a raised
`approved` flag over text nobody approved. Rather than forbid that round, a round now takes the
approval back — clearing `approved`, resetting `tasksCompleted` to zero and dropping the builder
session — and records it in the flow log. Re-approving is what starts the builder again, from the
first task.

Two orderings inside that are load-bearing. The withdrawal is written **before** the plan, so a
crash between them leaves the flag off over approved text, which only refuses a build; the reverse
would leave the flag on over unapproved text, which is the hole the whole change would otherwise
open. And the withdrawal's state is what the round's closing `WriteState` builds on, because that
call starts from a capture taken at the top of the act — a stale capture would restore `approved`
on the way out, silently, and only on the path where the round succeeds.

**What is given up**: an approval is no longer final within a run, and a plan re-reviewed after
three built tasks costs those three tasks again. Keeping the progress would have been cheaper and
wrong — the counter indexes into a task list the new plan may have renumbered, and a builder
resuming its session would carry a memory of numbering that no longer refers to anything. Paying
for the rebuild is the honest reading of "this is a different plan now". The alternative considered
and rejected was refusing a review round on an approved run, which closes the same hole by removing
a flow that works today: deciding to harden a plan further after approving it is a reasonable thing
for a user to do, and the server has no business refusing it.
