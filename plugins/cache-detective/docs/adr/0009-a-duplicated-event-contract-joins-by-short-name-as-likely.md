# A duplicated event contract joins by short name, as `likely`

The specification identifies an event by the full name of its contract type: a contract shared
through a package gives the publishing solution and the consuming solution one vertex, and that
vertex is the cross-service join. That is the clean case, and it stays `confirmed`.

eShopOnContainers — the first real corpus this phase runs on — does not have the clean case. Each
service carries its own copy of `ProductPriceChangedIntegrationEvent`, in its own namespace, with
its own properties, and the RabbitMQ event bus routes on `GetType().Name`. By full name those are
three unrelated types, and the graph would show a publisher with no consumers and three consumers of
nothing. Every cross-service chain in the corpus would end at the publish site, and the phase's own
rule, `CROSS_SERVICE_GAP`, would never fire on the project it was written for.

So the join is layered, the same way `serves` is, but the vertex is not. An `Event` vertex is
identified by the full name only; a duplicated contract is two vertices. What crosses between them
is a derived pair — an *event hop* — of one `publishes` and one `consumes`: on one vertex the hop is
`confirmed`; between two vertices whose short names agree and whose handlers sit in different
services it is `likely`, with the reason "contract duplicated across services" carried on the hop
— because a short-name match *is* an inference: nothing proves the two classes describe the same
message, only the bus's routing convention suggests it. The stored `consumes` edge is never
rewritten by a hop, and two different types with one short name inside a single service never pair
at all: the routing convention only ever joins services, not a service to itself.

The cost is a real false-join risk: two services that both define an `OrderCreatedEvent` for
different meanings would be joined. The mitigation is the confidence, not a heuristic — the finding
says `likely`, the evidence names both types, and a reader can see the join. Reporting nothing
at all on the commonest way integration events are actually written would cost more.

Rejected: full name only — correct and useless on the corpus. Short name always — throws away the
one case where the join is provable. Requiring the user to map duplicated contracts by hand in the
workspace config — thirty-three events in eShop alone, and the map would say nothing the short
name does not already say.
