# Forge reference: writing the plan

Read it once the requirements are written and the selections settled, before the first draft.

## Writing the plan

Write the plan as markdown, in three parts: the requirements, the gates, and the tasks.

The tasks live under a heading spelled exactly `## Approach`. That heading is not a suggestion:
`PlanTasks` refuses a plan without exactly one of it, and `forge.plan.confirm` parses before it
writes anything, so the wrong heading fails at approval rather than later. Anything above
`## Approach` is not walked as tasks; on a fresh builder session it is sent verbatim as the
Vendor-bound Builder Brief. Keep transient interview notes, rejected alternatives, secrets, and
host values out of it. The section ends at the next `##` heading, so put the tasks last or expect
everything after that heading to be dropped.

If a Builder Brief is rejected as sensitive, remove the sensitive content from the plan and directly
re-approve the changed plan before retrying the Builder act.

Inside it, number the tasks `1.` to `N.` in order, one task per numbered item — a gap or a repeat is
refused outright. That numbering is what `forge.build.next` walks, so a task that is really three
tasks will be built as one.

Above it, `## Requirements` states what must be true when the work is done: one numbered requirement
per item, `R1` to `Rn`, and then what the run deliberately does not do. Requirements are the
interview's answers, not the implementation — what must become true, what must not change, how it
would be observed — so no file names, no symbols, no "how". The exclusions carry as much weight as
the requirements: they are what stops the critic demanding work the user already ruled out.

Every task ends with a **Gate** — the command that would show that task done — and cites the
requirements it serves. A check that belongs to no single task goes under `## Gates` instead,
numbered `G1` to `Gn`, each citing what it discharges: the test suite, a warnings-clean build, an
invariant spanning the whole change. Leave that section out when the task gates already cover
everything; a ceremonial gate is worse than none. Targeted requires a `## Gates` section containing
real numbered G entries for full verification and exactly one executable Fix gate. The plan Critic
checks this before approval when the Brief selects targeted; confirm neither selects gate mode nor
validates the Fix gate. A new or nonterminal targeted fix validates it before decisions or job
creation; a terminal replay uses its saved result even if the current plan's gate changed.

The Fix gate grammar is the first real, exactly spelled `## Gates` section, ending at the next
level-1 or level-2 heading outside a fence. At least one real numbered entry is required, such as
`1. **G1.**` (also `**G1**`, `**G1:**`, with optional punctuation after the bold label).
Exactly one line-start `**Fix gate**` label is required, with an optional colon inside or after the
bold text. A numbered-list prefix is allowed and the label is case-insensitive.
A bulleted item (`-` or `*`) does not count as a Fix gate label. Examples inside
fenced code do not count as
labels or G entries. After whitespace, the label must lead directly to non-empty inline code or a
closed fence of at least three backticks or tildes, closed by the same character with at least the
opening length. A language tag is allowed. Prose first, an empty command, an unclosed fence,
missing or duplicate labels, or no real G entry is invalid. Cite the R requirements the Fix gate
covers after its command; runtime does not parse those references. Do not infer a union of task
gates or silently rewrite scripts to create a short check.

**The server runs the gates, so write them to be run.** After every `forge.build.next` the server
executes the task's gate on the host — from `workspaceRoot`, in PowerShell, with the
`gateEnvironment` you pass at approval — and its exit code, not the builder's report, decides whether
the task counts. After a full-mode `forge.review.fix` it runs the executable G entries; after a
targeted fix it runs only the Fix gate. The Orchestrator owns full verification in targeted mode. A gate
is executable only when the code comes **first** after the label: `**Gate:** `dotnet test …` …`.
Prose before the backticks makes a task gate a condition — the server records it as `not executable`
and the Builder checks it. The Orchestrator checks full-plan conditions; a targeted Fix gate must
be executable.

**Only the server runs executable task gates**, including on retries. The act prompt communicates
the existing parser's classification to the Builder. Do not change ownership based on a command's
cost, move a task gate to `## Gates`, or require an existing plan to be revised and re-approved.
The Builder checks condition gates itself; on `failed` or `unavailable`, you check them.
Separate targeted Builder checks are optional, not a required build-and-test pass. The Builder
must not reproduce its executable gate under another command, wrapper, or sequence, even when
the gate is a single targeted test. The Builder also must not run executable fix gates or their
complete equivalent. Plan-wide gates follow the separate schedule below.

Write gate commands as follows:

- One PowerShell command line, placed immediately after `**Gate:**` (or `**G1.**`). Chain with
  `;` — a native command exiting non-zero ends the script with that code — and make a condition
  fail explicitly: `if (…) { exit 1 }`. Several commands may go in a fenced block right after the
  label, one per line; it runs as one script and stops at the first failing line.
- Reference the environment as `$env:NAME` and name every variable the gate needs; you will be
  asked for them at approval. Never write a value into the plan.
- A task that adds tests must prove they **exist**, not that the suite is green: a green suite at
  the old count is exactly what a builder that wrote no tests reports. Filter to the new class and
  count the names —
  `if ((dotnet test src/X.slnx --list-tests --filter "FullyQualifiedName~FooTests" | Select-String "FooTests\.").Count -lt 14) { exit 1 }; dotnet test src/X.slnx --filter "FullyQualifiedName~FooTests"`
  — with the count the task demands. Then say what to name them, so the count is checkable.
- A gate that needs something outside the workspace — a sibling checkout, a database — needs the
  path or the connection string in `gateEnvironment`.
- Codex Builders run without Codex's sandbox or approval prompts, including resumed retries.
  Plan their required edits directly, including top-level `.git`, `.codex`, and `.agents` when
  those edits belong to the approved scope. Access depends on the launching process's permissions.
  `builderRoots` is accepted for compatibility and does not control access. Codex Critic and Scout
  run without the sandbox too, and stay read-only by their contracts alone.

```markdown
Builder: codex / gpt-5.3-codex / high
Fix verification: gate=<full|targeted>; fullGate=<beforeNextRound|final>

## Requirements

1. **R1.** What must be true once the work is done.
2. **R2.** What must not change.

**Out of scope.** What this run deliberately does not do.

## Gates

1. **G1.** `dotnet test src/X.slnx --nologo` passes. (R1, R2)

**Fix gate:** `dotnet test src/X.slnx --filter "FullyQualifiedName~FooTests"` (R1; required only for gate=targeted, omit for gate=full)

## Approach

1. **First task.** What to change. **Gate:** `dotnet test src/X.slnx --filter "FullyQualifiedName~FooTests"` (R1)
2. **Second task.** … **Gate:** `if ($env:CD_TEST_SQL_CONN) { dotnet test … } else { exit 1 }` (R2)
```

Write every task as its change-specific delta. The Brief supplies stable context once on a fresh
builder session, while every task must still stand alone with its own task gate and requirement
references. Each task starts a fresh builder session: the Brief, the list of files earlier tasks
changed, then `# Task N of M` and the task's own text. Only a retry of the same task resumes that
session, so nothing a task needs may live only in an earlier task's conversation.

A task that introduces or changes a rule more than one place decides — a predicate, a
classification, an identity, a mapping — names the rule's owner, the one function every such place
routes through, lists those places, and names the axes of the rule's input it covers: value
channels, call kinds, object kinds, one target or several. Write "wherever X" or "every Y" only
beside the list of what reads X, or narrow it explicitly. The Impact pass is where the list comes
from; a rule given one owner stayed fixed in the measured runs, and a rule added at the one place a
finding named drew findings along each of its axes for rounds.

Scale the plan's depth inversely to the builder you selected. A strong model at high effort takes
goal-level tasks. The cheaper the model or the lower the effort, the smaller and more explicit each
task must be: name the files and the symbols, decide the edge cases and the error paths yourself,
and make each task's `Gate` count what the task must produce rather than only run what already
exists — leave nothing to the builder's judgement, because the builder you chose has less of it. Judge strength from the
vendor's own catalogue — the position in its newest-first list and the chosen effort — not from a
remembered model name.

State the builder's selection at the top of the plan, above `## Requirements`, in one line — vendor,
model, effort, and `Fast` when the user chose it. The critic judges the plan's depth against the
builder named there.

The draft is not ready for its first `forge.plan.write` until the matrix check below and the Impact
pass described under "Scout reconnaissance" have run against it, nor for its first review until the
Evidence check has.

### The matrix check

While you draft the `## Approach` — before the Impact pass and before the first `forge.plan.write`
— decide yourself whether the plan needs a matrix task: a task whose test walks every combination
of a rule's axes and checks each against an expectation written from the requirements, not from
the code. List every rule the plan introduces or changes — a predicate, a classification, a mapping,
the choice of a name or a form, the order in which reasons are given — and, for each, the axes its
answer depends on: the kind of value × the action × the execution it happens in, say. A rule needs
a matrix when any one of these holds:

- its answer depends on a combination of two or more axes, with about six or more values across
  them;
- the plan lists its cases one by one, and there are more than about ten;
- a neighbouring rule of the same kind already has a matrix in the codebase;
- a missed cell is a silent fault: an unsafe narrowing (an answer narrower than the truth where
  only a wider one is safe), a wrong refusal, a security gap.

A rule that needs one gets its matrix task in the first draft. Left to review, it comes back one
combination at a time: in one measured plan review the rounds ran 28, 17, 15, 11, 12, 14 and 9
findings, and from the third round on nearly every finding was an uncovered combination of one rule
or an edge where it contradicted another. The matrix arrived after the fifth round, and the next
critic found an error in its expectation table — caught in the plan, before any code. In an earlier
pass, a matrix of 1,729 probes did the work of the review rounds. Make the same check for a rule a
later revision introduces.

In the turn of the first `forge.plan.write`, beside the plan's path, say in one line which rules
you checked, what you decided for each and why: `Matrix check: Classify — value kind × action ×
execution, 11 values: matrix, task 4. ReasonOrder — one axis, 4 values: none.` When the plan
introduces or changes no rule, the line says so.

### The matrix task

The critic judges a matrix task with the rest of the plan, so the plan spells it out:

- It is a task of its own, with its own test class in the ordinary suite — never behind an
  environment flag.
- It lists every axis and every value of each, and every excluded combination — what the test's
  `Allowed` filter drops — with the reason it is left out.
- It writes the expectation table into the plan from the requirements — by axis values where the
  cells are too many to list — each answer citing its R-item. In the code, every branch of
  `Expected(cell)` cites a requirement, never the code under test.
- It lists the cells known to be wider: each by name, with the reason its expectation cannot be
  reached without a change this plan does not make, such as one to the engine the rule runs on.
  Such a cell must come out wider than its expectation, never exact, and a name on the list that
  matches no cell fails the test. For a rule whose answers have no safe side, the list is empty and
  every cell must be exact.
- Its tests assert that no cell narrows unsafely, that every cell off the wider list gets its
  expected answer, and that every value of every axis occurs in some cell.
- Every reason on the wider list becomes an open question in the project's own catalogue or design
  document, and the plan names the task that writes it.
- It replaces the one-by-one cases of the same rule in other tasks rather than repeating them. Only
  cases outside its axes stay listed by hand.
