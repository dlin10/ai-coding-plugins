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

**Join**:
A point in an execution that runs only after another execution has ended: an `await`, `Wait`,
`Join` or `WhenAll` on a handle proven to be that execution's, reached on every path including the
exceptional ones. A `WhenAny`, a wait that can return on a timeout, an event wait or a `Dispose()` is
not a join.
_Avoid_: wait, sync point, completion (a completion is the end of an execution, a join is where someone relies on it)

**Happens-before**:
The proven order of two execution events: a path through spawns, joins, continuations and startup
leads from one to the other, inside one instance tree whose root runs once. Two accesses with
happens-before in either direction never overlap; without it they may.
_Avoid_: sequential, ordered, before (a line above is not happens-before when a loop repeats it)

**Instance tree**:
A root that runs at most once without overlapping itself, together with the executions started from
it and from its once-running descendants; its spawn and join edges hold for every instance they
connect, which makes it the only place a **Happens-before** path is trusted. A periodic timer or a
`Parallel` body inside it still overlaps itself.
_Avoid_: task tree, call tree, thread tree

**Construction**:
A constructor or type initializer running to produce an object or prepare a type. What it does to
the object it produces, or a type initializer to its own type's statics, nobody else can see unless
it publishes the object; everything else it touches belongs to the execution that triggered it: the
request that builds a controller, the first resolution of a singleton, the host starting its hosted
services, the first use of a type.
_Avoid_: initialization (field initializers are part of a construction, not a separate thing), setup

**Reachable set**:
The method bodies a run analyzes: everything the call graph reaches from an execution root or a
spawn site; a body outside it cannot execute in the process and is neither lowered nor counted
against coverage.
_Avoid_: scope, analyzed code, hot code

**Process scope**:
One application as it runs in one OS process: an executable project together with the projects it
loads; a test project is not one. A static field or a singleton reached from two process scopes is
two objects.
_Avoid_: solution, app, deployment, app domain

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

**Bucket**:
One cell of the candidate index: the accesses of one process scope on one resource, and, apart from
them, a region's wildcard accesses. Two accesses are compared only when they share a bucket, when one
of them is in the wildcard bucket of the other's region, or when they lie on the same path of an
open region and of one of its closed regions; so the number of comparisons is bounded by the
buckets, never by the square of all accesses.
_Avoid_: partition, group (a group is a report term), region (a region holds many buckets)

**Finding**:
A candidate the single conflict procedure classified under exactly one rule, `DCA1001` to `DCA1004`,
with its evidence, confidence and provenance. Its identity is the rule, the resource and the
unordered pair of access sites; the executions that reach those sites are its occurrences, not part
of it, so one pair of access sites never yields two findings, however many roots reach it.
_Avoid_: race, bug, warning, diagnostic (a diagnostic is what a provider reports about coverage)

**Occurrence**:
One way a finding's pair of access sites is reached: a pair of execution roots with the call path by
which the analysis first reached each site from its root. A finding has one or more, at most one per
pair of roots; the report shows a few as representative locations and counts the rest.
_Avoid_: instance, duplicate, hit

**Fingerprint**:
The stable name of a finding across runs and edits: a hash of the rule, the containing symbols of
both access sites, the resource's shape, the operation kinds and roles, and the protection result
kind. Moving lines, renaming a local or adding a caller does not change it; a different rule,
resource, access site or protection result does. A finding group's fingerprint is the same over its
rule and resource.
_Avoid_: id (the run-local `F1` the narrative cites), hash, key

**Protection**:
The synchronizer identity and mode an access must hold, as proved on every path reaching it; a
protection that may hold is evidence, never proof.
_Avoid_: lock (one of its kinds), guard (a guard is a path condition), synchronization

**Confidence label**:
The `High`, `Medium` or `Low` band a finding's confidence score falls in; the report's three
finding sections are confidence labels, and a group's label decides whether its **Narrative** is
mandatory.
_Avoid_: severity (the impact of a finding, assessed separately and never deciding either of those), level, priority

### What the analysis could not settle

**Opaque call**:
A call into a method whose body the run does not have and whose effects no built-in semantics
describes; its effects are unknown, which is not the same as none.
_Avoid_: external call, library call (a library method the built-in semantics describe is not opaque), unknown call

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
The findings of one rule on one resource, shown in the report with their representative locations
and the sum of their occurrences.
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
- Two **Accesses** are compared only inside one **Bucket** or across the wildcard and open-region joins the bucket's definition names; an access in no bucket is compared with nothing.
- A **Finding** has one or more **Occurrences**; a new **Execution root** reaching its access sites adds an occurrence and changes neither the finding nor its **Fingerprint**.
- A file **Suppression** names a **Fingerprint** and nothing else.
- An **Execution root** starts one or more **Execution instances**; a **Spawn site** starts one from inside another.
- Two **Accesses** form a **Candidate** only when no **Happens-before** orders them; happens-before removes pairs and never adds one. Startup precedes every instance of every root. See `docs/adr/0008`.
- A **Fire-and-forget** spawn and an `async void` call have no **Join**.
- A **Construction** belongs to the **Execution instance** that triggers it; its accesses to the object it produces form no **Candidate** unless it publishes the object.
- An **Opaque call** becomes a **Semantic gap** only under the gap's conditions; every other opaque call is counted in **Coverage**.
- An **Execution root** belongs to one or more **Process scopes**; two **Accesses** form a **Candidate** only inside one **Process scope**.
- A **Finding group** has exactly one **Skeleton** and at most one **Narrative**; a group whose **Confidence label** is High or Medium makes the run `Incomplete` without a **Narrative**, a Low group does not.

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
- "High and Medium groups" in the SPEC could be read as severity or as confidence. Resolved: they are
  **Confidence labels**; severity is impact, is assessed separately, and neither orders the report's
  sections nor makes a narrative mandatory.
- "Material gap" was a status condition in the draft (a material gap forced `Incomplete`). Resolved:
  **Materiality** is a rank, `Incomplete` means a phase did not finish, and the queue of **Gap
  packets** has no budget other than the **Deadline**.
- Phase 1b showed a sharing key for each access (process, invocation, root scope, hosted instance).
  Resolved: it stood in for **Ownership** before regions existed; once a region carries its ownership
  and evidence chain, the sharing key is retired and ownership is the one account of who can reach a
  region.
- TD-106 in the SPEC put the execution roots into the fingerprint, and phase 2a made a finding of
  every pair of roots reaching one pair of access sites. Resolved: a **Finding** is the pair of
  access sites, the roots are its **Occurrences**, and the **Fingerprint** carries no root, so a
  new caller of a shared helper neither adds a finding nor invalidates a suppression. See
  `docs/adr/0007`.
- Phase 1a read the whole solution as one program. Resolved: a solution may hold several
  applications, and the unit two accesses must share is a **Process scope**. See `docs/adr/0005`.
