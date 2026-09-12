# Concurrency Hunter — domain language

The vocabulary the server, the skill, the tool schemas and the report must use. Written during the
interview that scoped the first version; extended as later decisions land.

## Language

### The run

**Run**:
One invocation of the command, from the moment the skill starts it to its terminal status.
_Avoid_: scan, session, invocation, job

**Skill**:
The procedure the host's agent follows; it is the run's only orchestrator.
_Avoid_: adapter, plugin adapter, command adapter, workflow

**Server**:
The analyzer the skill reaches over MCP; it holds the run's state and its deadline, and never
starts AI work itself.
_Avoid_: engine (the analysis inside the server is not a separate thing the skill talks to), analyzer process, daemon

**Deadline**:
The fixed limit, measured from the run's start, after which the server accepts no AI result and
hands out no AI work.
_Avoid_: timeout, AI budget, time budget

**Late response**:
An AI result that reaches the server after the deadline or after cancellation; it is recorded as
late and never applied.
_Avoid_: stale response, orphan response

**Terminal status**:
The one word a run ends in: `CompleteWithFindings`, `CompleteClean`, `Incomplete`, `Failed` or
`Cancelled`.
_Avoid_: result, outcome, exit code

### Execution

**Execution root**:
A point the framework or the runtime can start an independent execution from: an HTTP action, a
hosted service's `ExecuteAsync`, a timer callback, a gRPC method.
_Avoid_: entry point (cache-detective's word; a root is also where a spawn begins, which an entry point never is), handler, thread

**Spawn site**:
A call in user code that starts a new execution from the current one: `Task.Run`, `Thread.Start`,
a `Parallel` loop body, a fire-and-forget task, an `async void` call, a queued work item, a
continuation.
_Avoid_: fork, thread creation, background call

**Execution instance**:
One logical execution of a root or of a spawn; it is not an OS thread, and an `await` inside it
does not end it.
_Avoid_: thread, request, task (a `Task` is a handle the code holds, not the execution)

**Fire-and-forget**:
A task-returning call whose handle reaches no `await`, `Wait`, `WhenAll` or `WhenAny` on any path;
a handle stored and awaited elsewhere is a spawn with a join, not fire-and-forget.
_Avoid_: unawaited call, dropped task, background task

**Reachable set**:
The method bodies a run analyzes: everything the call graph reaches from an execution root or a
spawn site; a body outside it cannot execute in the process and is neither lowered nor counted
against coverage.
_Avoid_: scope, analyzed code, hot code

### The heap

**Heap region**:
The identity of one object or one set of objects: an allocation site with its creation context, a
static storage, a modeled DI instance, a symbolic parameter or receiver, or a bounded summary region.
_Avoid_: object, instance, allocation (a region may stand for many runtime objects)

**Access path**:
The field-by-field route from a heap region to a location, with a selector where the location is an
element of an array, span or collection.
_Avoid_: expression, member chain, variable (a local's name is never part of a path)

**Resource**:
A heap region plus an access path: the unit two accesses are compared on.
_Avoid_: shared state, variable, field (a field of two different regions is two resources)

**Ownership**:
What the analysis proved about who can reach a region: `Owned`, `ThreadConfined`, `Escaped`, `Shared`
or `Unknown`, each with its evidence chain.
_Avoid_: scope, lifetime (a DI lifetime is evidence for ownership, never the same thing)

### Conflicts

**Access**:
One read, write, read-modify-write, atomic or compound operation on a resource, carried with the
execution instance it runs in, the protection held, and the guard it runs under.
_Avoid_: use, reference, touch

**Candidate**:
A pair of accesses that passed the cheap resource, overlap and operation filters and still needs
refinement.
_Avoid_: potential race, pair, hit

**Finding**:
A candidate the single conflict procedure classified under exactly one rule, `DCA1001` to `DCA1004`,
with its evidence, confidence and provenance; one pair of accesses never yields two findings.
_Avoid_: race, bug, warning, diagnostic (a diagnostic is what a provider reports about coverage)

**Protection**:
The synchronizer identity and mode an access must hold, as proved on every path reaching it; a
protection that may hold is evidence, never proof.
_Avoid_: lock (one of its kinds), guard (a guard is a path condition), synchronization

### What the analysis could not settle

**Semantic gap**:
A call the deterministic analysis could not reduce and that touches a mutable non-owned region, a
delegate, or feeds a shared region; a reflection, `dynamic`, unresolved-dispatch or unknown-library
call that touches none of those is not a gap.
_Avoid_: unresolved (cache-detective's word for a different mechanism), unknown call, hole

**Gap packet**:
The bounded question the server hands the skill for one semantic gap: the call sites, the symbols,
the candidate targets, the constraints and the reason.
_Avoid_: prompt, request, task

**Inferred fact**:
An AI hypothesis about a target, effect, capture, escape, spawn or returned alias that passed the
server's validation and is marked as AI-derived wherever it is used.
_Avoid_: annotation (cache-detective's word, which carries no validation), assumption, guess

**Materiality**:
The rank of a semantic gap by how many roots and shared regions it can touch; it orders the queue
and the coverage section, and it never decides a terminal status.
_Avoid_: severity, priority, blocking

**Coverage**:
The report's account of what the run analyzed and what it could not: loaded and skipped projects,
unsupported bodies, semantic gaps by materiality, and which built-in semantics applied.
_Avoid_: scope, completeness score

### The report

**Finding group**:
One semantic cause in the report: the findings that share a resource and a root cause, shown with
representative locations and an occurrence count.
_Avoid_: cluster, issue, bucket (a bucket is the candidate index's word)

**Skeleton**:
The part of the report the server renders on its own: every finding's paths, evidence, protection
result, event sequence and fingerprint, plus status, coverage and diagnostics.
_Avoid_: template, draft, structured report (that is `findings.json`)

**Narrative**:
The text the AI adds to one finding group or to the run: the interleaving in words, why the
protection is insufficient, and the fix suggestions; it cites evidence and never restates it.
_Avoid_: explanation, summary (the executive summary is the run's narrative), commentary

**Report bundle**:
The three files one run leaves behind: `report.md`, `findings.json` and `run-metadata.json`.
_Avoid_: output, artifacts, results folder

**Suppression**:
A user's decision to hide one finding from the report, with a mandatory reason: an attribute on the
method or type holding one of the accesses, or a fingerprint entry in the repository's suppression
file; it changes presentation only and is never evidence of safety.
_Avoid_: baseline, exclusion, ignore, whitelist

## Relationships

- A **Run** is orchestrated by the **Skill** and held by the **Server**; nothing else takes part in it.
- A **Run** has exactly one **Deadline** and ends in exactly one **Terminal status**.
- A **Late response** belongs to a **Run** but changes nothing in it.
- A **Resource** belongs to exactly one **Heap region**; a **Heap region** has exactly one **Ownership** at a time.
- A **Semantic gap** yields exactly one **Gap packet** and zero or more **Inferred facts**; every gap, resolved or not, appears in **Coverage**.
- A **Semantic gap** never changes a **Terminal status**; only a phase that did not finish does.
- Two **Accesses** on one **Resource** in two **Execution instances** that may overlap form a **Candidate**; a **Candidate** becomes at most one **Finding**.
- An **Execution root** starts one or more **Execution instances**; a **Spawn site** starts one from inside another.
- A **Finding group** has exactly one **Skeleton** and at most one **Narrative**; a High or Medium group without a **Narrative** makes the run `Incomplete`, a Low group without one does not.

## Example dialogue

> **Dev:** "When the **Deadline** hits, who kills the subagents the **Skill** started?"
> **Domain expert:** "Nobody can, reliably. The **Server** stops handing out work and marks whatever
> arrives afterwards a **Late response**. The **Skill** is told not to start more. That is the whole
> guarantee, and the **Terminal status** says `Incomplete` with the reason."

## Flagged ambiguities

- The SPEC draft used "plugin adapter" for a program that waits for the run's terminal state and
  cancels AI sessions. Resolved: no such program exists on any host; the **Skill** is the
  orchestrator and the **Server** is a state machine. See `docs/adr/0001`.
- "Sharing is decided by DI lifetime" was proposed and rejected: a lifetime is one source of evidence
  for a region's **Ownership**, and objects that never pass through DI need the same identity. See
  `docs/adr/0002`.
- "Material gap" was a status condition in the draft (a material gap forced `Incomplete`). Resolved:
  **Materiality** is a rank, `Incomplete` means a phase did not finish, and the queue of **Gap
  packets** has no budget other than the **Deadline**.
