# Cache Detective

Cache Detective statically scans a whole .NET workspace for cache-consistency risks. It loads every
configured solution through MSBuild, follows entry points and calls with Roslyn, joins cache keys to
the tables they depend on, and reports:

- database writes with no reachable invalidation;
- invalidations that reach no cached key;
- invalidation templates that are close to a cached template but do not match it.

Findings carry confidence, source locations, and a linear evidence chain. Cache entries used as
sessions, locks, counters, idempotency records, rate limits, or tokens are classified as stores and
excluded from staleness rules. Configured table budgets suppress findings whose TTL is short enough,
without discarding them from the scan results.

## Run a scan

From a supported host, invoke:

```text
/cache-detective:scan
```

To scan one configured solution instead of the complete workspace:

```text
/cache-detective:scan --solution <name>
```

The first run asks you to confirm the discovered `.sln` and `.slnx` files before it creates the
workspace configuration. Later runs reuse that configuration and its staleness budgets. Reports are
written to `.cache-detective/report-<timestamp>.md`.

## Requirements

- Windows x64
- .NET 10 SDK
- A solution or project that MSBuild can load (`.sln`, `.slnx`, or `.csproj`)

## Read-only boundary

Cache Detective does not edit source code, execute application code, query or mutate a database, or
connect to a cache. The MCP server writes one managed file,
`.cache-detective/workspace.json`, only when configuration is created or changed. The scan skill
separately writes the requested Markdown report under `.cache-detective/`. Build artifacts produced
by MSBuild remain limited to the normal `bin/` and `obj/` directories.

## Recognized cache APIs

- `Microsoft.Extensions.Caching.Memory.IMemoryCache`, including its get, set, create, and remove extensions
- `Microsoft.Extensions.Caching.Distributed.IDistributedCache`, including its extensions
- `Microsoft.Extensions.Caching.Hybrid.HybridCache`, including tag invalidation
- `StackExchange.Redis.IDatabase`, including string/hash operations, deletion, increment, expiration,
  and conditional sets

## Phase 1 boundaries

Phase 1 reads EF Core table access and tracked writes, but it does not parse SQL. Dapper-shaped calls,
ADO.NET commands, EF raw-SQL calls, and stored-procedure commands are recorded as unresolved evidence
instead of being treated as no data access. It does not inspect live database procedures, triggers,
or views; analyze response or output caching; follow external service calls; or verify findings at
runtime. Configuration fields reserved for databases, services, verification, and sensitive data are
preserved for later phases but are not interpreted yet.
