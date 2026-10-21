---
status: accepted
---

# Patch the plan against a snapshot outline, and answer a replay with a conflict

The reset of the build progress in the write's lifecycle is amended by
[0039](0039-a-patch-keeps-the-built-tasks-it-does-not-change.md): a patch keeps the built tasks
before the first task whose text it changes.

Issue #150. Revising a plan between review rounds meant sending it whole: `forge.plan.write` takes
the complete draft every time. Three Concurrency Hunter runs of 6–13 October 2026 made 23 writes
between them, of plans that had grown to 37,129, 92,291 and 83,142 characters, and most revisions
change a task or two. Telemetry measures Workers, not the Orchestrator, so how much of each draft
was unchanged cannot be recovered; what is certain is that the whole of it was generated and sent
again.

## Decision

Amends [0014](0014-writing-the-plan-is-its-own-call.md): `forge.plan.write` no longer answers
with documents and nothing else — it also answers with the outline of the plan it wrote.

**A plan may be revised in place by a Plan patch** — `forge.plan.patch` — beside the whole-draft
`forge.plan.write`, which stays.

- **The plan is cut into Edit targets that never overlap**: the preamble before the first heading,
  each heading with its body up to the next target, and, under exactly one build-recognized
  `## Approach`, each numbered task by the same boundaries `PlanTasks` walks. Outside Approach,
  heading lines begin at column 0 with one to six `#` followed by space or tab. The build's
  patterns run on CRLF-normalized text and positions map back to the original: a task starts at
  the beginning of the line holding its first digit, the Approach heading ends at the first task
  or the section end, and the section-end line starts a heading target whatever it looks like.
  Other headings inside the section are not targets; when the build recognizes zero or multiple
  Approach headings, only heading lines divide the plan. Concatenating the targets reproduces the
  plan text exactly, including line breaks, so an untouched target is untouched by construction.
  Two edits can only collide on one target, or at one position between two targets — `insertAfter`
  a target and `insertBefore` the next one — never on a part of a target.
- **Fenced code blocks are not considered**, as neither the task parser nor the run-wide gate
  parser considers them. Outside the Approach section, a heading line at column 0 inside a code
  block starts a target and cuts the block in two; inside the section, where only tasks are
  targets, a code block is cut wherever the build itself sees a task number or the section's end.
  That is coarse but safe: a target is replaced whole and the result is checked whole.
  The alternative — tracking fences — needed the Fix-gate grammar's rule for a fence opened on the
  same line as a bold label as well, and a single missed opening would turn the closing fence into
  an opening and fold every heading after it, the Approach included, into one target.
- **A target is named by an ID that holds within one snapshot** — its position — beside its own
  hash, and the patch names the hash of the whole plan it was written against. The plan hash fixes
  the snapshot, so the ID is unambiguous, duplicate headings included; the target hash catches an
  Orchestrator that copied the wrong ID. No ID survives a change to the plan, and none has to: each
  `forge.plan.write` or `forge.plan.patch` call answers with the outline of what it wrote (null for a
  whole-draft write of blank plan text), and `forge.plan.outline` gives it again when that answer
  was lost; a missing or blank plan is refused.
- **A patch is all or nothing.** Its edits are applied to one copy in memory and the whole result
  is checked before anything is written. Two replacements/deletions on one target, two insertions
  at the same position, or an insertion anchored on a target the patch deletes are refused.
  A patch that changes nothing or leaves blank text is refused rather than taking an approval back.
  Blank means whitespace after any byte-order marks at the start are set aside.
- **Its result goes through the write's own lifecycle**: the approval and the build progress are
  taken back by the same owner, `PlanReview.Reopen`, before the file is replaced, so a crash between
  the two leaves the flag off over the old text and never on over the new.

## Why a replay gets a conflict, not its saved result

Every other retried call here carries a key — `decisionBatchId`, `fixAttemptId` — and a retry
under it returns the saved result. A patch carries none. A patch whose answer was lost and which is
sent again finds the plan already changed, by itself, and is refused as written against a stale
base, with the current hash; the Orchestrator reads the outline and decides. That is one more call
after a rare loss, and it saves a record of applied patches, its file, and the window between
writing the plan and writing that record in which a replay would be a conflict anyway. Equal text
is never taken as proof that a patch landed.

## Considered and rejected

- **Sections that contain their subsections.** One edit would replace a whole `##` with its `###`s,
  but targets would nest, and every pair of edits would need checking for a parent and its child.
- **Headings only, tasks edited by replacing `## Approach`.** The commonest revision touches one
  task, and the Approach is the plan's largest section.
- **A target named by its heading text.** Readable, but the model copies the text, a stray space
  becomes "not found", and tasks would need an addressing scheme of their own.
- **The outline in `forge.status`.** Status is called often and for other reasons; a long plan's
  outline would ride on every answer.
- **Approval by hash instead of the whole plan.** `forge.plan.confirm` and `forge.plan.show` still
  take the plan whole. On a Text host the plan is shown to the user in full before approval anyway,
  and the approval contract of docs/adr/0003 is out of this decision's scope.

## Consequences

A patch is checked more strictly than a write in one respect: a plan that parsed as tasks before
the patch must still parse after it, including acceptance of the task numbers as ASCII-digit
numbers within range, so a patch cannot leave the tasks misnumbered or lose
`## Approach`, while a write can until `forge.plan.confirm`. Text a patch inserts is converted to
the plan's line breaks — CRLF only when the plan has at least one break and every break is CRLF,
LF otherwise — and ends with a line break, keeping supplied trailing blank lines. A separator in
that style is added between pieces when the preceding piece lacks a final break; untouched targets
keep their exact text. The plan a patch reads is the UTF-8 text every reader of `PLAN.md` sees,
with a leading byte-order mark dropped. Any byte-order marks at the complete result's start are
removed before checking, writing, hashing or measuring it. Whole-draft writes keep storing the draft
as given. Two Orchestrators patching one run see each other only as stale bases: the server takes no lock, and the plan file is replaced atomically, so the later of two
writes from the same base is the one refused, unless both read the file before either replaced it —
the window `AtomicFile` leaves open for every run file. This is no multi-file transaction: a
refusal leaves the plan, state, ledger and Flow log byte for byte; a failure before replacement
reports the plan unchanged and whether approval was withdrawn. A failure of the patch's Flow entry
after replacement reports the plan patched and gives its new hash. The diagnostic log is best
effort and does not change the outcome.
