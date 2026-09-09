# An HTTP route is matched on its known tail inside a chosen service

The specification joins an HTTP call to the endpoint that serves it in three levels: an explicit
`services` mapping, a client name matched to a service name, and a route template matched across
the workspace. All three end by comparing the call's path template with the endpoint's route, and
the specification's comparison is a full one — method plus normalised path.

A full comparison fails on any workspace with an API gateway, which is most of them. In
eShopOnContainers the WebMVC client requests `{PurchaseUrl}/c/api/v1/catalog/items`; Envoy rewrites
`/c/` to `/catalog-api/`; the catalog service mounts under a path base and declares
`[Route("api/v1/[controller]")]` with `[HttpGet("items")]`. No normalisation of the client's
template produces the endpoint's route, because the prefix the client sends is not the prefix the
endpoint sees, and the difference lives in a YAML file the scan does not read.

What survives a prefix rewrite is the tail. So, once the first or second level has chosen a
service — the mapping said so, or `ICatalogService` matched `Catalog.API` — the comparison inside
that service is on the **known tail** of the call's path: the segments to the right of the last
unknown fragment, matched against the trailing segments of each endpoint's full route (class
`[Route]`, method attribute, or `MapGet` pattern, parameters treated as equivalent). One candidate
is a `serves` edge at `likely`; several are an `unresolved` of kind `call` naming them all, for the
agent to settle.

Two details of "known tail" follow from the same corpus. The client's template starts with a
placeholder for its configured base address — `{purchaseurl}/c/api/v1/catalog/catalogbrands` —
and that placeholder is a whole address, not a segment, so the tail begins after any leading
placeholder as well as after the last `{?}`. And because the gateway *adds* a prefix, the tail is
usually longer than the route, not shorter: `c/api/{v}/catalog/catalogbrands` against
`api/{v}/catalog/catalogbrands`. The comparison therefore runs from the end over whichever side is
shorter, and either side may have segments left over. A mismatch anywhere in the overlap still
fails — `orders` does not match `orders/{id}` — and a tail made only of placeholders never matches.

At the third level no service has been chosen, so the tail is matched against every endpoint in
the workspace, and there it is too loose to trust: `/items` is an endpoint in half the services of
any shop. The third level therefore keeps the specification's full match, and a tail match there
is not attempted.

The cost is a `likely` that would have been `confirmed` under a full match, and a possible wrong
join when two endpoints of one service share a tail — `GET /orders/{id}` and `GET /drafts/{id}`
both end in `{id}`, which is why the match wants at least one literal segment in the tail and
falls to `unresolved` on a tie. Accepted: the alternative is that `serves` never fires behind a
gateway, and the cross-service half of `depends_on` stays theoretical on every real corpus.

Rejected: reading the gateway configuration — Envoy, YARP, Ocelot and nginx each have their own,
and the scan would become a gateway-config parser. Rejected: asking the user for a path-prefix map
per service — hand-maintained truth, stale the day after the gateway changes.
