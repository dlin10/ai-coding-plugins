# A construction entry is made once, with the executions of its region

Status: Accepted (#457), on the numbers of the #456/#457 run.

This decision carries #453's bits over the execution numbering from the executions of a constructed region to the
entries those executions take into the walk of [ADR 0017](0017-executions-share-one-walk-graph.md). Its neighbour,
[ADR 0038](0038-an-access-is-recorded-once-with-the-executions-that-run-it-so.md) (#458), folds the same multiplier
one stage later, in access collection.

## Context

The execution model of `Nop.Web` does not reach a terminal status. The diagnostic run of #456/#457
(`docs/runs/5g-E-scale.md`, code 5751f6ed with checkpoints) got further than every run before it:

- The heap took 48 minutes, the propagation of region executions 6 minutes, the entry loop 3 minutes. The walk (`Seed`,
  `Process`) finished in about 2 minutes.
- Then `Builder.Constructions` ran for two hours without an end. All 39 guard samples there are in `Bits.UnionWith`,
  `Bits.Of` and `Bits.Numbers`. The run was stopped by hand; commit peaked at 56.2 GB, under the 58 GB guard.

At the end of the entry loop, before the walk:

| | |
|---|---|
| Entries | 75,629,216 in 9,177 executions |
| Of them, construction entries | 75,619,340 |
| `regionExecutionPairs` | 75,619,344, over 99,187 regions |
| Distinct entries with the execution left out | 96,589 |
| Distinct with the execution and the instance left out | 93,471 |
| Live heap | 13.59 GB after the region executions, 26.89 GB after the entry loop (+13.3 GB) |

The four structures hold the 13.3 GB, by estimate: `_entered` 2.69 GB, `_entryWalks` 3.46 GB, `_pendingEntries`
3.22 GB, the lists of `_entries` 0.90 GB, and up to 3.03 GB of `ExecutionEntry` objects.

A construction entry is a pair of a constructed region and an execution of that region, made into an object. The entry
loop of `ExecutionModel+Builder.Build` takes each construction, then each execution in `RegionExecutions(region)` (#453's
bits, read out one number at a time), then each constructor instance, and calls `Entry(execution, (instance,
Construction, region))`. Each of the 96,589 entries is repeated in about 780 executions, and the constructor instance adds
almost nothing (96,589 against 93,471).

The repetition goes on past the entry loop. `Seed` takes each pending entry on its own, so that the walk keeps one
`TakenEntry` per pair in `_seeds`. `Constructions` then works per taken entry:

- It files every one under its node (`_seedsAt`) and its object (`_objectSeeds`, a list of execution numbers).
- For the executions that enter a construction's nodes from outside it, it visits every taken entry at a node, and for
  each, every object at that node other than its own: `EnterOutside(object, Bits.Of([execution]))`. That is the entries
  times the objects at their nodes, with a new bit array each time.
- For the clean paths, it starts `ArriveClean(node, Bits.Of([execution]))` once per taken entry.

The fixpoints themselves — objects over the walk nodes, and clean executions over the nodes — run over nodes, objects and
bit sets of executions, not over entries. How the two hours split between the per-entry loops and the fixpoints was not
measured. The samples, all inside `Bits` called from `Constructions` itself, fit the per-entry loops.

## Decision

A construction entry is made once per constructor instance and region, with the executions of the region as a bit set:
the same array `_regionExecutions` already holds. An execution's construction entries are the ones whose set contains it.
No entry is added or dropped for any execution. Only the representation changes.

What changes in the builder:

- **`Entry` and the entry loop.** The loop over the region's executions goes away. A construction makes one entry per
  constructor instance and keeps the region's set with it, in a list of its own, in the order the loop makes them today:
  receiver regions first, then by region id, then by constructor instance. Every other entry (root, spawn and tail,
  unknown enumeration, unknown delegate call, type initializer) stays one per execution and goes through `Entry` as now.
- **`_entered`.** Construction entries no longer go into it. They are made only by the entry loop, never by the walk,
  which makes spawn and tail entries alone. So a set keyed by the entry alone, with no execution in the key, keeps them
  apart, and a repeated construction of one region by one instance unions its set into the entry already made.
- **`_entryWalks`.** Its key keeps the tail for the entries that have one: the prefix of a sequential-awaited run names the
  site whose tail its awaited callees join. A construction entry never has a tail, so it needs no walk key: it is seeded
  once, with its whole set.
- **`_pendingEntries`.** Construction entries are all made before the walk, so they are taken in the first round: one item
  per entry with its set, permuted with the rest when a walk order is given.
- **`Seed`.** A taken entry carries a set of executions instead of one number: `Bits.UnionWith(ref node.Executions, set, ref
  node.Arrived)` in place of one `Bits.Add`, and one `TakenEntry` with the set.
- **`Process`.** Unchanged. It already passes executions on as bits and starts spawns, timers and tails per arrived
  execution, which is the work each execution really does.
- **`Constructions`.** It reads `TakenEntry` sets: `_objectSeeds` becomes a set per object, and `ArriveClean` takes the
  entry's set. The loop over the entries taken at a node, for the executions that enter a construction's nodes from
  outside it, is turned around to run per node, not per entry, as part of this decision. Group the node's entries by
  object. Let U be the union of their sets. An object that is no entry's own then gets U, and only the objects that are
  some entry's own get the union of the other groups. That loop then costs the objects at a node, whatever the number of
  entries there. Grouping gives the answer the per-entry loop gave, so this part would hold even without the rest.
- **`ConstructionIndex`.** It reads `objectSeeds` and `seedsAt` as sets of executions instead of lists of numbers.

What readers of the built model see:

- **`ExecutionAnalysis.Entries`** keeps its type and its meaning: each execution's entries, in the canonical order of
  today (by kind, then object, instance and segment). It becomes a view instead of 9,177 lists that hold 75.6 million
  copies. The view lists an execution's own entries and the construction entries whose set contains it. It works the
  list out each time an execution is asked for, and keeps none, since a list kept for every execution is the 75.6
  million again. Its readers do not change: `HappensBefore`'s flows, the access collector, the code flow's "constructs"
  step, `OnePath` and `PathSymbols`. An execution that has only construction entries, such as a lazy construction, is
  one of its keys as before.
- **Pairs, findings, occurrences, gaps.** Every execution has the same entries, walks the same nodes and reaches the same
  objects under construction. Nothing moves. The gates show it: `build/check-test-baseline.ps1` with the eShop snapshot
  and `record-demo-models.ps1 -Check` must show no finding, pair, occurrence or gap moving; a shift is a defect of the
  change.
- **Canonical output and metrics.** `regionExecutionPairs` is the same count. No metric counts entries.
- **SPEC and CONTEXT.** They define no per-execution entry. CONTEXT **Construction** — "belongs to the Execution
  instance that triggers it" — stays true: the construction is still in every execution of its region. ADR 0017's
  "a reader that needs one execution's … reads them out of the sets" now covers entries as well.

Rejected:

- **Shrinking the four structures by a constant factor.** For example, `_entered` and `_entryWalks` as one set, no
  `ExecutionEntry` objects, a smaller queue: 13.3 GB might fall to 4–6 GB. `_seeds` and the per-entry loops of
  `Constructions` would still run 75.6 million times, and that is where the run stopped.
- **Making construction entries on demand.** The walk needs every construction entry before it starts: a node's
  executions are the union of what its entries bring. Making them only when a reader asks for an execution moves the
  75.6 million copies to the readers, and the walk still cannot do without them.
- **One entry set for each component of the DI graph.** Executions that resolve one component would share its entries.
  But a component's executions do not all construct every region in it. Either each execution gains entries it does not
  have today, so instances, accesses and pairs it never runs appear and findings move, or the component is cut by the
  region's executions, which is the bit set this decision keeps. #453 already holds that set exactly, per region.

## Consequences

- At the entry loop, 75.6 million construction entries become 96,589, with sets the builder already holds. The 13.3 GB
  of the four structures falls to the entries themselves and their place in the lists, a few tens of MB. The walk's
  records of taken entries (`_seeds`, `_seedsAt`, `_objectSeeds`) were one per pair too. That is about 3 GB more by
  estimate, not measured, and it falls the same way.
- In `Constructions`, the per-entry loops run over 96,589 entries instead of 75.6 million, and the turned-around loop
  runs per node and object. The object fixpoint and the clean fixpoint stay as they are, over nodes, objects and sets of
  executions. They were never multiplied by the entries, and whether they alone fit on `Nop.Web` is not measured. If the
  run still stops in `Constructions` after this change, the fixpoints are the next cost, and that is a question of
  ADR 0017's per-object analysis, not of the entries.
- The view gives each reader its execution's entries, but the readers still work per execution. Access collection walks
  every execution from its own roughly 8,200 construction entries, and happens-before builds a flow per execution over
  them. That repetition is ADR 0038's question. Since the view keeps no list, an execution asked for twice is worked out
  twice: each time a pass over the construction entries, testing one bit each. A flow that `HappensBefore` keeps for an
  execution keeps its own list, as it does today.
- The engine's source changes. What that means for the model generator's version and the demo's generated models follows
  the pin of #429, as it did for #453 and #455.
