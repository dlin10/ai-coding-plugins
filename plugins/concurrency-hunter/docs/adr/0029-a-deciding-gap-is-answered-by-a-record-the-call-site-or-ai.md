# A deciding gap is answered by a curated record, then by the library's body at its call site, then by AI, and the run no longer generates

This decision amends [ADR 0012](0012-library-semantics-are-data-the-analysis-derives-from-decompiled-code.md) where the
run builds a generated layer, [ADR 0024](0024-a-library-model-holds-for-every-receiver-of-its-member.md), which makes a
library model hold for every receiver of its member, and
[ADR 0027](0027-the-run-generates-in-rounds-and-a-generation-takes-its-dependencies-models.md), which has the run generate
in rounds. The run stops generating models. What it cannot settle, it resolves only for the gaps that decide a finding:
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
     applies only where the receiver's type is that type or derives from it.
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
  pairs each rung removed.

Rejected: generating in the run. It spends its budget on members that decide nothing, and a model that must hold for every
receiver cannot settle a member whose behaviour depends on the receiver. Rejected: analysing every opaque call at its site.
One Polly call adds about 150 bodies, and most gaps decide nothing. Rejected: asking AI first. A record or the library's
code answers the common cases without a model in the loop, and the same input gives the same answer. Rejected: one record
for every receiver. Polly's retry and timeout policies share `ExecuteAsync` and differ exactly where a race lies. Rejected:
reading a known call's delegate as running once again. It hides real races, and the vocabulary can say once where once is
true.

## Consequences

- Two prerequisites come first. Records and AI answers need the vocabulary of runs. Analysing a loop that awaits each run at
  its call site needs an engine that binds a join to the pass that spawned the work; that amends
  [ADR 0008](0008-ordering-is-a-happens-before-graph-trusted-inside-one-instance-tree.md) and is decided with the run that
  builds it.
- On eShop, two `sequential-awaited` records keyed by `RetryPolicy` and `AsyncRetryPolicy` are expected to leave the 2 real
  findings out of 36. The 2 of 36 was measured with records that read the delegate as running once.
- A run loses the 900 s generation budget. Analysis at the call site and AI spend only on deciding gaps.
- What the call-site analysis reuses from the generator: the library compilation (ADR 0028), the open-world rule and seeds,
  and the engine's handling of library code. Driver synthesis, probes and fate classification stay with the offline
  generator.
- PRD FR-21, SPEC TD-034a, TD-034b, TD-034c and TD-035 and CONTEXT change when this decision is built. Committed
  generated files stay readable.
- The model evals extend to records keyed by receiver type, to the relation of runs, and to the answers of call-site
  analysis.
