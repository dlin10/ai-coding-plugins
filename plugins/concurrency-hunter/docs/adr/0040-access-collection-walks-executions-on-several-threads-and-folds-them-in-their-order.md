# Access collection walks the executions on several threads and folds them in their order

Status: Accepted (#461). The owner chose option A, one worker by default, and a gate at four workers. Several workers
need #463 first, and it is in (`e2d6ca0a`): the solved heap's containers drop their state keys once the solve settles,
so a read writes nothing.

This decision keeps everything [ADR 0038](0038-an-access-is-recorded-once-with-the-executions-that-run-it-so.md) makes
observable (#458): the folded accesses, their executions in collection order, each execution's position and variant.
It changes only who walks the executions. The heap a collection reads is the frozen solve of
[ADR 0016](0016-the-heap-is-frozen-after-the-solve.md), and nothing here writes to it.

## Context

`InterproceduralAccesses.Collect` walks a scope's executions one after another on one thread. For each one it builds an
`ExecutionCollector`, and `AccessFolding.Add` folds what it returns. Run 5g E-scale (`docs/runs/5g-E-scale.md`) has
measured the following:

- **OrchardCore.**
  - `OrchardCore.Application.Pages` has 577 executions of 128 roots.
  - The #454 dump after 60 minutes of collection held 8,110,072 accesses. One execution held up to 209,266 of them; the
    median was 540 and p90 70,281.
  - #458 collected for 145 minutes without reaching `accesses-end`, at 76 s of CPU per minute of collection: one
    analysis thread plus the server GC.
  - The thread-time trace of #458 put 99.6 % of the analysis thread in `ExecutionCollector.Emit`, half of it hashing
    strings. #464 replaced those keys with numbers. Its OrchardCore remeasure (`orchard-464-1`) has not run, so the
    split after #464 is not known.
- **nopCommerce.** `Nop.Web` has 9,177 executions. `nop-459-1` used 4,654 s of CPU in 3,378 s of wall time, about 1.4 of
  its 16 cores.

The trace of #458, as shares of the analysis thread (before #464):

| What | Share |
|---|---:|
| `ExecutionCollector.Emit` as a whole | 99.6 % |
| string hashing (`Marvin`, `String.GetHashCode`), own time | 51 % |
| dictionary lookups (`GetValueOrDefault`, `TryGetValue`) | 19 % |
| `Localities` (the construction query) | 16 % |
| `EmittedKey.GetHashCode` | 11 % |
| held locks (`HeldOverSpan`) | 5 % |
| walk (`UnknownEffectAccesses`, `DeepReadAccesses`) | 3 % |
| `AccessFolding.Add`, outside `Emit` | ≤ 0.4 % |

The rows below the first overlap.

The collectors are independent by construction. Each one walks one execution from that execution's own entries. It
keeps its own walk, paths, lock states, construction answers and unknown effects, and it returns a list of accesses.
What they have in common is either read-only or a cache of a pure function of the scope:

- **The solved heap.** It is frozen (`WroteState` throws while `Querying`). Its read journal is off after the solve:
  `Solver._reads` is null outside `ProcessOrSkip`, so `ReadState` records nothing. What a query names apart already
  goes to `ConcurrentDictionary` maps (`_named`, `_queryInterned`, `_operationsById`), and `_queries` is counted with
  `Interlocked`.
- **The execution model.** `ExecutionAnalysis` is built before collection. The construction query
  (`ConstructionIndex.Of`) takes one lock, `lock (_searches)`, over its whole search, because its scratch marks
  (`_marks`, `_stamp`) and its memos (`_searches`, `_answers`) are shared.
- **Scope caches the collectors share** (#443, #454, #464):
  - `StringPool`;
  - `bindingEvidence`;
  - `CollectorShares`: its resource maps, its `SequencePool`s of code flows, labels, gap checks and deciding calls,
    `TextNumbers` and the caches by number (`Instances`, `Regions`, `Locations`).

  None of them is thread-safe today. The comment on `StringPool` says why it was enough: "the collectors run one after
  another".
- **Scope helpers with lazy memos.**
  - `WalkGraph._steps` is a memo by instance id, and each answer depends on its id alone.
  - `UnknownCalls.Reach` keeps numbered regions, their steps, its walks and its shared bits (#462). Only its walk
    (`Of`, `BitsOf`) writes them, and only `SemanticGaps.Find` asks for it, on the coordinator before and after the walk
    of the executions. A collector asks only for `Starts` and `LibraryFieldsOf`: the first reads the heap and the
    program, the second a `Lazy<T>`.
  - `AsyncSegments._bodies` is a memo of each body's prefix and tail operations. The collector reads it through
    `ExecutionAnalysis.Runs` and `Follow`, and the construction query reads it too.
  - `GapOrigins` fills `_sites` in its constructor and only reads afterwards. The memo of facts by execution in
    `GapDecisions` belongs to pairing, not to collection.
  - `HandedFreshness._forwarders` is filled while its constructor runs its fixpoint.
  - `holders` and `slotFields` are `Lazy<T>` in the default `ExecutionAndPublication` mode, which is thread-safe.
- **The solved heap's containers wrote when read, until #463.** They stayed attached to their state keys after the solve
  (`TrackedContainer.Key` was not null). A read then made and kept keys:
  - `TrackedMap.Entry` adds the key of every entry read to `_keys`, a plain `Dictionary`
    (`if (!_keys.TryGetValue(key, out var entry)) _keys.Add(key, entry = Key.Child(key));`);
  - `TrackedMap._emptiness` and `TrackedSet._emptiness` are made on the first read of `IsEmpty` (`??=`);
  - `TrackedSet.Contains` sets `_itemsRead` and makes a child key;
  - `StateKey._all` is made on the first read of `Key.All` (`??=`).

  Each of these wrote only while `Key` was not null. Two workers reading one map would have added to one `Dictionary` at
  once. #463 freezes every attached container at `Settle`: the keys go and a read records nothing. It also makes
  `TypeSafety`'s memos and `SummaryCache` concurrent. The static caches the queries reach were safe already: the
  `ConditionalWeakTable`s of `MethodSummaryBuilder` and `UnseenWrites`, `ProgramIndex`'s `ConcurrentDictionary`s, and
  the `Regex` cache.
- **`AccessFolding`.** It is the one place where order is made. The position of an execution of an access is the running
  `ExecutionCount`. An access's own execution and variant are its first execution's, and the representatives the report
  shows follow from that. Its variant key compares the code flow by reference: two equal flows are one variant only
  because `CollectorShares.Flows` handed out one array for both.

So the collection is embarrassingly parallel in the walk and serial in the fold. The fold is cheap (≤ 0.4 % before
#464), and its order is the only one that is observable.

## Decision

Option A below. The executions are walked by a bounded set of workers. The coordinating thread folds their accesses in
the order of `ExecutionAnalysis.Executions`, exactly as it does now. Caches of the scope become concurrent caches of
the same pure functions, and what belongs to one execution stays in its collector.

- **How many workers.** One by default, so a run is what it was. One worker runs today's loop on the calling thread,
  check for check. A run of run 5g E-scale sets the number explicitly with `CH_ACCESS_WORKERS`, and a test sets
  `InterproceduralInput.Workers`. The number is not an `AnalysisLimits` field: those are the heap's limits
  (`Heap/MethodSummary.cs`) and the heap's code is not this change's.
- **The gate.** The demo and eShop run at one worker and at four, and both must match the committed files byte for
  byte.
- **What comes first.** More than one worker is safe only once #463 has detached the solved heap's containers from
  their state keys. This change is on top of it.

### What each structure becomes

| Structure | Today | Under A |
|---|---|---|
| `ExecutionCollector` and every field of it (`_constructions`, `_heldLocks`, `_rootNumbers`, `_identities`, `_effectResources`, walk, paths, lock states, unknown effects) | one per execution | unchanged: one per execution, and only one worker uses it. `_effectResources` keeps its plain dictionaries; the scope's resource caches, which collectors share, are the concurrent ones |
| an execution's entries (`ExecutionAnalysis.Entries`) | the loop asks for one execution's entries at a time; the model makes the list anew on each request and keeps none (ADR 0039) | the calling thread decides ahead only whether an execution is walked, with `ContainsKey`, which makes no list. The worker that takes the execution makes its entries, and they go when the fold has taken it. So no more executions' entries are alive at once than the window lets workers take, two per worker, and a test pins it. The list is made from the model's own entries, the construction entries and the execution numbering, all read-only after the build |
| `StringPool` | `HashSet<string>` | `ConcurrentDictionary<string, string>.GetOrAdd`; the caller keeps the returned string, never its own |
| `SequencePool<T>` (`Flows`, `Labels`, `GapChecks`, `DecidingCalls`) | two `HashSet`s | concurrent; the caller keeps the returned sequence. This is required, not an optimisation: `AccessFolding.VariantKey` compares `CodeFlow` by reference, so a flow pooled twice would split a variant and change the candidates of ADR 0038 |
| `bindingEvidence` | `Dictionary<HeapRegion, …>` by reference | `ConcurrentDictionary.GetOrAdd`; `BindingEvidenceScan` is a pure function of the region, and a value computed twice in a race is dropped |
| `CollectorShares.Resources`, `ValueResources` | nested `Dictionary` by instance, then summary access | concurrent on both levels, with the same rule: keep what `GetOrAdd` returns |
| `TextNumbers` and `Instances`, `Regions`, `Locations` | `Dictionary` with numbers in first-ask order | concurrent; numbers in race order. A number is an identity only (its XML doc already says so), so nothing that reaches the output depends on its value or order. Dictionaries keyed by numbers iterate in insertion order, which is per collector |
| `AccessFolding` (`_index`, `_variants`, `_roots`, the pools of paths, spawn sites and reads) | single-threaded | unchanged; the coordinator alone calls `Add`, in execution order |
| `unproven`, `folded`, `unresolved`, `filled` | `UnionWith` after each collector | unchanged: the coordinator merges them at the fold, in execution order, so the insertion order that a `HashSet` iterates in is today's |
| `ConstructionIndex.Of` | one lock over the whole search | scratch marks per worker (`ThreadLocal`), `_searches` and `_answers` concurrent. Kept under its lock, it would serialise the miss path of `Localities`, 16 % of the thread before #464 |
| `WalkGraph._steps`, `AsyncSegments._bodies` | `Dictionary` memos | concurrent memos. Each answer is a pure function of its key: an instance's steps, a body's segments. The caller keeps the answer the memo keeps, since `_takenSteps` compares steps by reference |
| `UnknownCalls.Reach` (numbers, steps, walks, shared bits, counters) | written by its walk | unchanged: its walk runs only in `SemanticGaps.Find`, on the coordinator before and after the walk of the executions; collectors call `Starts` and `LibraryFieldsOf`, which write nothing of it |
| `TypeSafety` | one per collector | unchanged: each collector makes its own; #463 has made its memos concurrent anyway |
| `GapOrigins` | `_sites` filled by its constructor | read-only after construction; unchanged |
| `HandedFreshness` | `_fresh` and `_forwarders` filled by its constructor | read-only after construction: only the constructor's fixpoint calls `IsForwarder`, and collectors call only `IsFresh`; unchanged |
| `TrackedMap._keys` and `_emptiness`, `TrackedSet._emptiness` and `_itemsRead`, `StateKey._all` | made on a read while `Key` was not null | not changed here: #463 detaches the containers from their keys at `Settle`, so a read makes and writes nothing |
| solved-heap queries | used by one thread | used by several. A query closure may touch no solver scratch field such as `_callArguments`, and each closure the collector calls (`Resolve`, `PointsTo`, `Located`, `StorageIdentity`, `FieldsOf`, `StaticRegionOf`, `DelegateCaptures`, `ClosuresOf`, `Edges`, `IteratorObjects`, …) is read for that. A region a query names goes to `_named` and `_queryInterned`, both `ConcurrentDictionary`, and `_anyNamed` is written with `Volatile`; the containers' own lazy keys are the row above |
| `WorkCancellation` | `AsyncLocal` frame of the stage | flows into the workers through the execution context, so `Check()` sees the stage's token. An observer of checks is called from several threads and must be thread-safe |
| counters (`executions`, `accesses`, `accessExecutions`) and checkpoints (`gaps:`, `accesses-end:`) | kept by the one thread | kept by the coordinator at the fold; workers report nothing. `DiagnosticCheckpoint` writes each line under a lock all the same: its log is opened anew per line and shared only for reading, so a second writer at once would fail |

### How the order is kept

- **The accesses and their positions.** Workers take executions in the order of `ExecutionAnalysis.Executions` and
  produce one list each. The coordinator folds list *i* only after lists *0…i−1*. `AccessFolding` therefore sees the
  same lists in the same order, and gives the same positions, first executions and variants.
- **Representatives per root.** ADR 0038's representative is the first execution of its root that reaches the access.
  Since the fold order is unchanged, the representative is too.
- **Site numbering.** `ConflictFindings` numbers sites from the collected accesses after collection, so it is unchanged.
- **What a race decides.** In a pool or cache, it decides only which of two equal values is kept. That is not
  observable, as long as every caller uses the kept one. It also decides the value of a `TextNumbers` number, which is
  an identity only.
- **The reorder window.** A finished list waits until every earlier one is folded. Workers run at most *K* executions
  ahead of the fold (*K* = 2 × workers, to be measured), so the waiting lists are bounded.

### Memory

- **Per worker.** One collector's working state, plus its list of accesses until the fold takes it.
  - The list is at most the largest execution's: 209,266 accesses at #454, about 224 bytes each at #443, plus what is
    not pooled. That is of the order of 0.1 GB.
  - The working state of one collector is not measured. Its paths, lock states and unknown effects are per execution,
    and they die when `Collect` returns.
- **The window.** It adds at most *K* lists of the size of their executions'. Most executions are small (median 540
  accesses).
- **The scope caches.** They stay single copies. This is why A keeps them shared: #443 and #454 made them shared to bring
  collection under the memory guard.
- **The thread limit.** It is one worker unless a run sets `CH_ACCESS_WORKERS`. Before a run sets more, one dump at the
  peak of a large execution gives the retained size of an `ExecutionCollector` (the "who holds" section of dumptool
  #459). The number is chosen from that against the guard: 58 GB locally, 110 GB on the Azure VM.
- **The garbage collector.** Allocation grows with the workers. The server GC already runs one heap per core, so it
  takes the load in parallel, but every query of the solved heap still returns a new `TrackedSet<string>` (audit
  L1-1). With *N* workers that garbage is made *N* times as fast. The numbered query API of L1-1 cuts it, but it is not
  a prerequisite.

### Cancellation and errors

- **One token.** The workers share a `CancellationTokenSource` linked to the stage's token and to the token of the work
  around it (`WorkCancellation.Token`), so a cancellation of either also reaches a fold that is waiting for a worker.
- **An exception in a worker.** The worker keeps it with its execution and goes on. When the fold reaches that
  execution in order, it cancels the source. The other workers stop at their next `Check()`, and the coordinator joins
  them all before it throws.
- **What the coordinator throws.**
  - If the stage's token was cancelled, the coordinator throws `EngineStageCancelledException("accesses", …)`, as
    today. `executions` counts the executions folded so far, and `accesses` and `accessExecutions` count those
    executions' accesses.
  - Otherwise it rethrows the exception of the earliest failing execution in collection order: every execution before
    it has been folded. A failure that is deterministic then reports the same execution whatever the timing. A worker
    can be up to the window ahead of the fold, so it may waste that much work before it stops.
  - A worker's own `OperationCanceledException` from the shared source ends that worker; it is not reported.
- **The guards.** The commit and deadline guards of a run stop the process, as now. A dump pause (`CH_DIAG_DIR`) stops
  the process too, so a pause sees every worker.

### What stays sequential, and what it leaves

The coordinator does the following on one thread:

- before the walk:
  - `WalkGraph`, `Discoveries`, the first `SemanticGaps.Find`, `UnknownCalls.Of`, `HandedFreshness`,
    `LibraryInternalObjects` and `WorkHosts`;
- during the walk:
  - the fold: `AccessFolding.Add`, three long strings hashed per copy (audit FN-10);
- after it:
  - `AccessFolding.Accesses`, the second `SemanticGaps.Find` (FN-11) and `Coverage`.

Two more bounds come from the walk itself:

- **The largest execution.** The wall time cannot be shorter than the slowest single execution. Measured by accesses,
  the largest is 2.6 % of the 8.1 million at #454. That is no bound at 8 workers, if time follows accesses.
- **Shared critical sections that stay.** No lock is left on the walk's path under A. The construction lock is the one
  that would cost the most if it stayed.

Before #464, the serial share *s* was about 0.4 % of collection time, plus the setup. The setup is small against
145 minutes. If #464 halved `Emit`, *s* is about 1 %, and Amdahl allows 1 / (*s* + (1 − *s*) / *N*): about 7.5× at
8 workers and 14× at 16. Realistically it is lower: memory bandwidth, the server GC sharing the cores, and lists waiting
in the window. A gain of 4–6× at 8 workers is the estimate to measure against. The run `orchard-464-1` gives the
split after #464, which replaces the "if halved" above.

### Gates

The output must not depend on the number of workers:

- The demo and the eShop snapshot run at one worker and at four. Both must match the committed files byte for byte:
  findings, site pairs, occurrences, fingerprints, gaps and counters, `accesses` and `accessExecutions` included.
- A test collects the demo's scopes repeatedly at four workers. It compares the folded accesses with one worker: their
  order, the executions of each, their positions and variants.
- **Tests that count checks.**
  - `WorkCancellationTests` count checks only where one worker collects. A collection made through `EngineFixture.Collect`
    keeps `Workers` at one, since only `ScopePipeline` reads `CH_ACCESS_WORKERS`. The pipeline cell is cancelled at root
    discovery, before access collection. So the counts hold at any setting, and a cache computed twice in a race, which
    makes extra checks, cannot reach them.
  - `StageCancellationTests` cancels deep inside each pipeline step, access collection included, so at four workers it
    sees several threads. Its condition was written for one thread: no check after the one that fired. It now counts
    checks by thread. The thread that fired checks no more, and any other thread checks at most once more: the check
    that stops it, as "Cancellation and errors" says. With one thread the condition is the old one.
  - A worker whose walk the cancellation stopped ends there, so it makes that one check and no other.
- `build/check-test-baseline.ps1` with `CH_ESHOP_ROOT` and `build/record-demo-models.ps1 -Check` exit 0.

### Options considered

**A. Workers over executions; scope caches concurrent; the fold in execution order on the coordinator.** As above.

**B. Workers over executions with everything thread-local.**

- **What each worker keeps.** Its own `StringPool`, `SequencePool`s, `TextNumbers`, resource maps and binding evidence.
- **The fold.** It re-pools every list of an access into the scope pools before it folds. Re-pooling is required, or
  equal code flows from two workers would be two variants.
- **What it costs.**
  - Memory: the scope caches exist once per worker. These are the caches #443 and #454 shared to cut 5.6 GB and more.
  - Time: the re-pooling makes the serial part grow from a hash per copy to a walk of every list of every copy.
- **What it saves.** No concurrent structure and no lock: every worker's code is single-threaded, as today's is.

**C. Two threads: the walk on one, the fold on the other.** It is a producer and a consumer, with no parallel walk. It
gains at most the fold's share, under 1 %.

**D. Scopes in parallel.** Different scopes would run on different threads. OrchardCore's wall is one scope,
`OrchardCore.Application.Pages`, so this does not shorten it. Each scope also carries its own heap and execution model,
and their memory would add up.

**E. Precompute the scope caches in parallel, then walk on one thread.**

- **What it would be.** Resources per instance, region facts, the reach and walk step memos, and the construction
  answers, all filled in parallel before the walk.
- **Why not.** The bulk is the walk and `Emit` of each execution (99.6 %), and which construction answers it needs is
  known only while walking. It also fills caches for questions no execution asks.

| | A | B | C | D | E |
|---|---|---|---|---|---|
| Parallel part | walk and `Emit` of every execution | same | none | whole scopes | cache filling only |
| Serial part | setup, ordered fold, finish | setup, re-pool and fold, finish | everything but the fold | the largest scope | the walk |
| Memory | one copy of the scope caches; per worker, one collector and the window | scope caches × workers | as today | heap and model × scopes | as today plus unused cache entries |
| New concurrency | concurrent caches; lock-free construction query | none in the walk | one queue | none in a scope | parallel fill |
| Order | by the fold | by the fold, after re-pooling | by the fold | per scope | as today |
| Gain on OrchardCore | up to ~*N*, estimate 4–6× at 8 | lower than A: larger serial part | < 1 % | none | small |

### Why A

- **Only A and B parallelise the part that takes the time.** C, D and E leave `Emit` on one thread for the one scope
  that does not finish.
- **A keeps the memory decisions of #443 and #454.** B multiplies the scope caches by the workers and makes the fold
  re-pool everything.
- **The order is made in one place in both.** A makes no new decision about it. `AccessFolding` already makes all of
  it, and it keeps doing so on one thread in the same order.
- **The concurrent caches are caches of pure functions.** A race decides which of two equal values is kept, or what
  number an identity gets. Neither reaches the output, and the gate at 1 and *N* workers checks exactly that.

## Consequences

- **Threads in collection.** Access collection is the first stage of the engine that runs on several threads.
  - Scope caches and helpers that the collectors share become concurrent.
  - What one execution owns stays single-threaded.
  - A new cache shared by collectors must be the cache of a pure function, and its callers must use the value it keeps.
- **The construction query.** It loses its global lock. Its scratch marks are per thread, and its memos are concurrent.
- **The solved heap.** Its queries are called from several threads. Each query closure the collector calls is read
  for solver scratch state before the change, and the repeated-collection test checks the outcome.
- **Run configuration.** The number of workers comes from `CH_ACCESS_WORKERS`, or is one. The checkpoint
  `accesses-end:<scope>` reports it. A per-execution checkpoint `execution:<scope>` reports each walked execution as the
  fold takes it: its place among all executions (`n=K/N`), its id, its accesses and the milliseconds of its walk. No
  checkpoint gave the number of executions before; the OrchardCore run of #464 needed a dump to learn how far it had
  come. The run metadata records no analysis limits, so it does not record the
  workers either. A test can send one worker through the workers' path (`InterproceduralInput.ThroughWorkers`): the loop
  and the workers share the walk and the fold, and the test pins that the dispatch adds nothing.
- **#463 is a precondition of more than one worker.** The solved heap's containers must be detached from their state
  keys after the solve settles, so a read of the heap writes nothing (see the table). The gate at four workers ran on
  a tree with #463.
- **Tracing.** The trace of a run no longer has all collection on the thread with `ScopePipeline.Run`. Reading a trace
  of collection means summing the workers. The thread-time tool of #458 needs that change, and so does `stage-watch`,
  which samples the analysis thread.
- **Measurement.** The per-execution checkpoint shows the imbalance the window and the dispatch order face. If one
  execution dominates, a dispatch order by expected size is a later change; it does not touch the fold order.
- **What the OrchardCore remeasure of #464 found.** It ran before this change, on one thread, and stopped at its 8-hour
  deadline after 69 % of the 14,560 executions (run 5g E-scale, section #464).
  - The serial fold took 1–3 % of the thread, so most of the time is inside the walk that workers share out.
  - 99.8 % of the copies an execution made in the last hour and a half folded into accesses already kept: collection
    time grows with the number of executions, not with the 3.5 million distinct accesses.
  - Workers divide that time by a constant, and they do not remove the repetition. Only caches the scope keeps across
    executions remove it: the unknown-effect cache that ADR 0038 left for later, and resources and regions by number.
  - When the fold key moves to numbers (audit FN-10) is still the owner's call.
