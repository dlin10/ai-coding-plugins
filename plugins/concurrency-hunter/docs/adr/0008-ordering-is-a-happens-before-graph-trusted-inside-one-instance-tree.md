# Ordering is a happens-before graph, trusted inside one instance tree

Until phase 3 any two different executions may overlap. Spawn sites, joins, continuations, timers
and the host's startup add order: a parent's write after `await t` cannot race with `t`, a
continuation starts after its antecedent, and nothing a constructor does at startup overlaps a root.

Ordering is one directed graph over execution events: the start and end of every execution, and the
points inside it where an access, a spawn or a join happens. Inside one execution, x precedes y when
no control-flow path leads from y back to x; a loop therefore orders nothing it repeats. Between
executions, the edges are: a spawn site to the start of what it spawns; the end of a spawned
execution to a join that must run on every path, exceptional ones included, and waits until that
execution is complete for a handle proven to be its own; the end of a `Parallel` loop's iterations to
the loop call's return; the end of an antecedent to the start of its continuation; the end of each
`WhenAll` argument to the join of the `WhenAll`; the end of a timer's callbacks to a proven
`DisposeAsync` or `Dispose(WaitHandle)` wait; the end of startup to the start of everything startup
did not start itself or through its descendants. A spawned execution's start and end bound its own
points, never the work it spawns in turn. Two accesses are ordered when a path joins them in either
direction.

A path is trusted only when every edge on it holds for every instance it connects, which is what an
instance tree guarantees. A spawn edge holds for every instance of what it starts when the spawning
execution runs once: a root that runs at most once and never overlaps itself, or an execution
spawned from such a root by a site that is not inside a loop. A join edge holds for every instance
when the join waits for all of them: a proven handle of a spawn that is not repeated, a `Parallel`
loop's return, a proven wait for a timer created once. Otherwise another instance of the same code
overlaps the pair and the pair stays. An execution that overlaps itself, a periodic timer callback or
a `Parallel` body, still has its self-pairs; the tree only orders it against the others. Startup is
the exception: it runs once, before every instance of every root, so its edges hold for all of them.

Rejected: ordering only between a spawn and its chain of parents, with siblings ordered only through
joins in a common parent. Smaller, and it covers the demo's cases, but every new edge kind (timers,
continuations, startup) would need its own special case, and orders that pass through two joins
would be lost. Rejected: trusting a path regardless of instances. It would drop real races between
one HTTP request's spawned work and another request, which is why the demo keeps spawn cases inside a
single `BackgroundService`.

The consequence to hold: happens-before only removes pairs. New pairs come from new executions
(spawns, timer callbacks, gRPC methods), never from the graph, and an edge whose handle identity,
join or wait is not proven is not added.
