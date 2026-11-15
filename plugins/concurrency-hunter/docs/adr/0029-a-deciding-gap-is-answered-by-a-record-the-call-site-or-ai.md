# A deciding gap is answered by a curated record, then by the library's body at its call site, then by AI, and the run no longer generates

This decision amends [ADR 0012](0012-library-semantics-are-data-the-analysis-derives-from-decompiled-code.md) where the
run builds a generated layer, [ADR 0024](0024-a-library-model-holds-for-every-receiver-of-its-member.md), which makes a
library model hold for every receiver of its member, and
[ADR 0027](0027-the-run-generates-in-rounds-and-a-generation-takes-its-dependencies-models.md), which has the run generate
in rounds, and the rule that a model proves no order (PRD FR-21, SPEC TD-034a,
[ADR 0013](0013-a-driver-seeds-what-a-program-could-have-put-there.md)): a built-in or project record's `sequential-awaited`
relation proves that waiting for the call's task waits for the task of every run, and that those tasks never overlap one
another; a model still proves no protection, and a generated model no order. The run stops generating models. What it cannot settle, it resolves only for the gaps that decide a finding:
through a curated record, an analysis of the library's body at the call site, or an AI answer, in that order. The model
generator stays, out of the run.

## Context

Most gaps decide nothing. eShop has 97 semantic gaps in 39 library members. Of its 36 findings, 34 carry a gap that decides
one of their checks, and all 34 name one member, `Polly.AsyncPolicy.ExecuteAsync(Func<Task>)`; once it is answered,
`Policy.Execute(Action)` decides the same findings. The gaps of the other 37 members — protobuf parser factories, startup and
options callbacks, health checks, CORS predicates — decide no finding: their delegates touch nothing shared.

Generating in the run answers the members a run reaches, not the ones that decide. On eShop, 900 s of generation gave 414
answers, 5 of them models, and removed no pair; 557 generated CoreLib models moved no finding. With the compile rules of
[ADR 0028](0028-a-library-compiles-with-its-own-identity-references-and-sources.md), the 158 members that refused to compile
reach the analysis and 11 of them become models. For both Polly members the generator's answer is `unknown-execution`,
because a model holds for every receiver: a retry policy runs the delegate in the caller, one attempt at a time, while a
pessimistic timeout runs it on another task and can return while it still runs.

Two Polly records written by hand took eShop from 36 findings to 2, both real, once a spawn site counted the passes of its
parent's run rather than the call paths that lead to it. Since a known call's delegate runs 0..n times and those runs are
unordered, which finds real misses such as `list.ForEach(x => Task.Run(() => Value = x))`, the same records bring the 30
seeding findings back. The vocabulary cannot say that the runs go one at a time and that each is awaited before the next and
before the call returns.

The engine already analyses library code: the generator analyses each member that way. One Polly call analysed as source
reached about 150 bodies against 7 as metadata, in 1.4 to 3.6 s. A library that keeps a delegate for a framework to invoke
later, such as a protobuf parser factory, needs the open-world rule the generator applies
([ADR 0013](0013-a-driver-seeds-what-a-program-could-have-put-there.md),
[ADR 0015](0015-a-delegate-that-does-not-run-is-not-run-and-a-holder-runs-its-own.md)). The driver analysis of Kestrel's
`Listen` took 98 s, so a large framework member needs a bound. The engine binds a join to its spawn site, not to one pass of
it, so a loop that awaits each run it starts, which is what a retry engine is, reads as repeated and overlapping.

An AI model given the decompiled member and its direct callees labelled 51 of 52 delegate parameters exactly, with no unsafe
narrowing. With ADR 0028 it reads the member's original text.

## Decision

- **Deciding gaps.** For each scope the run lists the gaps that decide a check of a finding: the callee, the findings, and
  the call sites. Only those gaps get the resolution below. The other gaps stay counted in coverage.
- **The ladder.** At each call site of a deciding gap, the first rung that answers settles it:
  1. **A record.** A project record wins over a built-in one. A record may name the receiver type it holds for; it then
     applies only where the static type of the receiver expression at the call site is that type or derives from it. The
     model for a call is chosen before the heap is solved, and a library factory such as Polly's `WaitAndRetryAsync` makes
     no object of a known type there, so the static type is the one the call site always has.
  2. **The library's body at the call site.** The callee's body, compiled as ADR 0028 compiles a library, is analysed in the
     scope with that call's receiver and arguments. What the library keeps follows the open-world rule. The bodies and the
     time spent on one call site are bounded; past the bound, or when the analysis leaves the call unknown, the next rung
     answers.
  3. **An AI answer.** The question names the check the gap decides. The input is the member's original text (ADR 0028) or
     its decompiled text, with its direct callees. The answer, in the record vocabulary, passes the same validation as an
     inferred fact. It is stored as a project record with provenance `ai` and the lines it rests on, and from then on it
     applies like any record. The team reviews it in the repository; the owner may adopt it as a built-in record.
  4. Otherwise the gap stays, and the finding stays capped as now.
- **Vocabulary.** A delegate's fate says how its runs relate: `concurrent` (any number of runs, unordered — what
  `invoke-now` and `iterator` mean today), `sequential` (one run at a time in the caller's execution) and
  `sequential-awaited` (sequential, and the task each run returns completes before the next run and before the call
  returns).
- **The run no longer generates.** It applies the committed generated files as `read` does and generates nothing, and the
  generation budget leaves the run. The generator answers `generate`, runs the model evals, and produces candidate built-in
  records for the owner to review, as [ADR 0022](0022-the-owner-regenerates-the-built-in-corelib-models-from-a-recorded-commit.md)
  has the owner do for CoreLib.
- **Coverage.** Coverage names each deciding gap with the findings it decides and the rung that answered it, and counts the
  pairs each rung removed. A record applies before the analysis, as every library model does, so a call it answers is never
  a gap and no deciding gap names the record; the rung is named for the rungs that answer after the analysis.

Rejected: generating in the run. It spends its budget on members that decide nothing, and a model that must hold for every
receiver cannot settle a member whose behaviour depends on the receiver. Rejected: analysing every opaque call at its site.
One Polly call adds about 150 bodies, and most gaps decide nothing. Rejected: asking AI first. A record or the library's
code answers the common cases without a model in the loop, and the same input gives the same answer. Rejected: one record
for every receiver. Polly's retry and timeout policies share `ExecuteAsync` and differ exactly where a race lies. Rejected:
reading a known call's delegate as running once again. It hides real races, and the vocabulary can say once where once is
true.

## Consequences

- Two prerequisites come first. Records and AI answers need the vocabulary of runs. A `sequential` relation and a loop that
  awaits each run at its call site both need an engine that binds a join to the pass that spawned the work: a delegate a
  retry runs often waits for work it starts itself, as eShop's seeding does with `SeedAsync(...).Wait()`, and a join is
  proved only for work started once. That amends
  [ADR 0008](0008-ordering-is-a-happens-before-graph-trusted-inside-one-instance-tree.md) and is decided with the engine
  change that builds it. Without it a `sequential` relation orders nothing that `concurrent` leaves unordered.
- On eShop, a `sequential` record keyed by `RetryPolicy` and a `sequential-awaited` one keyed by `AsyncRetryPolicy`, read
  with a join bound to its pass, are expected to leave the 2 real findings out of 36; `Execute(Action)` runs a delegate that
  returns no task, so there is nothing for it to await. The 2 of 36 was measured with records that read the delegate as
  running once. Read as `concurrent`, the same records turn the 30 seeding findings High: the gap that held them at Medium is
  answered, and the seeding's waits inside each run still prove no join.
- A run loses the 900 s generation budget. Analysis at the call site and AI spend only on deciding gaps.
- What the call-site analysis reuses from the generator: the library compilation (ADR 0028), the open-world rule and seeds,
  and the engine's handling of library code. Driver synthesis, probes and fate classification stay with the offline
  generator.
- PRD FR-21, SPEC TD-034a, TD-034b, TD-034c and TD-035 and CONTEXT change when this decision is built. Committed
  generated files stay readable.
- The model evals extend to records keyed by receiver type, to the relation of runs, and to the answers of call-site
  analysis.

## Amendment, 2026-11-11: the calls that made the receiver and the arguments get their bodies too

A deciding gap's call often runs on an object a library factory made. In the demo's `polly-retry-attempts`, `_retry` is
`Policy.Handle<InvalidOperationException>().RetryAsync(2)`, and `AsyncPolicy.ExecuteAsync(Func<Task>)` reaches the retry
loop only through the virtual `ImplementationAsync` that `AsyncRetryPolicy` overrides. With the factory left an opaque
call, the heap holds its result as a value with no object behind it: the body of `ExecuteAsync` alone dispatches
`ImplementationAsync` on no receiver, which is unresolved dispatch, and the delegate goes to an unknown execution again.
The gap stays.

So the library's body at the call site is the body of the deciding call together with the bodies of the library calls
whose results reach that call's receiver and arguments — for Polly, `Handle<T>()`, the retry factory and the
constructors they run — and the receiver is the object the library made, with the fields it set. The library, compiled
as [ADR 0028](0028-a-library-compiles-with-its-own-identity-references-and-sources.md) compiles it, joins the scope as a
source of bodies only: the scope's own code is compiled as before, and a call it makes to a member of the library still
names that member by its assembly and documentation id, which is the identity of the member's body in the library's
compilation. Library bodies enter the analysis only through those calls and the calls inside the bodies they reach; the
rest stay outside the set, as a body outside a generation's reachable set does
([ADR 0019](0019-corelib-generation-reaches-only-the-types-it-constructs.md)), and every other call into the library in
the scope keeps its record, its generated model or its opacity.

At the call site the program is the one the scope analyses, so nothing stands in for it: no seed
([ADR 0013](0013-a-driver-seeds-what-a-program-could-have-put-there.md)) is put into the library's fields or statics. The
library's type initializers are inert, as in the generation of a library other than CoreLib: what they store is in the
heap, and they make no access, gap or finding. What the library keeps follows the open-world rule as the engine applies
it to any code: what the admitted bodies hand to a call without a body or to unresolved dispatch, or store where such
code reaches, is reached by an unknown execution.

Each of those calls in the scope's code is a place of its own. A library body reached from one carries that call's place
into the calls it makes, so the objects library bodies create are apart per call in the scope's code, however much
library code two calls share: on Polly, every caller's `RetryAsync(2)` reaches the same `new AsyncRetryPolicy` in the
overload it calls, and every `ExecuteAsync(Func<Task>)` the same `new Context()`, so two independent workers would share
one policy and one context, and each worker's delegate would run under the other's root. The library's static storage
stays one, as it is in the process. Rejected: a deeper calling context for every body, the program's own included, and
a static method taking its caller's context everywhere. Either changes how the program's code is analysed for a question
only library bodies at a call site raise.

Rejected: the deciding call's body alone. On Polly it answers nothing. Rejected: a body for every call of the scope into
the library. It analyses calls that decide nothing, which this decision rejects, and a call with a body sets aside that
library's records and generated models. Rejected: compiling the scope's code against the library's compilation, as a
driver is. Every rule that takes a member with source for the program's own — whether a call is a library call, which
records and generated models apply, roots and registrations, automatic properties — would then take the library for the
program, and each would have to learn otherwise to leave the other calls into the library as they were.

Whether the engine carries a factory-made receiver through the library's wrappers, virtual dispatch and delegate fields,
and orders the retry attempts by its own pass rules rather than by a record's admission, is measured on Polly before the
bound on bodies and time per call site is set.

## Amendment, 2026-11-15: packages only, a bound in bodies alone, and rounds

Three things the decision left open are settled before the call-site analysis becomes what a run does.

**Only a package's members are analysed at the call site.** A deciding gap on a member of a shared framework
(`Microsoft.NETCore.App`, `Microsoft.AspNetCore.App`) stays a gap, and coverage says why. The frameworks already have an
owner: their records and the census the owner regenerates
([ADR 0022](0022-the-owner-regenerates-the-built-in-corelib-models-from-a-recorded-commit.md)), as the committed generated
files of a repository hold package members only. In the demo, 11 of the 12 members that are deciding gaps are framework
members — `Console.WriteLine(object)`, `FieldInfo.SetValue`, `CancellationToken.Register(Action)` — and analysing them
would compile `System.Private.CoreLib` and several more assemblies on every run, a cost the bound below does not limit,
since it counts the bodies an analysis reaches and not the compilation before it. Rejected: every library, as the
decision's Context implied for Kestrel's `Listen`. Whether a framework member is worth its compilation is measured
before it is admitted.

**The bound counts bodies and nothing else.** The library bodies one call site reaches are bounded by their number. The
time a call site takes is measured and reported, and cuts nothing: a run is the product, and the same program must give
the same findings on every machine, whereas a call site near a limit of seconds would lose its answer on a loaded one —
what the member limit of a generation showed before it counted CPU time
([ADR 0033](0033-the-recording-generates-four-members-at-once-and-the-member-limit-counts-the-members-cpu-time.md)), and
CPU time cannot be attributed to one call site whose bodies are solved in one heap with the rest of the scope. The run's
own deadline stays the guard against an analysis that does not end. Rejected: bodies and wall-clock time, as first
written; bodies and CPU time.

**The scope is analysed in rounds.** A gap decides a check only for the unknown execution its own delegate starts, so a
library call whose delegate makes another library call is not a deciding gap until the inner one is answered. eShop's
seeding shows it: `retry.Execute(() => InvokeSeeder(...))` runs a seeder that awaits `policy.ExecuteAsync(...)`; the
first analysis names only `AsyncPolicy.ExecuteAsync(Func<Task>)`, and once that call has its body the same findings are
decided by `Policy.Execute(Action)`. So after each analysis the deciding gaps on package members whose call sites are
not yet admitted are admitted too and the scope is analysed again, until an analysis names no new one or three rounds
of admission have run — four analyses of a scope at most. The admitted call sites only grow, so the rounds end and their
result does not depend on order. A gap still deciding after the last round stays a gap. A scope with no deciding gap on
a package member is analysed once, as before. Rejected: one round, which leaves eShop's 34 seeding findings behind the
outer call; admitting in the first round every library call whose delegate encloses the access, a relation the analysis
does not have and one that admits calls that decide nothing.
