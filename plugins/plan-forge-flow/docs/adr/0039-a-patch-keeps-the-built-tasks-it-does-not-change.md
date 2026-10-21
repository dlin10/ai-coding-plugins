---
status: accepted
---

# A patch keeps the built tasks it does not change

Issue #168. Amends [0014](0014-writing-the-plan-is-its-own-call.md) and
[0035](0035-patch-the-plan-against-a-snapshot-outline.md), which sent every change to an approved plan
through one reopening that set `tasksCompleted` to zero, dropped the files each built task changed
and forgot the Builder session.

## Context

After re-approval the next `forge.build.next` ran task 1 again, and every built task after it,
before the Builder reached the task the change was about. A re-run Builder found its work on disk
and only verified it, but each verification was a Builder turn, a server gate, and a 10–16 KB
result in the Orchestrator's context. Measured on concurrency-hunter runs of 14–20 October 2026
(`flow_log.md` task sequence joined with `forge.log` call durations):

| Run | Reopenings after approval | `forge.build.next` calls | Re-runs of built tasks | Builder time in re-runs | Builder time in first runs |
|---|---|---|---|---|---|
| `20261020-123405-0d2a47` (B3c, 10 tasks) | 8 | 52 | 42 | 2.4 h | 1.1 h |
| `20261014-181155-5c1f5a` (A10, 14 tasks) | 1 | 22 | 8 | 2.9 h | — |

In B3c every reopening was a patch to a task not yet built, or to the title, Requirements or Gates.
Two of the run's six compactions happened during the build, with 469 KB of `build.next` results in
the context. `forge.plan.confirm` itself already kept `tasksCompleted`; the reset was the reopening's.

## Decision

**A patch takes the build back only to the first task whose text it changes.** The tasks of the
plan before and after the patch are parsed as the build parses them and compared by position; the
first position where the texts differ, or where one plan has a task and the other does not, is
task `k`. The run keeps `tasksCompleted = min(tasksCompleted, k − 1)`, the changed-file hand-over
of the tasks it keeps, and — when no built task was reached — the gate failure the next task owes.
A patch that changes no task, or only tasks above the built count, keeps the progress whole. Either
way the approval is withdrawn and the Builder session forgotten; the next task starts a fresh
session anyway ([0026](0026-a-builder-session-per-task-and-per-round.md)), and re-approval stays
required for every content change ([0003](0003-approval-through-the-orchestrator.md)).

**The comparison is by task text, not by Edit target.** The issue proposed reading the reached
task off the patch's targets. Two cases make the targets the wrong witness. Prose inserted after
task `t` — an `insertAfter` on task `t`, or an `insertBefore` on task `t + 1` — names a position
between two tasks, yet the build reads it as the tail of task `t`, so task `t` changed. And a target
inside the Approach section that is not a task — the section heading with whatever precedes task 1
— reaches no task's text at all. Comparing what the Builder will read answers both, and insertions
and deletions need no case of their own: every task after one shifts, so its position differs.
Task numbers are not compared, since the Builder reads the body, and neither are line breaks.

**A whole draft keeps nothing.** `forge.plan.write` and a review round handed a `planDraft` reset
the progress to zero as before: a whole draft carries no claim about which tasks it left alone, and
the skill reserves it for genuine rewrites. A round handed no draft reviews the plan on disk and
changes no text, so it keeps whatever the write or patch that put the plan there kept.

**A later change still reaches the built tasks.** The reopening used to do nothing on a run that
was not approved, which was safe only because the first reopening had already zeroed everything.
Now a patch that kept the progress leaves a run that is unapproved and still has built tasks, so a
later patch, write or round handed a draft takes back what it reaches whether or not an approval
stands.

**The user decides what a change outside the tasks invalidates.** No heuristic maps a Requirements
or Gates change to the tasks it affects. The patch answers with `tasksKept` and `firstTaskOwed`, the
`## Plan reopened` Flow entry names the tasks kept and the first task owed, and the skill has the
Orchestrator tell the user how many tasks the re-approval keeps. When the user says a built task has
to be built again, `forge.plan.confirm` takes `rebuildFrom`, a built task `1..tasksCompleted`,
accepted only with `approved: true`, and takes the progress back to it as a patch into that task
would.

## Considered and rejected

- **Targets as the witness.** See above: prose after a task changes that task, and the outline
  cannot show it without parsing the inserted text the way the comparison already does.
- **Keeping the reset for `forge.plan.write` only when the text changed tasks.** A write of a
  rewritten plan could be compared the same way, but the skill sends genuine rewrites there, and a
  rewrite that happens to leave task 1 byte-identical is not evidence that the plan around it still
  means what it did when task 1 was built.
- **Rebuilding on a Requirements change.** Which requirement bears on which built task is a
  judgment; the user makes it at re-approval, with the kept count in front of them.

## Consequences

A run with mid-build patches re-runs only the tasks the patches changed; a patch above the built
tasks re-runs none. A Builder that starts task `k` after a patch is told what tasks `1..k − 1`
changed, as it would have been without the patch. A Requirements or Gates change no longer rebuilds
anything unasked: a built task that no longer meets a changed requirement stays built until the user
says otherwise or code review finds it.
