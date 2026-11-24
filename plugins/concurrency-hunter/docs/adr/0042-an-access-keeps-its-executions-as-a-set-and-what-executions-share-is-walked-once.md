# An access keeps its executions as a set, and what executions share is walked once

Status: Accepted (#479). Option C of [ADR 0041](0041-a-visit-emits-an-access-only-under-guards-the-execution-has-not-emitted-it-under.md),
in the form below, built as the census (#480), C2 (#481) and C1a (#482); C1b is decided by an ADR of its own on the
census's numbers.

This decision changes what an execution of an access is ([ADR 0038](0038-an-access-is-recorded-once-with-the-executions-that-run-it-so.md))
and how often access collection does what several executions have in common
([ADR 0040](0040-access-collection-walks-executions-on-several-threads-and-folds-them-in-their-order.md), ADR 0041). It
carries to collected accesses the rule [ADR 0017](0017-executions-share-one-walk-graph.md) made for the execution model's
accesses and [ADR 0039](0039-a-construction-entry-is-made-once-with-the-executions-of-its-region.md) for construction
entries: a thing that many executions have is kept once, with the set of those executions. A finding, its pair of sites,
its fingerprint, the gaps and the coverage stay what [ADR 0007](0007-a-finding-is-a-pair-of-access-sites.md), SPEC TD-076
and ADR 0038 make them. The heap stays frozen ([ADR 0016](0016-the-heap-is-frozen-after-the-solve.md)) and a path still
carries no construction ([ADR 0018](0018-access-paths-carry-no-construction.md)).

## Context

Two runs of run 5g E-scale (`docs/runs/5g-E-scale.md`) collected far enough to show the shape of the work. Each figure
below is read off the `execution:` checkpoint of every walked execution, or off a dump.

| | `OrchardCore.Application.Pages`, all of it | `Nop.Web`, executions 1–615 of 17,442 |
|---|---:|---:|
| executions walked | 14,554 | 613 |
| access copies / emitted / replaced later | 118.9 M / 190.0 M / 71.2 M | 224.2 M / 366.7 M / 142.5 M |
| folded accesses | 4,443,972 | 26.1 M at execution 604 (dump) |
| copies per folded access | 26.75 | 8.3 |
| visits / states, each execution's distinct states summed | 71.3 M / 1.96 M, 36.4 a state | 892 M / 154 K, 5,804 a state |
| entries of a heavy execution (more than 100 K copies), median | 30 | 55,226 |
| executions per root (the root's execution and its tails after `await`): median / p99 / max | 1 / 318 / 946 | 4 / 4 / 7, mean 2.7 |
| distinct sets of eight counters among the walked executions | 701 for 14,554 | 218 for 613 |
| walk time / copies of the first execution of each such set | 13.4 % / 11.2 % | 98.5 % / 97.6 % |
| emission's share of walk time | 90.3 % | 88.9 % |
| walk time of a heavy execution, mean / median | 8.7 s / 8.0 s | 80.9 s / 76.9 s |

On `Nop.Web` the times leave out the 15 executions whose walk spans a hold of the run for a dump.

The two scopes repeat for different reasons.

- **OrchardCore repeats whole executions.** A child execution is one per parent (ADR 0017), and the tail of a shared async
  body runs as an execution of its own under every parent. The tail of `ShellContext.ReleaseFromDependencyAsync` is 153
  executions with the same counters: 30 entries, 150,607 visits, 1,252 states and 193,215 copies each, 29.6 M copies in
  all. Among the 13,576 executions with accesses, 110 sets of counters that recur cover 12,998.
- **`Nop.Web` repeats what different executions share.** Its heavy executions are one per root, no two alike. 209 of
  the 217 have 27,797–78,334 entries, the constructions of every region they may build (ADR 0039); the other 8 have
  29–46 entries and still made 597–631 thousand copies each, so the constructions are not all of what they share: the
  unknown effects of their own bodies reach the same shared heap. Between the dumps at 45 GiB and 80 GiB the scope gained
  177.5 M copies and 18.97 M folded accesses over 159 heavy executions, about 119 thousand new accesses each (10.7 %);
  before the first dump it was 137 thousand each. So nine copies in ten repeat what another root's execution made, and
  the tenth is new, at a rate that falls slowly.

What the copies cost on `Nop.Web`, in the dump at 80 GiB private (86.2 GB; execution 600, 66.5 GB live):

| What | GB |
|---|---:|
| the record per copy: `AccessExecution[]` 7.41, `List<AccessExecution>` 0.84, the fold's dictionary 1.15 | 9.40 |
| `Access`, 43.1 M objects: 26.1 M folded and the copies on their way to the fold | 10.01 |
| the walks of the collectors waiting for the fold (#476) | about 23 |
| strings, mostly resource identities, most of which #474 removes | 10.65 |

The fold is one thread at 100 % and the workers wait for it half the time (#477). At 38.9 thousand copies a second the
scope would take 45.6 hours. The `gaps:` checkpoint bounds the copies from below before collection starts:
`executionAccessExecutions=1790335547`; the projection from the share of heavy executions so far is 6.38 thousand
million. Before collection starts, private is already 26.45 GB at `heap-end` and 23.78 GB at `gaps` on the VM's server
GC (#478). OrchardCore's collection peaks at 39.92 GB private, and ends at 38.44 GB private over 15.02 GB live.

The execution model already keeps its accesses in the form this decision proposes. `Nop.Web`'s 5,346,534 `CollectedAccess`
carry 1,790,335,547 pairs of access and execution in 9,205 distinct `ExecutionSet`s, 3.5 MB of bits in the dump.

A probe on the demo and on eShop (one `metrics` run each, the probe's output compared with a run without it: same counts
and coverage) gives what those small corpora can say. They repeat little, 1.05 and 1.03 copies per folded access, so they
show what an access depends on, not how often.

| | demo | eShop |
|---|---:|---:|
| copies / folded accesses | 6,316 / 5,995 | 4,552 / 4,434 |
| distinct execution sets of the folded accesses | 1,670 | 168 |
| rows (instance, site, resource, guards) with copies in several executions | 234 | 57 |
| of them, differing between executions in held protection, construction-locality, freshness, operation or read sources | 0 | 0 |
| a path root of an access reached by several call paths | 12 | 53 |
| a path root and call path of an access with several code flows | 0 | 0 |
| occurrences / distinct pairs of roots among them | 301 / 297 | 2 / 2 |
| copies on regions of a root's invocation scope (`invocation:root:` in the identity) | 30 | 812 (17.8 %) |
| copies on thread-confined regions | 1,149 | 3,783 (83.1 %) |

The new accesses of eShop are new instances (764), new regions (287), new sites and resources (2,808), the same site,
resource and operation under other guards (539), and 36 that differ in a list the probe did not compare. A region of a
root's invocation scope is new for every root by construction: a scoped service is one object per invocation of a root,
and so is every region named after one.

What the collector reads of its execution is short. The walk reads its entries, in order, and for a construction edge
whether the construction runs in the execution (`InstanceExecutions`). The hosts read its kind, subject and work sites.
The emission reads whether an object is under construction there (`Construction`), and for the variant its root, the call
path prefix of a spawned execution (`CallPathPrefixes`), its spawn sites and whether it is inert. Everything else is the
program, the frozen heap and the scope's caches. ADR 0041 names what stands in the way of collecting an access once:

- **the lock fixpoint** over the execution's own edges: an entry holds no lock, every other instance the meet of what its
  callers in this execution hold;
- **the reads feeding a write**, a may-analysis over the instances and edges the execution visited;
- **the order of the entries**: `EmittedKey` names the body, so of two instances of one body on one resource the first
  the execution meets names the access, and a body's call path is its first visit's.

## Decision

Option C, built in this order: a census first (#480), then the representation (C2, #481), then executions collected once
per class (C1a, #482). The shared walk below the execution (C1b) is decided by an ADR of its own, on the census's
numbers. C1a after C2 leaves OrchardCore's time to the third step and takes its memory and the fold's thread first.

### The representation (C2)

- **An access** is the key of ADR 0038, unchanged, with its executions as a set of execution numbers. Sets are kept once
  for each content, as `ExecutionSet` is in the execution model. The fold adds executions in collection order, so a set is
  built as a path of a shared tree of execution numbers: adding an execution to an access moves it to the child of its set
  for that number, and accesses that the same executions reach share every step. There is no record per copy.
- **A variant** is what an execution gives the access besides itself (ADR 0038). It splits in two.
  - The execution's part, one per execution: its root, the call path prefix of a spawned execution, the code flow's first
    step and the spawn sites of the prefix.
  - The walk's part: the call path below the entry, the code flow's steps along the path, the acquired locks and the
    access, and the read sources. It is kept once for each content, with the subset of the access's executions it is for.
  The variant of an access in one execution is the two parts together, the fields `AccessExecution.Variant` holds today.
- **The position** of an execution of an access is the execution's number, then the access's place among the scope's
  folded accesses in the order the fold first met them. Collection order between two executions is execution order, as
  it is today. Within one execution the order of first meeting is one another execution can decide, so pairing reads
  only a position's execution number and, of one execution, orders two accesses by their content and the copies of one
  access by their places in its list. The content order (`PairContent`) compares, of two accesses, the scope, assembly,
  region with its context, path and member of the resource, which a finding's group reads (the resources of a bucket
  have one identity but may be named by different paths or holders, as one list two fields hold is), then the site,
  instance, operation and every other field the fold tells accesses apart by, each record they hold field by field, so
  that two accesses compare equal exactly where the fold keeps them as one; of two variants of one access, their roots'
  ids, call path, spawn sites, code flow, read sources and the rest of their roots, item by item. That one order of
  copies, the execution's number and then content, is the order pairing puts one of two copies first by, before any
  witness is chosen; the candidate index says where it applies and names the reported resource:
  - The search for a pair of variants' witness takes, of two next executions, the earlier, and of two of one execution
    the one whose content comes first, so the site each execution of a witness has. This holds for every pair.
  - Where the index orients a pair by its copies (`ByPosition`: within one bucket, and a whole struct against a field
    below it), the copy the order puts first is the pair's first side: the earlier execution's, or of a pair of one
    execution, which has no earlier side, the one its content puts first. That side is what a witness is filtered and
    keyed by in orientation. Other pairs keep the side the index puts first: an open region's before a closed one's, the
    first bucket's of two that meet across buckets.
  - The found pairs are ordered by their earlier copy, then their later one (`WitnessOrder`). A found pair holds the
    numbers of its two kept views while pairing runs, and once it is done each view gets its place in that order,
    ranking once the accesses of the sides that keep a view, so the found pairs are ordered by one number each, as
    before.
  - Which resource a pair is reported on is the index's to say: where it reports a pair on its first side
    (`ReportsFirst`, within one bucket), the oriented first side's; otherwise the resource the meeting names, as a whole
    struct and its field are reported on the field whichever runs first.

  Where every order a reader keeps ties, the shown witness of an
  occurrence, the best scored witness and the solver's budget fall to that order, and the findings of one group whose
  sides start alike stand in the order their sides end, then their operations, then their identities. The
  hosted-transient rule skips a pair of one execution only where both sides are on a hosted transient. An execution
  that makes one access from several path roots, as a construction several roots trigger does, or from one path root by
  several variants, has those copies at one position, in the ordinal order of the roots' ids and then of what their
  variants hold: the order it made them in is not kept, and the set and its parts give the roots and the variants
  without it. Both numbers of a position are places in lists the fold holds, so every copy has one. What the order of
  first meeting still decides within one execution: the order of the public list of executions, and the order in which
  pairing meets pairs of accesses and numbers candidates. No finding, group, pair counter or coverage the probes compare
  reads that order; the timing's assumptions are gathered across a proof's questions in the order they are asked
  (#490), and are measured, not shown, not to read it. Two places of the candidate index read
  the fold's order as they did before step C2, and step C2 leaves them (#494): a collection several holder regions
  share is registered under the region the fold met first, and a pair across two buckets of overlapping cells puts first
  the side of the bucket the index filled first and asks no content (amended 2026-11-23 and 2026-11-24 in step C2).
- **One occurrence per pair of roots.** CONTEXT **Occurrence** and SPEC TD-076 allow at most one occurrence per pair of
  roots, with "the call path by which the analysis first reached each site from its root". `ConflictFindings.Fold` groups
  by the pair of roots and the pair of call paths, so it makes more where one root reaches a site by two paths: 4 of the
  demo's 301 occurrences, in 3 pairs of roots. The code follows SPEC and keeps every witness, as the owner decided on
  2026-11-23. Each pair of the two accesses' variants still gives its witness, the first pair of its executions in
  collection order that passes, and the timing is still asked of the pairs of executions themselves. The witnesses of
  one pair of path roots and one orientation are one candidate: a pair reported on its earlier execution's resource,
  where the two accesses hold different resource objects, gives one for each side that runs first. What a gap decides
  is marked on every witness, so the library calls a gap admits are the ones each witness asks for; the solver asks a
  candidate once, since its query is made of the two accesses' conditions and terms, and its answer is every
  witness's. A finding folds its candidates into one occurrence per pair of roots, which shows the least protected
  witness, then the one with the smallest instances, then the best scored, then the first in collection order; where a
  pair of roots reaches the sites by several pairs of accesses, other instances or other held protection, that is the
  pair it shows.
- **A finding reads every witness.** Its score is the highest of its witnesses' scores, its label follows from that
  score, and its uncertainty and gap callees are the union over all its witnesses, as the owner decided on 2026-11-23;
  the line that the ordering of lifecycle methods of one hosted service is not analysed is named once where any witness
  is such a pair. The witness the finding shows gives `accesses[]`, the protection, the path feasibility, the scenario
  and the six base evidence items; the `SPn` evidence items collect the distinct spawn and timer sites of the listed
  occurrences. So a finding's confidence may come from another witness than the one it shows; SPEC section 8.1 says
  which field reads which.
  Before this, and before this decision's first step, a finding read one witness per pair of roots and pair of call
  paths, the least protected: where one root reached a site by one path both under a lock and outside it, the pair
  under the lock and what a gap decided for it were dropped, so the finding scored lower than with two paths, and a gap
  check that only the dropped pair carried left the finding and the deciding gaps of coverage although the admission
  of library bodies had seen it. That behaviour is corrected on purpose. Which witness an occurrence shows does not
  change.
- **The collector folds the key, not the copy.** Every part of the key is a number or a pooled object: the resource key
  (#474), the instance and body, the operation, the held protection's key, the guards as one pooled list, locality,
  freshness, the term and the flags. Uncertainties, gap checks and deciding calls, binding and ownership evidence, the
  symbol and the source follow from those. The collector builds an `Access` only for a key the scope has not folded yet;
  for every other key it hands the fold the key and the walk's part, and builds nothing else. That is what ADR 0038 left
  for later.
- **The fold** stays on the coordinator, in collection order (ADR 0040), and costs one lookup of a key of numbers and one
  step of a set per copy. A worker reads the scope's folded keys from a concurrent map without waiting for the fold and
  builds the access of a key it does not find; the fold drops the one a race built twice.
- **Why the race decides nothing.** The map of folded keys is not the cache of a pure function: what a worker finds in it
  depends on how far the fold has got. It is admitted on these grounds, an amendment to ADR 0040, which lets collectors
  share only caches of pure functions and what is read-only:
  - Only the fold writes the map, and it writes a key when it folds the first execution, in collection order, that
    makes the key. A worker walks an execution the fold has not reached, so every key it finds was made by an earlier
    execution, and every key it misses is either new with its execution or folded meanwhile by an earlier one.
  - So when the fold takes execution *i*, a key already in the map came from an earlier execution and *i*'s copy of it
    adds *i* to the set; a key not in the map has *i* as its first execution, and *i*'s worker built its `Access`, since
    the key was not in the map when it looked. The `Access` kept for a key is always its first execution's, and one a
    worker built because it looked too early is dropped.
  - What the scope reads of that `Access` is the key and what follows from the key: the site, instance, resource,
    operation, held protections, guards, locality, freshness, term and flags, and the uncertainties, gap checks,
    deciding calls, binding and ownership evidence, symbol and source made from them. None of it is the execution's:
    the variant is in the parts, outside the `Access`. So the `Access` any execution would build for a key is equal to
    the first's field by field, its lists by content, and the lists the scope pools (ADR 0040) are the same objects. Which
    worker built it is not observable.
  - The parts of the variant are the fold's to keep, for each execution it takes, in collection order.
- **Readers.** `InterproceduralPairing` reads the set and the parts: `PairingSide` keeps the side's executions by number,
  and the witness search walks pairs of executions in collection order as it does now. `AccessExecutionList`, which model
  generation and the tests read, makes the view of an access in one execution on demand, in the new collection order.
  `SemanticGaps` and `Coverage` read sets already.

### What stays per execution

C2 leaves each collector walking its own execution, so the three obstacles stay where they are: the collector solves its
own locks and feeding reads and meets its own entries in their order. They become questions only for the steps that
share work between executions.

- **C1a** collects a class of executions once. Executions are one class when they have the same entries in the same
  order, the same kind, subject, work sites and inertness, and the same answers to every membership and construction
  question the walk and the emission ask. The collector of the first execution of a class records those questions and
  their answers. Every other execution of the class answers the same questions, and only if every answer agrees is it
  taken into the class; otherwise it is collected on its own. The collector is a function of the program, the frozen heap
  and those answers, so equal answers give equal accesses, walks' parts and counters: the class's accesses take the
  class's set of executions, and each member keeps its own execution's part of the variant. The lock fixpoint reads only
  the entries and the edges the walk took, the feeding reads only the instances and edges it visited, and the order is the
  entries' order, so all three are the class's.
- **C1b** shares what different executions walk alike. The walk from a construction entry is made once for each class of
  the entry's executions that agree on every construction edge it meets, and its rows (resources, guards, unknown
  effects, deep reads, uncertainties) once with it; an unknown effect's list is made once per instance, call and handed
  storage (ADR 0041 option B1). What each execution still does is cheap and stays its own: it solves its locks and its
  feeding reads over the rows it has, and replays the rows' keys in its own entry order through its own `seen` and the
  fold. So the instance that names an access and a body's call path are still the execution's first, the key still has
  the held protection the execution's fixpoint gives, and the guards are still minimal per execution. C1b is the step
  that turns `Nop.Web`'s 55 thousand entries per execution, and the unknown effects its roots expand over the shared
  heap, into rows made once; how much of its emission that removes is what the census measures.

### What changes in the output

- **The same on the demo and eShop, measured:** every pair of sites and fingerprint the pairing gives, `accesses` and
  `accessExecutions`, the skips, suppressions and assumptions of the filters before the solver, which are counted per
  pair of accesses under its first pair of executions (each side's first execution, the same under either position),
  every check a gap decides for a witness, and every library call a gap admits. Within one execution the order of
  emission gave a pair its first side and a witness's search its next execution, and content gives them now (**The
  position**): they can differ only where the two orders differ for a pair of one execution, or for two copies of one
  execution a witness's search meets next, and that pair reaches what is compared; it can then name another path or
  region of one bucket than the order of emission named, or give each execution of a witness the other site. Two
  things are measured and no more is claimed of them: the assumptions the timing reports are gathered across the
  questions a completion proof asks, so they read the order in which pairing asks its questions, which this changes,
  and they are the same on the demo and eShop; and whether the timing of a pair reads which of its sides comes first is
  asked of every pair of executions of the demo and eShop in both orientations (`PairOrientationTests`), the proof's
  own questions beyond them being #490.
- **Diagnostic lines only:** the witness search may ask the pairs of executions inside one pair of executions in another
  order, so `executionPairs` and the timing line of the `pairing-end` checkpoint may move.
- **Moves, with one occurrence per pair of roots:** the demo's occurrences, 301 to 297; its candidates, 304 to 297, and
  its comparisons with them, since a pair that stands counts one comparison per candidate, while the witnesses stay
  304; and the witness the merged occurrences show, which is chosen as above. The demo's snapshot is re-recorded in
  those counters in step C2, each shift accounted for. eShop does not move: its two occurrences have two pairs of roots.
  TD-076 makes an occurrence a way a finding is reached and not part of it.
- **Moves, with every witness read by its finding:** a finding's score and label, its uncertainty and gap callees, and
  the deciding gaps of coverage, wherever one pair of roots and call paths has witnesses that differ in protection,
  instances or what a gap decides. On the demo and eShop nothing moves: every finding's score, label, uncertainty and gap
  callees and every deciding gap of coverage are what they were, compared field by field, and the demo's expectations
  still match.
- **Moves, with one query per candidate:** the solver's counters (`solver-queries`, `solver-sat`, `solver-unsat`,
  `solver-unknown`, `solver-safeguard-timeout`, `solver-budget-exhausted`) and the `unsatisfiable-query` skips count
  candidates, so they move wherever a candidate has two witnesses with a query, whatever the budget; and the run's
  budget (TD-094) is spent per candidate, so it lasts longer, and a candidate it reaches now that it did not reach before
  is decided instead of left standing. A candidate's one answer is the answer each witness would have had only where the
  solver's answer repeats: a work-limited answer to the same query (ADR 0021). Three things differ:
  - A query the safeguard stops is every witness's `Unknown`, where asking again might have answered; and an `Unsat`
    takes every witness away, where another attempt might have been stopped and kept the finding.
  - Witnesses of one candidate whose sides run in opposite orders, where the candidate does not split by orientation,
    ask mirrored queries, the same up to the names of the two sides. The candidate asks the form of its first witness in
    the order the budget is spent in. A decided answer is the same for both forms, but at the work limit one may be
    decided and the other not. A canonical form would not give back what pairing by variants gave, which asked both
    forms, and would need an order of two accesses that may differ in their guards alone, so the move stays.
  - On a run whose budget runs out, a finding that stood only on a witness that repeated an unsatisfiable query after the
    budget was spent goes away with the first.
  A query with no conditions and no terms is not asked and costs nothing, as before.
- **May move where candidates tie:** within one occurrence, two witnesses that tie on protection, instances and score
  are shown in collection order. Collection order places two witnesses with their executions in one pair of executions
  by their views' content, where it placed them by emission, so two such witnesses may swap; the code flow and read
  sources the report shows for that occurrence would then be the other's. A differential test of each step shows
  whether that happens on the demo, eShop or the fixtures; a shift is explained or the step is wrong.
- **Moves by intent, with the hosted-transient rule:** a pair of one execution with one side on a hosted transient and
  the other on a holder every instance shares now stands, where the rule read the first side alone and skipped the pair
  whenever that side was the transient's: a list a hosted transient and a singleton both hold is one object, and the
  pair may race. The demo and eShop have no such pair, so nothing of theirs moves; the fixtures that have one gain its
  pairs and findings, as the run record of step C2 lists.
- **Never moves, measured on the demo and eShop:** which pairs of sites pair before the solver. A pair of accesses still
  stands when some pair of their executions passes, and every filter before the solver reads what it reads today, the
  hosted-transient rule aside.

### Options considered

**C2 alone.** The representation, each execution still walked and emitted on its own.

**C1a and C2.** Classes of executions on top of the representation.

**C1b and C2.** The shared walk below the execution on top of the representation, with or without C1a.

**C1 without C2.** Classes or shared walks, each execution still given its record per copy.

**A and the constant (ADR 0041), with #476 and #477.** The walk released before the fold, the fold sped up or split.

| | C2 alone | C1a + C2 | C1b + C2 | C1 without C2 | A + #476 + #477 |
|---|---|---|---|---|---|
| Record per copy | none | none | none | 24 B and more, as today | as today |
| Fold per copy | a lookup of numbers | the same, once per class | the same, a replay of keys | as today | faster, still per copy |
| Walk and emission | per execution | per class | per class of an entry; per execution only locks, reads and the replay | per class or entry | per execution |
| OrchardCore collection | the fold stops binding | 13.4 % of today's walk time by the counters | as C1a, more if entries are shared across roots | as C1a | the fold stops binding |
| `Nop.Web` collection | workers bound: 81 s a heavy execution on average | no gain: 218 sets of counters for 613 executions | removes the emission of shared walks, by an amount the census measures | as C1b, memory as today | workers bound, copies kept |
| `Nop.Web` memory | no record per copy; one access per key | same | same, resources and unknown effects kept once per scope | 9.4 GB per 224 M copies and growing | as today |
| Output | occurrences per pair of roots; ties | same | same | none | none |
| Work and risk | two to three weeks; medium: every reader of executions | one week on top; low: exact by recorded answers | three to five weeks on top; high: locks and reads per class | as C1 | days; low; does not reach the end of `Nop.Web` |

### Why this order

- **C2 is needed on both scopes whatever else is built.** A record per copy is 9.4 GB for the first 3.5 % of `Nop.Web` and
  cannot reach thousands of millions of copies; on OrchardCore it is 3.57 GB in the pairing dump, 30 bytes a copy, and
  the pairing sides beside it. Without C2 the fold stays one thread per copy, which is #477.
- **C1a is OrchardCore's multiplier and is cheap to make exact.** One collection per class would have taken 13.4 % of the
  walk time and made 11.2 % of the copies, by the counters, which only suggest the classes; the recorded answers decide
  them.
- **C1b is `Nop.Web`'s multiplier if its executions are many and heavy.** With one heavy execution per root, as in its
  first 613, `Nop.Web` has about 500 heavy executions: its 1,350 entries of roots at 2.7 executions per root. At 81 s
  each that is about 1.4 hours of 8 workers after C2, and C1b can wait. If the executions after the roots are as heavy,
  as the projection of 6.38 thousand million copies assumes, it is 17 hours, and C1b is the step that removes it. The
  census says which.
- **The census is cheap and settles the rest.** It is counters and changes no output. On the VM `Nop.Web` reaches its
  `entries` checkpoint 19 minutes after the start and collection after 39, so an hour of collection is a run of under
  two hours.

### Estimates against the gate

Targets: a peak of 24 GB private on the local machine (16 cores, 32 GB).

| | OrchardCore | `Nop.Web` |
|---|---|---|
| Today | collection 66 min on 8 workers; peak 39.92 GB private, at its end 38.44 GB private and 15.02 GB live (VM); pairing does not end | 26.45 GB private at `heap-end` before collection; stopped at 3.5 % of it at 115.4 GB private |
| Per copy, today → C2 | 30 B (`Access.Executions`, 3.57 GB in the pairing dump) and 7–17 B of pairing side since #473 → nothing; sets are shared (the model keeps 9,205 for 1.79 thousand million pairs) | the same |
| Per folded access, today → C2, by the `Nop.Web` dump | `Access` 232 B, fold index 44 B, list and array 313 B at 8 copies → `Access` without the variant's fields about 176 B, index 44 B; with its share of resources (721 B a resource since #474, 0.23 resources an access) about 400 B | the same |
| Folded accesses | 4.44 M | 26.1 M at 3.5 %; 55–60 M if new accesses come only with the roots' own executions (about 500), 100 M or more if the later executions bring their own |
| Live at the end of collection, C2 and #476 | about 11.5 GB: 15.02 GB less 3.57 GB of records per copy | the model's 9.6 GB and 22–40 GB of accesses and resources |
| What is still needed for 24 GB | #478 (before collection private is 2.5 times the live heap) and pairing (#475) | resources kept once per scope (C1b or ADR 0041 B1) and an access of numbers, about 130 B with its resources: 7–13 GB of accesses beside the model's 9.6 GB; and #478. 24 GB private is in reach at the low end of the census's range and not at the high end |

These are estimates from the dumps' bytes per object, not measurements of the new representation. Each step measures its
own.

### Steps

Every step keeps the gates of ADR 0040: `build/check-test-baseline.ps1` with the eShop snapshot at one worker and at four,
and `build/record-demo-models.ps1 -Check`.

1. **Census, counters only (#480).** The `entries` checkpoint gains, per execution, its own entries, its construction
   entries, its kind, and the class of its entry list; the `execution:` checkpoint gains the folded accesses the
   execution was first to make, split into new instance, new region, region of a root's invocation scope, new site and
   resource, and only other guards, held protection or locality, its copies from construction entries, and the key
   lookups another instance of the same body hit. No output moves. Measured by a run of `Nop.Web` on the VM to an hour of
   collection and a run of OrchardCore through collection. A few days; low risk.
2. **C2 (#481).** The set, the parts of the variant, the position, the key folded by the collector, one occurrence per
   pair of roots, the readers. A differential test collects and pairs the demo's scopes, eShop and the fixtures of
   `AccessFoldingTests` and the timing matrices both ways and compares findings, pairs of sites, occurrences,
   fingerprints, gaps and counters, and the representatives; every difference is one the section above names. A test of
   the race makes the map of folded keys answer late, so that workers build accesses the fold then drops, and early
   where the order allows it, and compares the folded accesses, their sets, their parts and every field of each `Access`
   with one worker's. Measured by a dump of OrchardCore at the end of collection and in pairing, and the fold's copies a
   second on `Nop.Web`. Two to three weeks; medium risk: `Access.Executions` has many readers.
3. **C1a (#482).** Classes by recorded answers. A test collects every scope of the demo and eShop with and without
   classes and compares the accesses, their sets and their parts; a mutation that drops one recorded question turns it
   red. Measured by OrchardCore's collection time and the classes it found. About a week; low risk.
4. **C1b.** An ADR of its own, on the census's numbers: the classes of a construction entry, the lock and read solves per
   execution over shared rows, the replay. Three to five weeks; high risk.

#476 is needed under every option: a collector waiting for the fold holds its walk, and under C2 a worker still waits for
the fold's window. Its first item, letting go of the walk before the queue, stands; its items on `State` and `Guards`
stand until C1b changes the walk. #477 closes with C2: the fold becomes a lookup of numbers per copy, and nothing is left
to split over threads. #478 is needed under every option.

## Consequences

- **An execution of an access is a member of a set**, not a record. A reader that needs one execution's view of an access
  asks for it and gets it made; a reader that walked `Executions` as a list walks the set and the parts. The tests that
  pin the representative of an occurrence (ADR 0038) pin it under the new position.
- **The position is no longer the order of emission.** Pairing reads only its execution number. Within one execution
  nothing pairing gives a finding's identity, group, representative or the solver's order reads the order of first
  meeting: a pair of one execution takes its first side, a witness's search its next execution and ties among witnesses
  their order from content. The candidate index still registers a collection several holder regions share, and joins
  buckets of overlapping cells, in the fold's order, as it did before step C2 (**The position**, #494).
- **The fold keeps its order and loses its cost.** It is still the one place where order is made (ADR 0040), and it does a
  lookup of numbers per copy.
- **ADR 0040 admits one shared structure that is no cache of a pure function:** the map of folded keys. A worker's answer
  from it decides only whether it builds an `Access` the fold may drop, never which `Access` is kept. A new structure of
  that kind needs the same argument and a test of its race.
- **A key of numbers is a new contract.** A field added to `Access` that pairing reads must enter the key or follow from
  it; the differential test fails where the demo or a fixture reaches a field the key leaves out.
- **C1a and C1b record what the collector asks of its execution.** A new read of the execution in the collector must be
  recorded, or the class test fails where a case reaches it. ADR 0041's rule that a new place reading the path must leave
  the path-free accesses holds the same way.
- **SPEC TD-039 scores a finding over its witnesses; CONTEXT says what an occurrence shows.** CONTEXT **Access** keeps its
  meaning: an access recorded once with the execution instances that run it so. The text of SPEC TD-076 stays as it is,
  and the code is brought to it in step C2. SPEC TD-039 said a finding takes the occurrence with the highest score and
  gathers the uncertainty of all occurrences; it now says the same of every witness, as the owner decided on
  2026-11-23. CONTEXT **Occurrence** says which pair of instances an occurrence shows where a pair of roots reaches the
  sites by several, and that the finding reads them all; its rule on **Candidate** gives one candidate per pair of
  roots and orientation with its witnesses. SPEC section 6's counters keep their definitions: `solver-budget-exhausted`
  counts candidates and `solver-queries` queries, as before, of candidates that are now one per pair of roots.
- **Accesses that cannot stand in a candidate are kept.** Construction-local accesses never enter pairing, and accesses on
  thread-confined regions are skipped there (83 % of eShop's copies). Keeping them only as counts would cut memory and
  move `accesses`, `accessExecutions`, the comparisons and the `confined` skips; that is decided after C2, on its
  measured bytes (#483).
- **The census can overturn the order.** If it shows the later executions of `Nop.Web` heavy and C1b's share of their
  work small, the next decision is not C1b but which accesses `Nop.Web` must keep at all (#483).
