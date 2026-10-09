# Leave targeted fixes pending until full host verification

## Context

Issue #144 measured repeated full-suite checks dominating code review: a full gate took
33–35 minutes, and multiple fix batches repeated it for about two hours in one round. A short
check can provide useful feedback after each batch, but it cannot establish that the complete
change passes regression checks. A regression may concern an existing finding, a new defect, or
the execution environment; its failing test does not identify which earlier finding to reopen.

Executable task gates remain server-owned as decided in
[ADR-0027](0027-leave-task-gates-to-the-server.md). This decision changes the optional verification
workflow for code-review fixes, not the completion checks of implementation tasks.

## Decision

Preserve full verification after each fix attempt as the default. An explicit targeted choice
uses one executable `**Fix gate:**` under `## Gates` instead of the complete-plan gates. Its
success leaves the affected findings pending full verification; it does not close them.

Before drafting the plan, the interview asks one question: run full verification after each
Builder fix turn, or only at the end of the run? The first answer keeps `gate=full`; the second
selects `gate=targeted` and `fullGate=final`. The Orchestrator records that choice in the Brief
and passes it to confirmation and fix calls. There is no separate targeted-mode question and
no repeated interview between fixes. The question uses the conversation's language; the Brief's
choice label uses the plan's language and explicitly names gate and fullGate. Explicitly requested
targeted verification before the next round remains supported.

In targeted mode, the Orchestrator runs all complete-plan gates once after the current round's
fixes before the next Critic (`beforeNextRound`, the default timing), or after code review settles
(`final`). Both timings use the Orchestrator's host execution. There is no additional server tool
or act for launching those full checks. The existing `hostVerified` decision records successful
host verification with evidence; the server does not independently verify that assertion.

An omitted `fullGate` on a later approved confirmation preserves the previous choice. An
explicit change is allowed while findings are pending and does not verify or close them. Saved
status timing is authoritative after reconfirm; a timing-only change does not rewrite the plan
or use plan.write. Later confirms omit fullGate until another explicit user decision, including
approval after a future revision.
Omitted gate environment and Builder-root arguments also preserve their saved values; explicit
empty values clear them. A successful confirmation still clears the global gate-failure brief,
while attempt history remains available for retry. A refused confirmation leaves plan, state
and ledger unchanged, including an approval already recorded; diagnostic logging is allowed.

Pending findings and their covering fix attempts remain visible in persisted state, status,
Critic input and the Flow log. A successful short-check attempt can replay its result without
another Builder turn or gate while full verification is still pending. A Critic assessment alone
cannot verify pending findings. In `final`, an approving Critic does not establish full verification.
Terminal replay does not require a valid Fix gate in the current plan, since it executes no
check. A new or nonterminal targeted attempt validates that gate before decisions or work.

A retry or new fix preserves an earlier pending marker until it succeeds. A successful targeted
attempt replaces that marker with its own identity; successful full verification closes the
finding. Failure, timeout, cancellation, interruption or refusal before the Builder starts does
not erase the earlier pending verification.

The ledger uses schema version 3 because every fix attempt now requires an explicit gate mode
and pending verification changes completion semantics. Version-2 snapshots remain readable without
rewriting them; their attempts mean `full`, and a subsequent mutation writes version 3. New-format
fields under version 2 are rejected. The sole saved mode is attempt.GateMode, mandatory in v3;
host result annotations are not stored in LastResult. New/nonterminal attempts save their mode/ID
binding before sensitive-input checks or vendor startup, with Terminal=false, previous LastResult
and pending marks preserved. Interruption retains the last completed result, or null if none exists. This supersedes the format choice in ADR-0022; the optional
extensions in ADR-0025 did not require the mandatory mode or these semantics.

A failed or unavailable full check leaves pending fixes unverified. The Orchestrator records
the command, covered attempts, exit code and output tail through forge.log.append, then decides which existing findings to fix or whether
to raise a new finding. The server does not automatically reopen or choose findings after such a
failure. Send failure output in the next fix note or raise what/reason; the server's failure brief
does not contain host checks. The Orchestrator may also correct the environment and repeat the full
check. Do not repeat a successful full run without edits; full-mode executable results may be
reused as evidence, with conditions confirmed separately.

Full verification is required before reporting a successful run at either timing. A user stop
or a declined extra review round ends an unfinished run with pending verification reported
explicitly. A final regression requiring code changes returns to the ordinary fix/review cycle,
then repeats full verification; an environment-only repair repeats the checks without another
Critic. A blocked `beforeNextRound` run can repair its environment, explicitly switch to `final`,
or consciously defer/reject through an explicit decision that reports unverified fixes. At the cap,
ask the existing extra-round question before returning to review for a code repair. A user stop
never forces verification against that stop.

Both gate and fullGate are case-sensitive; empty, whitespace and unknown inputs are refused before
mutation, decisions, jobs or Workers, including fullGate with approved=false. A corrupted saved
fullGate (including non-string JSON) stops state reads without defaulting or overwriting; saved
validation precedes input timing validation. Log the failure through the available log.append and
start a new run, without manual state edits. Auxiliary models/log/poll/cancel/fetch gain no state
checks. Background completion matches direct responses; fetch keeps a historical snapshot, while
new fix replay and status expose current pending state.

Use an explicit short check instead of deriving a union of task gates. The author of the plan
owns whether that check is meaningful. The server's first-failure behavior does not inspect or
rewrite stages inside arbitrary invoked scripts.

## Consequences

Independent fix batches can receive short feedback without paying for a full suite each time.
Pending state preserves the distinction between an implemented fix and a fully verified fix.
The Orchestrator owns full-check timing, failure interpretation and evidence, consistent with
the existing host-verification trust boundary.

Targeted mode is unsuitable when the plan has no meaningful short check or the repaired rule
spans many classes whose joint behavior requires the complete regression suite. For a separate
batch use a new explicit full-mode attempt and explain why, also when a Fix gate is wrong or
repeatedly fails. Do not silently change the approved plan; a plan change uses ordinary revision,
review and approval with its existing progress reset. Final timing can
spend Critic rounds on code that later fails regression checks. Pending verification must survive
restart and must remain visible until explicit verification or another legal Orchestrator decision.
