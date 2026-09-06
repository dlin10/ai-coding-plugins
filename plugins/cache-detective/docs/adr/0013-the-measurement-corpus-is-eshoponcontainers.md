# The heuristics are measured on eShopOnContainers, not on nopCommerce and Orchard

Two open questions have been deferred to the same pair of runs since phase 1: the accuracy of the
`role` heuristic (ADR 0005) and the false-positive rate that would let the database reading of orphan
invalidation ship (ADR 0003). Both name nopCommerce and Orchard, following the specification, which
assigned those repositories before anyone had indexed them.

Indexing them establishes that neither can answer either question.

nopCommerce has no EF Core at all — data access is linq2db and FluentMigrator — so the EF write
heuristic has nothing to fire on. Its cache keys are not strings at the call site either: an
`IStaticCacheManager` takes a `CacheKey` object that a key service builds from a format string, and
the key folder reads string expressions. Very few keys are classified, so there is very little `role`
to measure.

Orchard stores everything in a YesSql document table plus index tables, which makes table-level joining
close to meaningless; its cache surface is a signal and tag machinery the built-in recognizers do not
describe.

eShopOnAbp was prepared as the EF Core bench and turns out to be the clearest case of all. Measured on
2026-09-05, a full index produced 37 vertices, 30 edges and zero findings, with all fifteen unresolved
entries of kind `call`: `IRepository<Product, Guid>.GetAsync`, `IObjectMapper.Map`,
`IAbpApplication.Initialize`. ABP's repositories are implemented in its NuGet packages, and the call
graph resolves an interface call only to implementations inside the workspace. No ABP application will
ever yield an EF write, and that is the documented boundary working, not a defect: the graph says
"fifteen calls I could not follow" rather than "no writes".

Both heuristics are therefore measured on **eShopOnContainers**, which calls EF Core directly from code
the indexer reads, holds a genuine `store` in the Redis-backed basket alongside genuine caches, and
already carries the eval harness and workspace configuration built in phase 3.

nopCommerce and Orchard keep the role the specification's own bench table gives them, which was never
heuristic accuracy: load — how long a large monolith takes to index — and coverage, the share of cache
and SQL call sites that reach the graph rather than `unresolved`, measured before and after the agent's
annotations. That second number is the honest form of the coverage target, because measured before
annotation it reports only how long the built-in recognizer list is.

This amends the deferral in ADR 0005, which is discharged here, and re-points the condition in ADR 0003
at a corpus that can meet it; the orphan-invalidation reading itself stays deferred, since measuring a
false-positive rate for it is not part of this phase.

Rejected: adding recognizers for `IStaticCacheManager` and Orchard's dynamic cache so the benches
produce numbers — those repositories were chosen precisely to test what happens against an unfamiliar
cache API, and teaching the tool their APIs would measure the fix instead of the problem. Rejected:
measuring on the demo stand, whose bugs we planted ourselves, which tells us nothing about real code.

## The role heuristic stays unmeasured on this corpus too

Written above: eShopOnContainers "holds a genuine `store` in the Redis-backed basket alongside genuine
caches", so both heuristics could be measured there. Half of that survived contact with the corpus.

The EF write heuristic measured cleanly: on the clean checkout at `7e5ae7b5` the sampler found ten
heuristic write sites against a target of twenty, every one of them a `DbSet` add, update or remove or
a tracked-entity assignment reaching `SaveChangesAsync`, and all ten were judged true writes by reading
the code. Accuracy 1.0, false-positive rate 0.0, on a sample the size the corpus allowed.

The role heuristic did not measure at all. The whole recognised cache surface of eShopOnContainers is
one `IDatabase` field in `RedisBasketRepository`, and its three call sites take the key from a method
parameter, so no template folds, each site becomes an `unresolved` of kind `key`, and a key with no
template carries no role. The basket *is* a store in the source, exactly as the ADR says; the classifier
never sees it, because the key it would classify does not exist until run time. Zero candidates against
a target of thirty, and the sample file records that as its reason rather than carrying invented rows.

So the specification's open question about `role` accuracy remains open after phase 4, now for a
reason that is understood rather than assumed: every prepared corpus fails it differently. nopCommerce
builds keys as objects, Orchard's cache is a signal-and-tag machinery the recognizers do not describe,
ABP hides everything behind package repositories, and eShopOnContainers keys its one cache from
parameters. A corpus that measures `role` needs string-templated keys at the call site on a codebase
nobody on this project wrote, and none of the four has them.

Rejected: applying the planted patch from the phase-3 eval to manufacture candidates — the patch is
the project's own code, so it would measure the classifier against keys written to be classified.
Rejected: relaxing the sampler to admit unresolved keys — a key without a template has no role to be
right or wrong about.
