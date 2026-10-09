# Executions share one walk graph

The execution model walks the call edges from each execution's entries to learn which instances the
execution runs and, for each object under construction, whether a node runs inside its constructor
chain on some path (`MayIn`) or on every path (`MustIn`). Each execution had a walk of its own, with
a node per execution, instance, segment and tail, and each collected access was recorded once per
execution that ran it. On decompiled CoreLib, scope `four`, 208 executions — 163 type initializers,
the driver's root and 44 unknown calls of a delegate — each reach 17–19 thousand of the 20 thousand
instances. The walks alone made 349 million visits over 1.55 million nodes in 725 s, a node being
walked again 250–280 times as its sets grew one object at a time around the cycles of the call
graph; collecting accesses, ownership and happens-before over every pair of execution and instance
did not finish in 1200 s. A large program whose roots meet in shared services has the same shape.

The decision: executions share one walk graph.

- A node is an instance, a segment and the site whose tail the tails of its awaited callees join.
  It carries the set of executions that reach it, as bits over execution numbers. Each execution
  arrives at a node once: a node passes on only the executions that arrived since it last did, and
  nodes are taken up in reverse postorder. Every pending entry is taken before the walk passes
  executions on, so executions that reach a node together arrive there in one visit; the entries the
  walk itself makes — of spawns, timers, tails and unknown calls — are taken in the next round. A
  child execution of a spawn, a timer or an async tail is still one per parent: its parents are the
  set at its site.
- `MayIn` and `MustIn` are worked out per object, not per pair of execution and node. An object is
  under construction in execution `e` at node `n` on some path when `e` reaches an edge or entry
  that starts its construction and `n` is reachable from it, and on every path when no path from an
  entry of `e` to `n` avoids those edges and entries. Each object's two execution sets are computed
  over the nodes its construction reaches that reach a node with an access on the object, since every
  path to such a node lies among them. A constructor chain is kept per instance, as the objects whose
  construction reaches one of its nodes, and publication asks each instance only about the objects
  it stores, returns or hands over.
- A collected access is recorded once — instance, operation, region, construction-local — with the
  set of executions that run it so. A reader that needs one execution's visits, steps or accesses
  reads them out of the sets.

Both questions are reachability in one graph, so the answers are exactly those of a walk per
execution.

Rejected: keeping a walk per execution and only ordering it so that a node is walked about once
instead of 250–280 times. The nodes and the collected accesses would still be repeated once per
execution. Rejected: running a library type initializer in the execution
that triggers it. It removes the 163 walks, but it reverses ADR 0006 and changes findings.

The consequences to hold. Executions, ownership and its evidence, published objects, happens-before,
call path prefixes and collected accesses are what they were, so no finding moves. The cost of the
execution model grows with the size of the graph, and the number of executions enters it only as
the width of a bit set. `ExecutionAnalysis.Accesses` carries execution sets, and a reader that asked
about one execution asks whether it is in the set. The accesses stage still walks each execution on
its own, with its paths, locks and guards; on a large closure it is the next cost.
