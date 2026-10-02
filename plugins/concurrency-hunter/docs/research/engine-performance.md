# Engine performance — research notes for phase 5d run A3

Evidence from the profile of 2026-10-01 that put run A3 into SPEC 14.3: the engine on decompiled `System.Private.CoreLib`
(SCENARIOS question 85). The decision lives in the "5d run A3" row of SPEC 14.3; this file carries what that row does not:
what was measured, why each stage is slow or large, why the proposed fixes keep the results bit-identical, and how to
measure again. Read it before planning or implementing run A3.

This file and `engine-performance/` are temporary. Delete both when run A3 closes, once their content lives in code, tests
and the SPEC.

| What | Where |
|---|---|
| The CoreLib decompile/compile spike, the engine harness, the profiling scripts and the retained-size analyzer | `engine-performance/prototype/` — how to rebuild and run them is in its `README.md` |
| Raw results of every run below | `engine-performance/data/` |

## Setup

- Corpus: `System.Private.CoreLib` 8.0.31 decompiled one file per top-level type (17 s, 9.8 MB of C#) and compiled by
  Roslyn as the corlib (errors in 151 of 30,614 bodies). Driver: `Driver.Run() => string.Join(",", new object[] { new Probe() })`.
- Engine: the build of cca4c8c (5d run A2), driven through `EngineFixture`'s stages by the harness, which replicates
  `ReachScope` with one hand-made root.
- Two scopes. **four**: CoreLib's own overrides of `ToString`, `Equals`, `GetHashCode` and `Dispose` made bodiless; 10,300
  members, 10,621 bodies. **all**: every one of the 944 dispatch slots cut; 957 members, 974 bodies.
- Tools: `dotnet-trace` (`--profile dotnet-sampled-thread-time`, ~100 Hz per thread, exported to Speedscope),
  `dotnet-counters` (System.Runtime meter), `dotnet-gcdump`, `dotnet-stack`; retained sizes by `gcretained` (dominator
  tree).

## Heap solve: one linear scan, not size

The `four` solve did not finish in 10 minutes; it runs on one core (1.00–1.05 cores in the counters). 180 s sampled on the
solver thread:

| Frame | Inclusive | Exclusive |
|---|---|---|
| `Solver.Propagate` → `Process` | 100% | |
| `Solver.Call` | 98.7% | |
| `Solver.Eval(AbstractValue)` | 95.8% | |
| `Solver.ReferenceLocations` | 94.6% | |
| `Enumerable.Where<(string,int,string,string)>.ToArray` | 94.5% | **88.1%** |
| `Solver.DeadReceiver` / `Solver.EligibleReceivers` (callers of the above) | 52.6% / 41.4% | |
| `Thread.PollGC` (the GC poll inside that same loop) | 8.1% | 8.1% |
| `SummaryCache.Get` → `MethodSummaryBuilder.Build` (new bodies) | 2.7% | |

What the samples say:

- **The scan.** `ReferenceLocations` resolves a `ReferenceParameter` by `_edges.Where(edge => edge.Callee == instance.Id)`
  and a `ReferenceCall` by `_edges.Where(edge => edge.Caller == … && edge.Operation == …)`: a pass over every call edge
  of the run (a `HashSet` of 38–76 thousand tuples, by the size of its entry array) per hop. 96% of the CPU is that loop.
- **The depth.** It recurses up the callers to `MaxAccessPathDepth` (8); 70% of the samples sit at depth 8, so the cut
  fires all the time. Nothing memoizes the result, and every pass of `Propagate` re-processes every instance.
- **Twice per call.** `Call` evaluates the call's receivers in `DeadReceiver` and again in `EligibleReceivers`. CoreLib
  hits this everywhere because instance calls on structs pass `ref this`.
- **The same pattern elsewhere**, cold here but next once the scan is gone: `Component` rebuilds successor and
  predecessor maps from all `_edges` per call, `CountRound` runs `_edges.Any(...)`, `Closure(region)` scans all
  `_fields` per region, and `ExecutionModel.FlowOf` scans the whole visit list per execution.

**Still adding facts, slowly.** Two gcdumps, at minutes 3:11 and 8:07 of the solve:

| | 3:11 | 8:07 |
|---|---|---|
| `InstanceState` | 10,213 | 12,117 |
| `MethodSummary` (bodies reached) | 2,719 | 3,218 of 10,621 |
| `HeapRegion` | 3,199 | 4,038 |
| Points-to of fields (`Dictionary<(string,string),HashSet<string>>`, retained) | 2.2 MB | 4.2 MB |
| `Solver`, retained | 37.5 MB | 48.3 MB |

New bodies arrive in bursts about every 55 s (27% of the CPU in the first 15 s, 0–3.5% after); the rest is re-processing.
The number of passes could not be read: `_changes` and `_changedRounds` are private, and the steps between passes are too
short for 100 Hz sampling.

**Memory is bounded.** Working set 888–906 MB for 10 minutes, gen2 656 → 682 MB, LOH 77 → 68 MB; gen0/gen1/gen2
collections 1,625 / 145 / 5 (two of the gen2 forced by the gcdumps); allocation 3–6 MB/s with the bursts; GC pause ≈ 0.
Of 645 MB live, 240 MB are IR bodies and 210 MB strings; the solver's own state is under 50 MB.

## Executions: the walk enumerates paths

On `all` the solve takes 10 s; `ExecutionModel.Build` then grows by ~250 MB/s (943 MB → 2 GB in 5 s) and was stopped at
15.9 GB working set after 3 min 13 s, by a guard for the machine (free commit under 1.5 GB). It never finished, so nothing
here comes from the public `ExecutionAnalysis`; the counts come from the heap.

| | 2 GB | 4 GB |
|---|---|---|
| Live heap | 1,975 MB | 3,900 MB |
| `_visits` (`HashSet<(execution, instance, intervals key, segment, tail)>`), retained | **1,572 MB (80%)** | **3,483 MB (89%)** |
| Interval-key strings > 10 KB | 43,628, average 35.5 KB | 74,778, average 46 KB |
| Visits | 44.2 thousand | 75.4 thousand |
| Distinct instances visited (the growing `Dictionary<string,HashSet<string>>`, by elimination `_instanceExecutions`) | ~901 | ~1,394 |
| Executions / still queued in `_pendingEntries` / with steps | 30 / 29 / 1 | 30 / 29 / 1 |
| `ExecutionStep` / `CollectedAccess` / `SpawnOrigin` | 3,072 / 0 / 0 | 5,581 / 0 / 0 |

From a working set of 9 GB gen2 stays at ~6.45 GB and the growth moves to the LOH (2.3 → 10.8 GB): the keys pass 85,000
bytes. The allocation rate falls from ~800 to 50–180 MB/s because each pop sorts and joins an ever larger set; both stack
snapshots are in `Builder.Walk` → `string.Join`, one growing the string builder, the other sorting the set. The `Kind` of the 30 executions is not in a gcdump
(a value, not an object); with no `SpawnOrigin` they are the root and executions created before the walk (type
initializers, unknown enumerations, unknown delegate calls).

So neither the number of executions nor accesses per region dominate: the walk of the first entry of the first
execution did it all. `Walk` deduplicates a visit on the whole set of objects under construction. The set only grows down
a path (each constructor edge adds what it constructs, several regions when the receiver is imprecise), so an instance is
walked again for every distinct set on the paths that reach it (~50 per instance at 4 GB and rising), and each visit keeps
a key of O(|set|) characters. Memory is visits × set size, and both grow. The key is also built before the duplicate check,
which is the early ~800 MB/s of garbage.

### Why two sets per node are exact

The interval set has three readers (cca4c8c):

1. the visit key in `Walk`;
2. `_chains` in `Walk`, read by `Published`: object → instances of its constructor chain;
3. `Collect`: an access to region R is construction-local when R is in the set and not published.

The visit list's other readers (`Build`'s `visits`, `FlowOf`) use only the instance and the segment. For each node
(execution, instance, segment, tail) take:

- **MayIn** — the union of the sets over all paths (least fixpoint, from the entry's set);
- **MustIn** — their intersection (greatest fixpoint; "unset" stands for all objects).

Then:

| What the walk produces now | With two sets |
|---|---|
| `Collect` emits `local = true` iff some visit has R in its set | R ∈ MayIn |
| `Collect` emits `local = false` iff some visit has R outside its set | R ∉ MustIn |
| I ∈ `_chains[o]` iff o is in some visit's set of I | o ∈ MayIn(I) |
| What an edge adds | depends only on the edge: the `!Contains` checks only skip duplicates |

The outputs are identical. A node is queued again only when MayIn grows or MustIn shrinks, at most once per allocation
region each, so the walk is polynomial. `Collect` then resolves an instance's accesses once instead of once per visit. The
invariant to write on `Walk`: the set's readers ask only "is R inside on some path" and "is R outside on some path"; a
reader asking about two objects on the same path would break the equivalence.

## Run A3

**Placement: after B1, before B2.**

- B1 brings the decompiler into the exe (`LibraryCompilation`), the shared stages of a scope (`ScopePipeline`),
  `generate` and the model evals, so the benchmark can run on the product's own stages instead of this file's harness.
- B2 reads effects from the driver's accesses — `Collect` and `IsConstructionLocal`, the ground of task 5.
- B3 (question 85, in-run generation by dependencies, the TD-034c budget mechanism) and run C (the budget value, PRD 6.1)
  are calibrated on the engine's speed.
- B1's closure bound (1500 bodies, `ModelGenerator.CLOSURE_BOUND`) does not protect from the executions growth: it hit
  at 974 bodies.

The name follows A1 and A2, runs on the engine, and leaves the B2, B3 and C references in the SCENARIOS rows as they are.

| # | Task | Gate |
|---|---|---|
| 1 | Counters in `HeapCounters` and the diagnostics: `Propagate` passes, `Process` calls, `ReferenceLocations` calls and the hops cut at depth 8, `Walk` visits | exact |
| 2 | Receivers evaluated once per call (`DeadReceiver` and `EligibleReceivers` share one `Eval`) | exact |
| 3 | `_edges` indexed by callee and by (caller, operation), kept on insertion; the same for the other linear scans above | exact |
| 4 | `ReferenceLocations` memoized per pass by (instance, target), cleared where `_reachIndex` is | exact |
| 5 | `Walk` on MayIn/MustIn per node, with a test comparing every execution of demo and eShop against the old walk | exact |
| 6 | A worklist of instances whose inputs changed, instead of re-processing all of them each pass — only if task 1's counters show many passes | exact |
| 7 | The CoreLib benchmark as a repository script on B1's stages (`LibraryCompilation`, `ScopePipeline`) with `string.Join` and both scopes, measurements recorded. Not through `generate`: it refuses CoreLib members (reason `corelib`) and stops at its 1500-body closure bound | measurement |
| 8 | Ref locations propagated forward in `Bind`, like parameter values, so a `ReferenceParameter` is a lookup; the depth cut no longer applies to ref chains (`MaxAccessPathDepth` keeps bounding field paths) | new snapshots, approved |

- **Exact gate (tasks 1–7).** Demo findings, the demo and eShop snapshots and B1's model-eval fates are bit-identical to
  before the run; the equivalence test of task 5 passes. Speed is measured and recorded, not a threshold: what is reachable
  is not known yet.
- **Task 8 is last.** Tasks 1–7 first prove the results unchanged, so any snapshot difference in task 8 is task 8's alone.
  It changes results by design: the cut silently drops locations past 8 hops. Task 1's truncation counter tells beforehand
  whether demo and eShop change at all. It gets its own demo case (a `ref` parameter forwarded nine times, then written:
  lost before, found after, for instance `ref-parameter-chain-beyond-depth`), every changed finding is explained, and the
  snapshots are re-recorded on the owner's approval, as eShop's were in A2.
- **Measure after task 3.** Removing the scan bounds the solve's gain at ~25× (the remaining 4%); what then dominates —
  the recursion tree or the passes — decides tasks 4 and 6.
- Question 85's decision on CoreLib stays in B3, with these measurements.

### What the tasks leave to the plan

- **A stopgap for task 5, not a substitute.** Interning each interval set as an id (hash-consing, a new id only on a
  constructor edge, the parent's id otherwise) removes the key strings — 89% of the heap — and the sort and join per pop,
  but not the visits, which stay one per distinct set on the paths and keep growing. Use it only if task 5 slips.
- **Task 6's dependencies.** An instance is processed again when an input it reads changed: its parameters and receivers,
  the cells and the fields of the regions it loads, its callees' call results, returns and ref results, the static regions
  it touches, the delegates and holders it consumes. A missing dependency loses facts silently, so the exact gate is the
  guard; a test can also end the worklist with one full pass that must change nothing.
- **Snapshots that skip.** The eShop snapshot (`EShop_measurement_matches_the_snapshot`) and the census
  (`UnsupportedCensusTests`) are skipped when `CH_ESHOP_ROOT` is not set; without it the exact gate says nothing about eShop.
- **Algorithms before scope.** At 10,621 bodies the solver's own state is under 50 MB: the closure's size is not what
  stops CoreLib. B1's 1500-body closure bound and the dispatch-slot exclusions guard the cost of the current algorithms;
  revisit them after A3, with question 85 in B3.

## Measuring again

- `dotnet-gcdump collect` gives up after 30 s by default; a 2 GB heap needs `-t 900`. `report` gives shallow sizes only —
  use `gcretained`.
- The Speedscope export puts `CPU_TIME` / `UNMANAGED_CODE_TIME` pseudo-frames at the leaf: exclusive time belongs to the
  nearest real frame. `PollGC` frames mark a tight loop that does not allocate.
- A gcdump keeps types, sizes and references, not field values: an enum such as `ExecutionKind` cannot be counted from it,
  and a collection's entries are counted from its array's references (three per `_visits` entry, two per dictionary entry).
- Profile a copy of the engine DLLs (`engine\bin2`), not the repository's bin another session may be rebuilding, and stop
  only the harness's own PID.
- The executions stage on `all` reaches 16 GB in about three minutes; run it with a guard on the system's free commit, not
  on the process.
