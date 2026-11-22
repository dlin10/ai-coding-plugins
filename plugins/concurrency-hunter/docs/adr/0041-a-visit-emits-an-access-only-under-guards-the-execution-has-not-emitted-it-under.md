# A visit emits an access only under guards the execution has not emitted it under

Status: Accepted (#467). The owner chose option A, with the counters it is measured by, and #470 beside it.

This decision changes how much work `ExecutionCollector.Emit` does for a visit, and nothing it returns. The walk of
[ADR 0018](0018-access-paths-carry-no-construction.md) and SPEC TD-090, the fold of
[ADR 0038](0038-an-access-is-recorded-once-with-the-executions-that-run-it-so.md) and the workers of
[ADR 0040](0040-access-collection-walks-executions-on-several-threads-and-folds-them-in-their-order.md) stay as they
are. It removes the repetition inside one execution. The repetition across executions is left, and "Consequences" says
what would take it.

## Context

Access collection reaches a terminal status on neither large corpus of run 5g E-scale (`docs/runs/5g-E-scale.md`):

- **OrchardCore, one thread, code b9743dbd.** 468 minutes of collection without an end, 22–23 executions a minute of
  14,560. The fold held 88.9 million copies as 3.5 million accesses, and 99.8 % of the copies made in an hour and a half
  folded into accesses already kept. `Emit` is 95–98 % of the thread.
- **Nop.Web, eight workers, code 86c17332 (`nop-461-1`).** The fold took 44 executions of 17,442 in 14 minutes and then
  stood for 67.
  - The eight main executions of admin actions made 597–631 thousand accesses each in 224–615 s.
  - Seven executions of n=45…60 ran for more than an hour each. A dump shows about 0.5–0.6 million distinct keys in
    each of their `seen` dictionaries: no more than the finished ones.
  - The workers' trace at the stall has no insertion into `seen`. It has `Fresh` 16.4 %, `Facts` 11.0 %, `CallPaths`
    9.5 %, `IdentityOf` 9.2 %, `LocatedRegion` 6.9 %, `Localities` 6.8 %, `HeldOverSpan` 5.5 %, and the lookup by
    `EmittedKey` with `List.FindIndex` 20 %.

So an execution spends its time on accesses it has already recorded. The code says why (references as of 1a249266,
`Accesses/InterproceduralAccesses.cs`):

- **A visit is one path.** `Visit` walks breadth-first from each entry of the execution, and reaches a state, an instance
  with a segment of its body, on at most 16 paths of its own per entry and on one more that forgets where it came from
  (TD-090, lines 1348–1406). Each path is an item of `_visits`. An execution of `Nop.Web` has about 8,200 construction
  entries (ADR 0039), and the walk from an entry goes through the constructions of its own execution, so a dependency
  is walked from the root, from every construction around it and from itself.
- **`Emit` does all of its work for every visit** (line 2655). For each access of the instance and each resource it
  touches, it looks up the identity, the feeding reads, the freshness, the region's facts, the held locks, the located
  region, the construction-locality and the call paths, and makes an `EmittedKey`. Only then does it ask whether the key
  already has an access under guards this visit's guards include (line 2835), and drops the visit's access if so.
- **A visit's path reaches the emission in a few places only.**

  | What | Where | Read for |
  |---|---|---|
  | the guards of the calls on the path | `PathGuards` | the guards of every access of the visit |
  | the values the path binds | `Predicates`, `Bind` | an access's own guards and its selector term |
  | the places a reference resolves to | `ResolveReferences` | a reference access |
  | the storage of a collection handed over ready | `HeldCollections` | a known call's effects and an unknown effect's list of accesses |
  | the locks of enumerations on the path | `EnumerationPathLocks` | the held locks; null on most paths |
  | whether the visit hosts work without a body | `_hosts`, `_workHosts` | whether the body runs and what it holds |
  | the path's steps | `CodeFlow` | an access the visit emits, and no other |

  Everything else the key is made of is the same on every visit of the instance in the execution: the resource of a
  summary's access (a scope cache since #454), the operation and its feeding reads (worked out before the visits are
  read), the construction-locality (asked per execution, instance, operation and region, ADR 0018), the locks where no
  enumeration adds any (kept per instance and operation since #464), the root of the call path (the body's first
  visit, not this one), freshness and library-internality.

So for an access whose emission reads the path only for its guards, two visits of its instance differ in one thing: the
set of guards. The rule `Emit` already applies to them is "a path under every guard of another and more adds nothing"
(lines 2797–2799). It is applied key by key, after all the work.

A temporary counter measured this on the demo and eShop (one `metrics` run each, 1a249266). Of 14,222 key checks,
3,260 hit a key already recorded: 31.0 % on eShop, 15.7 % on the demo. The rule below, counted without being applied,
predicted 2,774 of the hits (79 % and 96 %), and no access was emitted where it would have skipped. The large corpora
were not measured: no run counted visits or hits.

## Decision

Option A below. The collector remembers, for each access of an instance, the sets of guards it has emitted the access
under in this execution. A visit whose guards include one of those sets skips the access before it reads the access's
resources.

- **Which accesses.** An access of a visit is *path-free* when the emission reads the visit's path for its guards alone:
  - it is the summary's own access or one of an unknown effect's kept list, so it is one object on every visit;
  - it has no selector term;
  - no enumeration on the visit's path adds locks;
  - the visit hosts neither a sequence enumeration nor work without a body.

  Every other access is emitted as it is today: a reference access, an access with a term, an effect of a known call
  made anew for the visit, and any access of a visit with enumeration locks or of a host.
- **The rule.** Let *P* be the key bits of the guards a path-free access runs under on a visit: its own guards, bound
  along the path, and the path's.
  - If the collector has applied the access of this instance under some *P₀* ⊆ *P*, the access is skipped whole on
    this visit: no resource, locality or call path of it is read.
  - Otherwise the access is emitted by today's code, and *P* is recorded as applied. An access none of whose resources
    gets as far as its guards looks up no key, and nothing is recorded for it.
  - The rule is asked only of an access recorded before. Today's code asks for the guards of such an access and for the
    locks of the visit's path on this visit too, so the rule asks nothing new: it asks them earlier.
- **Why it returns the same accesses.** When *P₀* was applied, every key of the access got an access under *P₀* or
  already had one under a subset of it. A kept access leaves its key's list only when one under a subset of its guards
  takes its place (lines 2837–2844). So at a later visit under *P* ⊇ *P₀* every key still holds an access whose guards
  *P* includes, and line 2835 would drop each of them. The keys are the same on both visits, because nothing else they
  are made of reads the path. What the skipped code writes besides accesses, the unresolved-base counter, does not read
  the path either and was written by the first visit.
- **Granularity.** The accesses of a visit that share one list of own guards, as all accesses of one unresolved call
  do, are decided together: they have one *P*. A first implementation may decide per access.
- **The same rule, earlier.** A visit's known-call effects and unknown effects are listed before its accesses are
  read, and the list is made again on every visit. Where a call is handed no collection ready, its list does not read
  the path, and the rule can be asked before the list is made. This is taken when the counters show the listing on top
  after the first step; on eShop it is 41 % of `Emit`.
- **Where the memory lives.** In the collector, like `seen`, and it goes with it. No collector reads another's.
- **Counters.** With `CH_DIAG_DIR` set, the `execution:<scope>` checkpoint also reports the execution's entries, visits,
  distinct states, resources gone through, key checks, hits, emitted and replaced accesses, the accesses the rule
  skipped with the key checks they would have made, and the milliseconds of the walk, the locks, the dependencies and
  the emission. The `gaps:<scope>` checkpoint reports the sum of the execution sets of `ExecutionAnalysis.Accesses`,
  which bounds `accessExecutions` from below before collection starts.

### Options considered

**A. A memory inside the execution.** As above.

**B. Caches of the scope.** What `Emit` works out from the instance, the access and the heap alone moves to caches the
collectors share, as caches of pure functions (ADR 0040):

- the accesses of an unknown effect or a known call's deep read, by instance, call and the storages handed ready, with
  what the walk writes besides them (`UnseenFilled`, `UnprovenReferences`, `UnresolvedBases`) replayed in order;
- the rows of an access: each resource with its identity number, region facts, located region, library-internality and
  the pure part of freshness;
- the body of an `Access`, by the numbers of every value it is built from, so that an execution that makes an access
  another has made takes the body and adds its variant, and the fold matches by reference.

A finished access cannot be cached per instance. It also depends on what the execution gives each row: the locks of
its own fixpoint, the reads that feed a write over the instances it visited, the construction-locality and the root of
the call path. Working those out per row is the loop itself.

**C. The loop turned around.** An access is collected once, and the executions that run it are a relation, as ADR 0017
did for the execution model's accesses and ADR 0039 for construction entries.

- **C1. A walk per entry, shared.** A construction entry is shared by the executions of its region. Its visits and rows
  are made once where the executions' contexts agree, and each execution replays the keys through its own `seen`.
- **C2. An access carries a set of executions and a variant per root.** No record per copy is kept, and the position of
  an execution of an access is defined anew.

What stands in the way is per execution today: the lock fixpoint over the execution's own edges, the feeding reads,
and which instance of a body and which path of a body came first in the order of the execution's entries. `EmittedKey`
names the body, not the instance, so two instances of one body on one resource are one access in an execution, with the
instance met first.

**D. A cheaper constant only.** #469 (numbers for the remaining string keys) and #470 (the first receiver without a
sort in `CodeFlow`).

| | A. Memory in the execution | B. Scope caches | C. Turned around | D. Constant |
|---|---|---|---|---|
| Repetition inside an execution | removed for path-free accesses | each row cheaper | C1: as A, per entry | each row cheaper |
| Repetition across executions | stays | each copy cheaper | removed | stays |
| Findings, site pairs, occurrences, fingerprints, gaps, counters | unchanged | unchanged while the caches are pure | C1 unchanged; C2 keeps findings and changes the order of candidates | unchanged |
| Order, positions, variants | unchanged: a skipped access emitted nothing | unchanged | C1 unchanged; C2 redefines positions | unchanged |
| Model, ADR 0038, ADR 0040 | none | new shared caches | the execution of an access is represented anew; the fold and the witness search read sets | none |
| Memory | a list of guard sets per path-free access with rows, per collector | grows: the scope keeps what a collector kept for one execution | falls: no record per copy under C2 | as today |
| Gain on `Nop.Web` | the stall: at least 6× on the seven executions, a lower bound | at most 2.1× at the stall, by trace shares | needed if the copies do not fit | at most 1.5× at the stall, 2.6× in the first minutes |
| Gain on OrchardCore | not measured | at most 1.45×, by trace shares | up to the 25 copies per access | at most 1.2× |
| Work and risk | days; low | one to two weeks; medium | weeks; high | a day or two; low |

### Why A

- **It is the stall.** The stalled executions look up keys they have recorded. `IdentityOf` runs only for accesses that
  are one object on every visit, and `freshness.IsFresh` only for a summary's own accesses, so the trace is made of
  path-free accesses. A skips exactly those.
- **It changes no output by construction.** The rule answers ahead a check `Emit` makes anyway. Nothing is shared
  between collectors, so nothing of ADR 0040 is touched, and the fold gets the lists it gets today.
- **B and D keep both multipliers.** They make a row or a copy cheaper, by at most 2.1× where the stall is longer than
  6×.
- **C is the only option that removes the multiplier of executions, and it is not decided here.** It replaces what an
  execution of an access is, and the measurements that choose between C1 and C2 do not exist yet: how many copies a
  whole scope makes, and what a copy costs once a visit is not repeated.

## Consequences

- **`Emit` keeps one more structure per collector.** A new place that reads the path for an access must also take that
  access out of the path-free ones. The differential test below fails if it does not.
- **Tests.**
  - The demo's scopes collected with the rule and without it give the same accesses per execution: order, every field,
    and which pooled list is which object.
  - One case for each way the path reaches the emission: a term bound to two values on two paths, a reference that
    resolves to two places on two paths, a lock of an enumeration on the path, a host of work without a body, and a
    path under fewer guards after one under more.
  - `build/check-test-baseline.ps1` with the eShop snapshot at one worker and at four, and
    `build/record-demo-models.ps1 -Check`. No finding, site pair, occurrence, fingerprint, gap or counter moves,
    `accesses` and `accessExecutions` included; a shift is a defect of the rule.
- **The walk still enumerates every path.** `_visits` is as long as it was, and a visit that is skipped still costs its
  guards. If the walk itself becomes the cost, the bound of TD-090, 16 paths per state in each entry, is the question,
  and changing it changes the guards of accesses.
- **Every execution still emits every access it runs once.** OrchardCore's 25 copies per access are made as before, each
  with its code flow, and the fold drops them. The counters say what that costs once the visits are not repeated.
- **Copies may not fit on `Nop.Web`.** Of its first 42 executions, 8 made about 0.6 million accesses each. If that
  share holds over 17,442 executions, the scope makes about two thousand million copies, and one `AccessExecution` is
  24 bytes. This is a projection from two controllers. The `gaps:` counter replaces it with a number, and if the number
  is of that order, option C2 is the next decision, with a new definition of an access's position.
- **#469 and #470.** #470 is on the path that emits, which this decision leaves, and is done beside it. #469 is
  remeasured afterwards: its lookups are made inside the repeated rows.
