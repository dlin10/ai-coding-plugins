# A publish is attributed to the caller that names the type

The `publishes` edge ran from the method whose body physically contains the `Publish` call. Where the
event is constructed at that call, that method is also the one that decided which event to publish, and
the edge is right. Where the call sits in a shared helper, as in eShopOnContainers'
`CatalogIntegrationEventService.PublishThroughEventBusAsync` whose parameter is the event, it is wrong
in a way that spreads: the helper is one vertex, every caller's event type lands on it, and a finding
headed by any single caller shows every type any caller ever passes.

The information needed to fix it was already being computed and thrown away. Resolving the event type
from a parameter already walks to the callers through `SymbolFinder.FindCallersAsync`, up to five hops,
and folds each caller's argument. It then merged every result into one flat set keyed by type name and
discarded which caller produced which: the caller symbol was read only to continue the recursion and
never associated with the type it yielded. So the change is to keep the pair. The edge runs from the
caller that named the type, carrying the type it named.

This does not reopen the traversal question ADR 0014 settled. That decision is about the call-graph
walk, one frontier, each method expanded exactly once, the expanded set a function of the solution
alone, and the caller lookup is not part of it. It is a symbol query answered from the compilation, run
at one publish site, with the same answer whatever order the walk reached that site in. Each method is
still expanded once, and the helper still records its `Calls` edge, so the chain from a caller through
the helper is unbroken. What the helper no longer carries is a `publishes` edge that was never its own.

A caller found this way may have no `Handler` vertex, because vertices exist only for methods the walk
reached from an entry point. That case records an unresolved row of kind `event`, the same one already
recorded when no type resolves at all. The two alternatives were both worse. Creating a handler vertex
for the caller puts a head into the graph that no chain reaches, and a finding whose head is
unreachable is addressed to nobody, against the rule that a finding belongs to the handler at the head
of the chain. Climbing the `calls` edges to the nearest reachable ancestor requires a backwards walk
that does not exist and silently attributes an event to a method that does not publish it. An
unresolved row says what is true: the publish was found, the publisher was not, and here is the site.

The counts move down, and that is the point rather than a side effect. On eShopOnContainers the number
of `publishes` edges falls, because the union that inflated the helper is gone and because a publisher
the walk never reached is now a row instead of an edge. The behaviour snapshots are re-approved with
the new numbers, deliberately: a snapshot re-approved without saying why is how a regression enters,
and this one is a correction that the snapshot exists to record.

Rejected: keeping the helper's edge alongside the caller's. The helper's edge is exactly the union that
made the finding unreadable, so keeping it means every finding still shows every type. Rejected too:
attributing to the head of the chain rather than to the immediate caller. Reachability already walks
`calls` transitively, so the head is reached anyway, and attributing there would lose the one thing the
caller knows and the head does not, which of the helper's many events this path actually publishes.

## The counts did not move, and that is not the measure

Written above: "The counts move down, and that is the point rather than a side effect. On
eShopOnContainers the number of `publishes` edges falls." Measured on 2026-09-08 against the pinned
checkout, it does not. Fourteen before, fourteen after, with unresolved-event rows at one both ways.

The prediction was wrong for a reason worth keeping. The old flat set was keyed by the event's full
type name alone, so it lost information only where two callers of one helper named the *same* type —
and on eShopOnContainers no two do. What the union destroyed there was not the number of edges but
which handler each one hung on, and a count cannot see that. The defect was always that a finding
headed by one caller showed every caller's events; the count was never the thing it damaged.

What the change did is visible only by looking at where an edge starts. All fourteen now start at the
handler or controller that constructed the event — `CatalogController.UpdateProductAsync`,
`BasketController.CheckoutAsync`, the Ordering domain-event handlers, `GracePeriodManagerService` —
and none at an integration-event service helper; two handlers publish two types each, which the old
shape could not express at all. A corpus where two callers pass one type would show the count fall,
and none of the three prepared corpora is that corpus.

The lesson generalises past this ADR: an aggregate is the right evidence only when the defect changes
the aggregate. Naming the count to watch before knowing what the fix moves is how a real improvement
gets reported as a no-op — and, worse, how a plan's gate ends up asserting the wrong number.
