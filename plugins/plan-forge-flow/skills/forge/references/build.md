# Forge reference: the build

Read it after approval, before the first `forge.build.next`.

## The gate decides the task; the builder's verification is its own word

Every build and fix result carries two accounts beside its `status`. `verification` is the
builder's: `outcome` is `passed`, `failed` or `unavailable`, and `evidence` says what it ran and what
it showed — or quotes the refusal when nothing could run. `gate` is the server's: it ran the task's
gate command on the host after the builder's turn, and reports `outcome`, the `command`, its
`exitCode`, the tail of its `output`, and `seconds`. The code review reads the diff, not either of
them. Fix responses add host-owned `gateMode`: full runs executable G entries and can close its
findings; targeted runs only the Fix gate and returns current `pendingFullGateFindingIds` for that
attempt, including []. Short success leaves those findings unresolved and pending. Neither the
Builder's report nor a Critic assessment verifies them. Full verification of targeted fixes follows
the code-review schedule in `code-review.md` at either saved timing, with hostVerified evidence for closure.

If the Builder ran no checks because an executable gate belongs to the server, its verification
is `unavailable` with that explicit reason, not `failed`. Fully implemented work is still `done`;
the server runs its gate as usual. Optional Builder checks report only their own actual results,
never a claim that the gate passed. A genuine inability to check still reports its exact reason.

Declare `phase: "build"` before each build act — `forge.build.next`, or
`forge.work.start` with act `build.next` — a retry of the same task included, so each record holds one
attempt at one task.

Read `gate.outcome` first:

- **`passed`** — the task counts, whatever the builder said about its own verification, and whatever
  it said about its own `status`. A builder that reported `unavailable` because its environment
  could not run the gate has been checked for you; a builder that reported `blocked` for the same reason has
  had `status` rewritten to `done`, because the gate is the proof it was missing. Its `verification`
  still says what it could not check, so read that before you narrate the task as a clean success.
- **`failed`** or **`timeout`** — the `status` is `gate_failed`, `tasksCompleted` did not move, and
  the next `forge.build.next` retries the same task with the gate's command, exit code and output in
  front of the builder. The Builder fixes the cause without running the gate; separate diagnostic
  checks are optional, and the server repeats the gate. Call it again. If the same gate fails twice
  more, stop and show the user the output rather than spending a fourth turn: the gate may be wrong, the environment may be missing
  a variable, or the task may be beyond the builder. A full-mode G failure or targeted Fix gate
  failure after `forge.review.fix` is the same signal with no task to withhold — the next fix
  carries it — so do not start the next
  `forge.review.code` round on a `gate_failed` fix without deciding what to do about it.
- **`not_executable`** — the gate is a condition rather than a command, or the task states none.
  Only here does the builder's `verification` decide: on `unavailable`, run the check yourself and
  record what you ran and saw through `forge.log.append` before the next act; on `failed`, do not
  advance past it — verify yourself, and either fix forward or stop and ask the user.
- **`not_run`** — the builder reported `blocked` after a verification that `failed`, so it ran the
  check itself and watched it fail and there is nothing left for a gate to settle; or the host has no
  PowerShell. The second case is the environment's fault, not the task's: say so and treat the task
  as `not_executable`. A `blocked` turn whose verification was `unavailable` never lands here — the
  host runs the gate for it, and the outcome is one of the three above.
- **`not_run` with `status` `background_killed`** — the builder ended its turn while a command it
  had started in the background was still running, and the session killed it; `gate.detail` names
  the command. By the builder's own account the work was unfinished, so no gate ran and
  `tasksCompleted` did not move. Call `forge.build.next` again: the retry is told what was killed and
  to run it in the foreground. If it happens twice for the same command, the command probably
  outlasts the worker's 30-minute foreground limit — split the task, or run the command yourself and
  record it with `forge.log.append`. Only a claude builder reports this.

Say the gate's outcome in your one line of narration — a task whose gate failed, or whose gate
nobody could run, must never read like a clean `done` in the chat.

### When the host times the call out

`forge.build.next` and `forge.review.fix` can run longer than the host's clock — an hour on Claude
Code and Codex — and the host then cancels the call and kills the builder wherever it had got to.
You get an error such as `MCP server "plan-forge-flow" timed out after 3600s` and no result.

**It is not an act that changed nothing.** The builder may have finished the work and been killed
before it could report, so before you do anything else read the run's own account of it:

- the flow log has a `cut short` entry naming the files the builder had written, which are still on
  disk;
- the attempted `fixFindingIds` remain unresolved in the decision ledger, and the Flow audit keeps
  the cut-short attempt;
- the task was not counted and no gate ran, so this interrupted turn verifies nothing. Earlier
  pending marks and the last completed attempt result remain intact; a first interruption has no
  completed result to replay.

Do **not** invent a fresh attempt or tell the user the work was lost before you have looked. Call the
same act again with the same `fixAttemptId`, exact `fixFindingIds` and `gate`: it resumes the same
Builder session and preserves the attempt's immutable binding. A full-mode successful fix can close
findings; targeted success leaves them pending full verification. If the attempt already reached
terminal success, the retry returns its saved result without another Builder or gate. If you need to
know where the tree stands first, `forge.status` gives the ledger summary and drift, and `git diff`
gives the content. Should the retry time out the same way, the work is too big for one call — split
the remaining work into a new legal attempt, or run the checks yourself and record them with
`forge.log.append`.
