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
- Optionally, SQL Server and a read-only login, to read the database catalogue as well as the code

## Reading the database

A scan works without a database: it then reports what the code alone can prove, and says so wherever
a chain runs into a stored procedure it knows nothing about. Configuring one lets it follow writes
made by procedures and triggers, and reads made through views. Add the database to
`.cache-detective/workspace.json`:

```json
{
  "version": 1,
  "solutions": ["src/Shop.slnx"],
  "databases": [{ "name": "shop", "connection": "env:CD_SHOP_CONN" }]
}
```

The connection is a reference to an environment variable, never a connection string: this file is
committed. One database per workspace — a table is identified by `schema.name`, so two databases
would collapse two different `dbo.Products` into one vertex and every chain through it would be
fiction. A configuration naming two is refused, saying why.

### The login it needs, and nothing more

Create a login that can read the catalogue and cannot read your data:

```sql
CREATE LOGIN [cache_detective] WITH PASSWORD = '<a strong password>';
USE [Shop];
CREATE USER [cache_detective] FOR LOGIN [cache_detective];
GRANT VIEW DEFINITION ON DATABASE::[Shop] TO [cache_detective];
GRANT SELECT ON sys.sql_expression_dependencies TO [cache_detective];
```

That is the whole grant, and the integration tests run under exactly it — they create a login with
these rights, index under it, and first prove the login is refused a `SELECT` on a user table, so
"read-only" is enforced by the server rather than asserted by us.

`VIEW DEFINITION` has to be granted **on the database**, not on a schema. Granted on a schema, the
login reads zero rows from `sys.sql_expression_dependencies` with no error anywhere, and every
procedure-to-procedure call vanishes silently. Cache Detective checks the permission and records the
gap rather than reporting no calls, but the fix is the grant above.

## Read-only boundary

Cache Detective does not edit source code, execute application code, or connect to a cache. Against
a database it issues only `SELECT`s over `sys.` catalogue views and one dynamic management function:
it **never** modifies a database — no DDL, no DML, no `EXEC` of any procedure of yours — and it never
reads a row of your data. Connections are opened with `ApplicationIntent=ReadOnly` unless your
connection string already states an intent. Every statement the indexer issues passes through a
single method, and a test drives the indexer against a fake connection and asserts that each
statement names nothing outside `sys.`.

The MCP server writes one managed file, `.cache-detective/workspace.json`, only when configuration is
created or changed. The scan skill separately writes the requested Markdown report under
`.cache-detective/`. Build artifacts produced by MSBuild remain limited to the normal `bin/` and
`obj/` directories.

## Recognized cache APIs

- `Microsoft.Extensions.Caching.Memory.IMemoryCache`, including its get, set, create, and remove extensions
- `Microsoft.Extensions.Caching.Distributed.IDistributedCache`, including its extensions
- `Microsoft.Extensions.Caching.Hybrid.HybridCache`, including tag invalidation
- `StackExchange.Redis.IDatabase`, including string/hash operations, deletion, increment, expiration,
  and conditional sets

## What it reads

Alongside EF Core table access and tracked writes, raw SQL is now parsed. Dapper calls, ADO.NET
commands and EF raw-SQL calls are folded to as much text as the compilation can prove, and the
T-SQL grammar decides what that text touches. `"SELECT * FROM dbo.Products WHERE Id = " + id` yields
a read of `dbo.Products`; `$"SELECT * FROM {table}"` does not, and is recorded as unresolved with the
position that defeated it. What decides is where an unknown fragment lands in the parse tree, not
whether the parser complains — `SELECT * FROM @p` is legal T-SQL.

With a database configured, the catalogue is read too: which tables each stored procedure and trigger
reads and writes, which procedures call which, what each view reads, and which triggers hang on which
table for which events. That closes the chains code alone cannot: a handler calling a procedure that
writes a table, or writing a table whose trigger writes another. A finding's subject is always the
handler at the head of such a chain, because that is who can fix it — a procedure no indexed code
calls produces no finding at all.

## Boundaries

Dynamic SQL built at run time (`sp_executesql`, `EXEC(@sql)`) is recorded as unresolved, not followed.
Column-level dependencies are not modelled: a key depends on tables. Cache Detective does not analyze
response or output caching, follow external service calls, or verify findings at runtime. It reports
one reading of orphan invalidation — a `Remove` of a key nothing caches — and deliberately not the
other, because it cannot yet see every writer of a table. Configuration fields reserved for services,
verification, and sensitive data are preserved for later phases but are not interpreted yet.
