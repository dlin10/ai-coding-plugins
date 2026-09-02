---
name: cache-detective
description: Diagnose caching bugs by tracing one cache key through every write, read, and eviction before proposing a fix. Use when a value is stale or wrong until restart, when data leaks between users, tenants, cultures, or environments, when an update does not show up, when a cache hit rate collapses, or when the user mentions cache invalidation, TTL, "устаревшие данные", or "кэш не сбрасывается".
---

# Cache Detective

A caching bug is a lifecycle bug: something wrote an entry, something else read it later, and nothing invalidated it in between. Find the break in that lifecycle before touching any code.

Reply in the language the user used to address you. Keep code identifiers, keys, and configuration names verbatim.

## Establish the symptom first

Pin down, in one sentence each:

1. **Which value** is wrong — the concrete field or response, not "the data".
2. **What it should be**, and what it actually is.
3. **When it recovers** — never, after a TTL, after a restart, after a deploy, on a different instance, for a different user. This single answer usually names the layer.

Do not start reading code until these three are settled. If the user cannot answer the third, ask.

## Identify the layer that served the value

A request can pass through several caches, and only one of them is lying:

| Recovers after… | Look at |
|---|---|
| A restart, and only on one instance | `static` fields, `IMemoryCache`, a singleton holding state |
| A fixed interval | An absolute or sliding expiration, `HybridCache`, an output cache |
| Never, for everyone | A distributed cache entry written without expiration, or a persisted projection |
| A hard refresh in the browser | `Cache-Control`, `[ResponseCache]`, output caching, a CDN |
| A new query shape only | EF Core change tracking, a second-level cache, a compiled query |

Name the layer explicitly before continuing. Half of all wrong diagnoses are the right analysis applied to the wrong cache.

## Trace the key's lifecycle

For the key that carries the suspect value, build the complete picture — writes, reads, evictions:

1. Find the key's construction site, then every use of it. For C#, use the available Roslyn tools for references, callers, and implementations before falling back to text search; a key assembled from a constant prefix hides from a plain grep of the literal.
2. Search the prefix as text too, since keys are routinely rebuilt by hand in another file rather than shared.
3. Record each site as write, read, or eviction, with the file, the line, and the condition that reaches it.

Then answer the four questions that decide the case:

- **Does the key distinguish everything the value depends on?** Tenant, user, culture, role, feature flag, API version, environment. A missing dimension is a data leak, not a staleness bug, and it is the most urgent finding on this list.
- **Does every write path have an invalidation path?** Look for the update, delete, bulk import, and background job that change the source of truth. The one that skips eviction is usually the answer.
- **Does the entry ever expire?** An entry written with no expiration is evicted only under memory pressure, which is why it reproduces in production and not locally.
- **Does the invalidation reach every reader?** An in-process cache evicted on the instance that handled the write leaves every other instance stale. Same for a per-pod cache behind a load balancer.

## Check the less obvious failure modes

When the four questions come up clean, work through these:

- **A failure or a null was cached.** A miss stored as an empty result keeps a transient outage alive long after it ended.
- **A mutable object is shared.** A cached reference mutated by one caller changes what every other caller sees, with no cache operation in sight.
- **Read-modify-write raced.** Two requests read the same entry, both write back, one update disappears. The cache is a store here, not a cache.
- **The serialized shape drifted.** A distributed entry written by the previous version deserializes into defaults, or throws, after a deploy.
- **A sliding expiration never lapses.** A hot key refreshed on every read never expires, so the TTL that "should have fixed it" never fires.
- **Scoped data was captured in a singleton.** A `DbContext`, a request-scoped user, or a `CancellationToken` held past its scope produces stale reads that look like caching.
- **The key is unstable.** Built from `GetHashCode`, object identity, a `DateTime.Now`, or a culture-dependent format, it either never hits or hits the wrong entry.

## Report the diagnosis

State it in this order, and keep it short:

1. The key and the layer.
2. The lifecycle break, as a specific site: this write has no matching eviction, this key omits the tenant, this entry has no expiration.
3. The evidence — file and line for each site — and, separately, whatever you assumed because the code does not prove it.
4. The fix, and the reason it addresses the break rather than the symptom.
5. How to verify: the concrete sequence of actions that must now produce fresh data, plus what to watch if it does not.

Propose one fix, not a menu. Do not implement it unless asked.

## Two rules that prevent bad advice

- **Never recommend removing the cache as the fix.** It hides the bug at a cost the user did not agree to. Removing it as a temporary diagnostic step is fine when you say so.
- **Never state a TTL, an eviction policy, or a cache size from memory.** Read the registration, the options object, and the configuration file. A value that "should be five minutes" is worth nothing next to the one that is actually configured.
