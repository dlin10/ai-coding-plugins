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

**Unknown execution**:
An execution the analysis knows will run some code without knowing which root or spawn runs it —
the enumeration of an iterator that escaped to where the analysis cannot follow it, or the
invocation of a delegate handed to an **Opaque call**. It may overlap every execution of its
process scope, itself included, and nothing orders it. The one exception is a delegate handed at
one site by an execution that runs once and runs that site once: the call may run it many times,
but one at a time, as a spawn from such a site does not overlap itself.
_Avoid_: background execution, anonymous thread, somewhere

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

**Selector**:
What an access path's last step picks out of an array, span or collection: a proven constant, a
symbolic expression or conservative range with the guards that bound it, or unknown. Two selectors
tell two locations apart only when their values are proven never to meet. A slice's selector is
counted in the storage the slice is cut from: the framework's spans are taken at their word, and any
other type's offset counts only where its own code proves it over values that cannot change once the
slice is constructed.
_Avoid_: index, key, subscript (an index is one shape a selector takes)

**Collection structure**:
A collection's own shape — its count, its slots and their order — as a resource separate from the
storage of any element. Two mutations of one collection conflict on its structure however far apart
their keys are proven to be.
_Avoid_: the collection, the container, bucket (the candidate index's cell)

**Ownership**:
What the analysis proved about who can reach a region: `Owned`, `ThreadConfined`, `Escaped`, `Shared`
or `Unknown`, each with its evidence chain.
_Avoid_: scope, lifetime (a DI lifetime is evidence for ownership, never the same thing)

### Conflicts

**Access**:
One read, write, read-modify-write, atomic or compound operation, or **Unknown effect**, on a
resource, carried with the execution instance it runs in, the protection held, and the guard it runs
under.
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

**Atomic operation**:
One read, write or read-modify-write the platform performs indivisibly on a single location:
`Interlocked`, `Volatile` and a `volatile` field. It is a property of the operation, never a
protection, and a read and a write that are each atomic do not make the pair between them atomic.
_Avoid_: lock-free, thread-safe, synchronized

**Protection result**:
What the analysis proved about a pair's common protection: `sufficient`, `partial`,
`different-identity`, `incompatible-mode` or `unprotected`. Only `sufficient` removes the pair; the
other four describe a finding.
_Avoid_: protection level, protection status, safe/unsafe

**Guard**:
The path condition an access runs under: the branch, type test, comparison or switch case that must
hold for control to reach it, in its own body or at any call on the path that reached it. A condition
a caller states on a value it passes constrains the parameter that value was passed as. Two accesses
whose guards cannot both hold never meet.
_Avoid_: protection, lock, condition (bare)

**Confidence label**:
The `High`, `Medium` or `Low` band a finding's confidence score falls in; the report's three
finding sections are confidence labels, and a group's label decides whether its **Narrative** is
mandatory.
_Avoid_: severity (the impact of a finding, assessed separately and never deciding either of those), level, priority

### What the analysis could not settle

**Known call**:
A call into a method whose body the run does not have and whose effects a built-in semantics
describes for that exact method and a supported version of its assembly; its effects are the ones
described, and it is never a **Semantic gap**. The same method in an unsupported version is opaque.
_Avoid_: library call, recognized call, BCL call (a method is known by its description, not by who ships it)

**Deep read**:
What a **Known call** does to an argument it consumes whole, as a serializer or a formatter does: a
read of every field of every region reachable from that argument, up to the depth the summaries are
bounded by, and a wildcard read beyond it. It reads private fields as well, because it stands for
whatever the argument's getters could read without running them.
_Avoid_: serialization read, full read, recursive read

**Opaque call**:
A call into a method whose body the run does not have and whose effects no built-in semantics
describes; its effects are unknown, which is not the same as none.
_Avoid_: external call, library call (a library method the built-in semantics describe is not opaque), unknown call

**Unknown effect**:
What an unresolved **Opaque call** may do to what its receiver and arguments reach: read and write
every field, collection structure and cell reachable from them, up to the depth the summaries are
bounded by and a wildcard beyond it, as one access kind that conflicts like a write. Through a
receiver it reaches only the state of the type that declares the member, so a library member called
on a source object — a base constructor, an inherited `HttpContext` — sees none of the fields the
source declares; and it reaches a library object only through what the heap knows the object holds,
never the library object's own state. On a thread-safe collection's structure it is as atomic as
the collection's own members. On a region that belongs to one execution — `Owned` or `ThreadConfined`
— it only reads: the call neither changes such an object nor keeps it. A `readonly` field of an
object it only reads too, since nothing outside the object's construction can assign one but
reflection, whose calls get the whole effect there as well; what the field points to gets the whole
effect.
_Avoid_: havoc, unknown write, opaque write

**Semantic gap**:
A call the deterministic analysis could not reduce and that touches a mutable non-owned region, a
delegate, or feeds a shared region; a reflection, `dynamic`, unresolved-dispatch or unknown-library
call that touches none of those is not a gap. A virtual, interface or delegate call with no receiver
object is unresolved dispatch, and the same conditions decide whether it is a gap. Only shared
regions count — `Escaped`, `Shared` or `Unknown` — so an object of one execution handed to the call
makes no gap; an array created at the call to carry its arguments — a `params` array, or an array
creation in the argument's place — is judged by its elements.
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
and the coverage section, and it never decides a terminal status. Roots count first, shared regions
break a tie, and call sites break the next; there is no combined score.
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
- A **Semantic gap** makes a **Finding** uncertain only when it decides one of the finding's checks:
  one of its accesses is the gap's unknown effect, or its resource, its ordering or its protection
  came through the gap's call. A gap that merely lies on a call path before an access leaves the
  finding as it is. An **Occurrence** an unresolved gap decides loses score on the check the gap
  decides and is never above Medium; a finding still takes its best occurrence, so one occurrence no
  gap decides can keep it High.
- A delegate handed to an **Opaque call** runs in an **Unknown execution**; nothing orders it with
  the execution that made the call. What it captures of that execution's own objects it touches as
  that execution does, so they stay confined; on a shared object it overlaps everything.
- An **Opaque call** changes no object's **Ownership**: an object of one execution it receives is
  only read, and a shared object it reaches gets the whole **Unknown effect**. A **Construction**
  that hands a shared object it produces to one has published it.
- Two **Accesses** on one **Resource** in two **Execution instances** that may overlap form a **Candidate**; a **Candidate** becomes at most one **Finding**.
- Two **Accesses** are compared only inside one **Bucket** or across the wildcard and open-region joins the bucket's definition names; an access in no bucket is compared with nothing.
- A **Finding** has one or more **Occurrences**; a new **Execution root** reaching its access sites adds an occurrence and changes neither the finding nor its **Fingerprint**.
- A file **Suppression** names a **Fingerprint** and nothing else.
- An **Execution root** starts one or more **Execution instances**; a **Spawn site** starts one from inside another.
- Two **Accesses** form a **Candidate** only when no **Happens-before** orders them; happens-before removes pairs and never adds one. Startup precedes every instance of every root. See `docs/adr/0008`.
- A **Fire-and-forget** spawn and an `async void` call have no **Join**.
- An iterator's body runs in the **Execution instance** that enumerates it, at the enumeration, never at the call that creates it; an iterator that reaches a consumer the analysis cannot follow is enumerated by an **Unknown execution** as well. See `docs/adr/0011`.
- A **Construction** belongs to the **Execution instance** that triggers it; its accesses to the object it produces form no **Candidate** unless it publishes the object.
- An **Opaque call** becomes a **Semantic gap** only under the gap's conditions; every other opaque call is counted in **Coverage**.
- A **Known call** into a member of an immutable framework type touches nothing only when every argument it receives is immutable too; an argument that is not gets that argument's own effect, such as a **Deep read**.
- A **Known call** never runs the user's code: a library member that takes a delegate it may invoke — a LINQ operator with a lambda, a retry policy, a mediator — is not known until the sub-phase that models the invocation, and a lambda that becomes an expression tree is data, not code.
- A write or read through a reference — a ref-returning indexer, a ref local or return, an `out` or `ref` argument — is an **Access** to the location the reference names; one whose location the analysis cannot trace is counted in **Coverage** and never dropped.
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
- An **Opaque call** whose **Semantic gap** stays unresolved could be read as touching nothing or as
  a write. Resolved in the phase-5 interview: it may read and may write whatever its receiver and
  arguments reach, so on either side of a pair it conflicts like a write, and it never proves safety.
- Whether an entity an EF Core query returns belongs to the `DbContext` that produced it. Resolved in
  the phase-5 interview: only when entity tracking is proven for that query, and tracking that is
  not proven does not prove the entity isolated either. Until phase 5e models tracking, EF Core is
  opaque persistence and never yields a database verdict.
- A library object's own state — a `DbContext`'s change tracker, an `HttpClient`'s default headers —
  could be read as a **Resource** a **Known call** reads or writes. Resolved in the phase-5a
  interview: it is not a resource. A **Known call** is described only by what it does to its
  arguments, so a member that changes that state is not known and stays an **Opaque call**, while
  one that only reads it is known and touches nothing; the `DbContext` and `DbSet` members are the
  exception the SPEC fixes as opaque persistence, which is why a `DbContext` shared between
  executions is not seen until phase 5e.
- An object handed over through a `Channel<T>` could be linked from the writer to the reader.
  Resolved on 2026-09-23 for the first version, and restated in phase 5b: the object the reader gets
  back is not linked to the written one, and when the written object is shared the write is a
  **Semantic gap**, so the producer–consumer pair shows as uncertainty, never as safety. Linking the
  two waits for the second-wave `Channel` semantics (PRD 8).
- An object of one execution handed to an **Opaque call** could be read as escaping, since unknown
  code might keep it. Resolved in phase 5b by measurement: that reading made every per-request
  object passed to a view, a mapper or a LINQ operator pair with itself across requests — 913 new
  findings on eShop against 2. The call only reads such an object and leaves its **Ownership** as it
  was; what unknown code does with it is a question for an **Inferred fact**.
