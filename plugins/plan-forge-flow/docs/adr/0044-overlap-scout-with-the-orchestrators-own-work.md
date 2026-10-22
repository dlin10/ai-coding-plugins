---
status: accepted
---

# Overlap Scout with the Orchestrator's own work through jobs

Limits [0043](0043-remove-cursor-as-host-and-vendor.md) where it keeps the job tools for the Builder
acts alone, and [0006](0006-worker-acts-as-jobs-on-the-cursor-host.md) where it allows one job per
Run.

## Context

Every Scout call blocked the Orchestrator. On the concurrency-hunter runs of 12–20 October 2026
(issue #171) Scout took 17 to 85 minutes of a run's wall time: one or two orientation calls of 8 to
36 minutes before the first domain question, and an Impact pass with follow-ups of 15 to 50 minutes
after the last one. In run `20261020-123405-0d2a47` the Critic and Builder questions ran in the four
minutes before the Impact pass started, and the plan went out four minutes after it returned.

Neither call can overlap the user's answers: the orientation decides what to ask, and the Impact
pass needs the Change points of a drafted approach. Each can overlap the Orchestrator's own work —
the questions that do not depend on the lay of the land, and the vendor questions and plan prose
that do not depend on the pass's answer. A host lets the Orchestrator keep working only while no
tool call of its own is open, so the call has to return before the Scout turn ends: that is a job.
The server ran Scout as a job for Cursor until 0.59.0 removed both (0043).

Two things held only because Scout blocked the Orchestrator:

- A Scout turn wrote `state.json` from the copy it read when it started, minutes earlier, because
  nothing else wrote in between. Beside the Orchestrator's own calls that write takes back what
  they wrote — the user's instructions, a new Scout choice. Two turns finishing together both took
  the number after the counter they had read, and the strictly increasing check of
  `scout-answers.json` refused the second answer, which was lost.
- The Run had one Scout session anchor, and one turn at a time wrote it.

## Decision

From 0.60.0 `forge.work.start` runs `scout` on every host, beside `build.next` and `review.fix`. It
takes `question` and `sessionMode` and none of the Builder's arguments; the vendor, model, effort and
Fast come from the persisted Scout selection as on `forge.scout.run`.

- **Which jobs share a Run.** A fresh Scout job runs beside other fresh Scout jobs; a `continue`
  Scout job and a Builder act each run alone. A start that may not run answers `started: false`
  with the oldest running job as `jobId` and every running job in `activeJobIds`. `forge.status`
  reports every running job, oldest first, as `activeJobs`.
- **The anchor.** Scout turns of one Run that overlap — fresh jobs side by side, or a direct
  `forge.scout.run` beside a job — each leave the Run with no session anchor: none of their sessions
  is the one investigation a later `continue` would mean. A later follow-up is `fresh`, or names in
  its question what it continues from. A direct `continue` is refused while a Scout job runs, naming
  the jobs. The server keeps the turns in flight per Run (`ScoutFlight`), for both routes.
- **The state.** A Scout turn's writes read the state as it stands at the write
  (`RunDirectory.UpdateState`) and change only its Scout choice — and not even that when the user
  chose another Scout while it ran, because its session belongs to the choice it ran with. The
  number and the append happen under one lock per Run, so answers are numbered in the order they
  finish.
- **The skill.** The orientation question starts as a job right after the Scout selection, while
  the Orchestrator asks the opening questions that do not depend on it. The Impact pass starts as a
  job once the approach is sketched and its matrix check made; while it runs the Orchestrator asks
  the Critic and Builder questions and writes the requirements, gates and tasks, and it fetches the
  answer and folds it in before the first `forge.plan.write`. An Impact pass naming more than six
  Change points is split by area into fresh jobs run side by side. A follow-up whose answer the next
  question to the user depends on stays a direct call. The Evidence check is unchanged.

Rejected: letting a fresh job start beside a running `continue`. The continued session is an
investigation the Orchestrator chose to extend, and a fresh question beside it would take its
anchor away. Rejected: a Builder act beside Scout jobs. Nothing in the protocol needs it — Scout
precedes the plan, the Builder follows its approval — and a Builder turn also writes the state it
read when it started.

## Consequences

- Plan review and code review keep only their one-call tools.
- A Run can have several running jobs. A job id still names one job; poll, cancel and fetch are
  unchanged, and a fetched Scout job carries `documents.scout` exactly as a direct call does.
- An Orchestrator that loses track of parallel jobs, after a compaction or otherwise, finds them in
  `forge.status`'s `activeJobs`. Their answers land in `scout-answers.json` whether or not it fetches
  them.
- The other writers of `state.json` still read it and write it back in two steps, so a write that
  lands between them can still be lost. What changed is that a Scout turn no longer holds its copy
  for minutes. Another server process writing the same Run is as unordered as before.
- The gain is measured on the next runs, against issue #171's table: the Scout wall time that
  overlapped Orchestrator work, the longest single Scout call, and the interview from `forge.begin`
  to the first `forge.plan.review`.
