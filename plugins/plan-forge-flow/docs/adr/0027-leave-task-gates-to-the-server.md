# Leave executable task gates to the server

Issue #134 measured a Builder running the same task gate twice before the server ran it once more.
The independent server check is necessary; the duplicate Builder runs consume the host's deadline.

The existing parser decides ownership. An executable command immediately after the task's
`**Gate:**` belongs to the server, whatever its cost or breadth. A condition belongs to the Builder,
with the Orchestrator checking it on failed or unavailable verification. The act prompt tells the
Builder the parser's answer. It must not run an executable gate on the first attempt or a retry,
nor reproduce the complete check through another command, wrapper, or sequence.

Separate targeted checks are optional during implementation or diagnosis. The Builder reports
their actual results. If it ran no checks because the gate is reserved for the server, it reports
`verification.outcome: unavailable` with that explicit reason. A completed implementation remains
`done`; intentionally leaving the gate to the server is neither failed verification nor blocked
implementation. The wire schema needs no new outcome.

The server still executes the current task's gate, even without a `## Gates` section. Failure
withholds the task and briefs the retry with the command, exit code and output. The Builder fixes
the cause without running that gate, and the server checks again. Existing eligibility, killed and
cut-short turn handling, and Orchestrator recovery when the server cannot run a check stay intact.

Plan-wide `## Gates` keep their separate schedule: the Orchestrator runs them after the last task,
before code review; the server runs their executable commands after a review-fix turn. They must
not block an earlier build task on work a later task owns or replace the task's own independent
check. Existing plans need no migration or re-approval.

This is an instruction contract, not shell-command interception. The log records what a Builder
actually ran; a live run is needed to establish whether a vendor follows the instruction.
