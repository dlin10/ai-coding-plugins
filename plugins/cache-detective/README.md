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

Cache Detective does not edit source code and does not execute application code. It **never** modifies
a database — no DDL, no DML, no `EXEC` of any procedure of yours — and it never writes to a cache.
Connections are opened with `ApplicationIntent=ReadOnly` unless your connection string already states
an intent.

What it reads depends on which of the three parts is running, and the difference matters:

- **The code indexer** reads source and build metadata. Nothing else. A scan is complete when every C#
  and VB project the solution declares was opened; a solution's other entries — `.dcproj`, `.sqlproj`,
  `.vcxproj` and the like — are listed under `skippedProjects` with their extension as the reason and do
  not make the scan partial, because MSBuild does not open them and they hold no code to index.
- **The catalogue indexer**, when you configure a database, issues only `SELECT`s over `sys.` catalogue
  views and one dynamic management function. It reads no row of any table of yours. Every statement it
  issues passes through a single method, and a test drives the indexer against a fake connection and
  asserts that each statement names nothing outside `sys.`.
- **Runtime verification**, and only when you turn it on, reads rows of the tables a finding depends on
  and values out of your cache. That is the point of it: a comparison of a cached value against its
  source cannot be made without reading both. **Rows are read only from the tables you name in
  `verify.tables`**; for every other dependent table it reads only when that table was last written,
  from `sys.dm_db_index_usage_stats`, which holds no row data at all. The stores it reads are the ones
  you name in `verify.stores`, it writes to neither cache nor database, and neither the values nor the
  real keys leave the server — see
  [Verifying findings against the running system](#verifying-findings-against-the-running-system).

The MCP server writes one managed file, `.cache-detective/workspace.json`, only when configuration is
created or changed. The scan skill separately writes the requested Markdown report under
`.cache-detective/`. Build artifacts produced by MSBuild remain limited to the normal `bin/` and
`obj/` directories.

## Verifying findings against the running system

Verification looks at the cache and the database as they are right now and says what that was worth. It
is off unless you configure it, and it changes nothing about a finding — see
[Why verification never suppresses a finding](#why-verification-never-suppresses-a-finding).

### Turning it on

Add a `verify` section to `.cache-detective/workspace.json` and pass `--verify` to the scan, or set
`"auto": true` and let the skill decide per run — `auto` is the workspace's consent, not a trigger:

```json
{
  "version": 1,
  "solutions": ["src/Shop.slnx"],
  "databases": [{ "name": "shop", "connection": "env:CD_SHOP_CONN" }],
  "verify": {
    "redis": "env:CD_VERIFY_REDIS",
    "database": "env:CD_VERIFY_DB",
    "auto": false,
    "stores": ["redis", "distributed"],
    "keyPrefix": "shop:",
    "clockMarginSeconds": 60,
    "tables": {
      "dbo.Products": { "key": "Id", "from": "id" }
    }
  }
}
```

Both connections are `env:` references and never the strings themselves, because this file is
committed. A field this schema does not know is refused rather than ignored, on the grounds that a
setting you believe is doing something should be.

- **`stores`** names which stores verification may read; it defaults to `redis` and `distributed`. A key
  held in process memory belongs to a process this tool is not inside, so there is nothing for it to
  read and an `IMemoryCache` key is never verified.
- **`keyPrefix`** is the prefix your cache client adds to every key before it reaches Redis. Without it
  a real key does not match the template the scan folded, and the reading is discarded as a mismatch
  rather than reported.
- **`clockMarginSeconds`** is how far the cache's clock and the database's clock may disagree before
  comparing a moment on one with a moment on the other means nothing. It defaults to 60.
- **`tables`** is keyed by `schema.name`, and each entry needs both halves: `key` names the column that
  identifies a row and `from` names the key placeholder its value comes from. Either alone is refused,
  because one says which row without saying which value to look for and the other the reverse.

**Without a `verify.tables` entry carrying both `key` and `from`, refutation is unreachable.** There is
then no way to fetch the row a cached value was built from, so the strongest available observation is
`possible` or `not_verifiable`, however healthy the entry looks.

### A connection string that disables `INFO` makes verification impossible

This one is a refusal, not a setting. With `INFO` unavailable the Redis client's own auto-configuration
writes a probe key into database 0 — measured on StackExchange.Redis 2.8.31, where `AutoConfigureAsync`
sends `SET` with `PX 1`, and the test it makes is on the command map, so `AllowAdmin`,
`ConfigCheckSeconds`, `TieBreaker` and `AbortOnConnectFail` are all powerless to stop it. A write of one
millisecond into somebody else's database is still a write, and this tool does not make them. So a
connection string with `INFO` disabled is refused and verification reports `not_verifiable` instead. A
*renamed* `INFO` keeps the command available and is accepted.

Otherwise verification sends only `SCAN`, `TYPE`, `GET`, `HGET`, `TTL` and `OBJECT IDLETIME`, through a
single method that a test inspects, exactly as the catalogue indexer does. The keyspace is walked with a
raw `SCAN` and never `KEYS`, under a budget of 50 iterations or 5 seconds. **When that budget runs out
the tool stops waiting, but it does not cancel the command already sent** — the Redis client offers
neither a cancellation token nor a per-command timeout on a raw command, so the request stays on the
wire and its reply is discarded.

### The second login, and what it needs

Verification reads rows, so it needs a second SQL Server login with rights the indexing login is
documented not to have. Grant it only over the tables you named in `verify.tables`:

```sql
CREATE LOGIN [cache_detective_verify] WITH PASSWORD = '<a strong password>';
USE [Shop];
CREATE USER [cache_detective_verify] FOR LOGIN [cache_detective_verify];

-- Read the catalogue, as the indexing login does.
GRANT VIEW DEFINITION ON DATABASE::[Shop] TO [cache_detective_verify];

-- Read sys.dm_db_index_usage_stats, for when a table was last written.
-- Before SQL Server 2022:
GRANT VIEW SERVER STATE TO [cache_detective_verify];
-- SQL Server 2022 and later, the narrower grant that replaced it:
GRANT VIEW SERVER PERFORMANCE STATE TO [cache_detective_verify];

-- Read rows, and only from the tables verify.tables names.
GRANT SELECT ON OBJECT::dbo.Products TO [cache_detective_verify];
```

> **This minimal grant is documented but not verified.** Unlike the indexing login, whose rights the
> integration tests create and prove by asserting a refused `SELECT`, no test runs verification under a
> login holding exactly the grants above. A verification run is expected to be administrative, so the
> tested path is an administrative connection. If you choose to restrict the rights, check them
> yourself: a missing grant surfaces as a `not_verifiable` reason naming the refusal, not as a silent
> wrong answer, but that is the only guarantee offered here.

### What the three observations mean

- **`refuted`** — every field the scan could compare between the cached value and the row it was built
  from is equal, across an exhaustive sample of keys that existed throughout the traversal, **and** the
  finding's own table was not written after the entry was created. The entry agrees with its source *for
  those fields, right now*. It does **not** mean the missing invalidation is not missing: the next write
  still exposes it.
- **`possible`** — at least one comparable field differs, or the finding's table was written after the
  entry was. Staleness is possible, and this is the observation worth triaging first. A write to that
  table after the entry withholds refutation even when every compared field agrees — the fields compared
  are only some of what the value was built from. `basis` says which of the two the answer rests on:
  `field_difference`, `age`, or `both`.
- **`not_verifiable`** — the tool could not tell, and the reason says why. The reasons differ in kind:
  no `verify` section, an unreachable cache, a refused permission, a key whose role is not `cache`, a
  store outside `verify.stores`, a key with no table dependency, a traversal that stopped short, a
  sample trimmed to the limit, an ambiguous match between key and template, or a field type the
  comparison does not cover.

A result may also be marked partial: something failed part of the way through, and the readings taken
before it are still reported. Refutation rests on field agreement and on nothing else — that statement
needs no clock, no TTL and no assumption about who called `EXPIRE`.

### Why verification never suppresses a finding

An observation is added beside a finding and never replaces it. Verification does not change a finding's
confidence and does not decide whether it is reported; see `docs/adr/0012`. The reason is that the two
answer different questions. A finding says the code has no path from a write to an invalidation. A
`refuted` observation says that at this moment the fields it could compare happen to agree — which is a
statement about the current contents of one cache, not about the missing code path. The next write still
exposes it. A tool that dropped the finding on that evidence would be hiding a real defect behind a
lucky sample.

### What is never shown

You never see a real cache key or a cached value. A key reaches the report as its template and a short
hash; a field reaches it as its name and whether it differed. A field whose name matches a sensitive
mask is marked redacted. This is the reason verification lives inside the server rather than behind
third-party MCP servers for Redis and SQL Server: on the far side of a tool boundary the value is
already in the agent's context before anything can redact it, because fetching it *is* the tool call.
See `docs/adr/0011`.

## Recognized cache APIs

- `Microsoft.Extensions.Caching.Memory.IMemoryCache`, including its get, set, create, and remove extensions
- `Microsoft.Extensions.Caching.Distributed.IDistributedCache`, including its extensions
- `Microsoft.Extensions.Caching.Hybrid.HybridCache`, including tag invalidation
- `StackExchange.Redis.IDatabase`, including string/hash operations, deletion, increment, expiration,
  and conditional sets

## Events and services

Cache Detective recognizes MediatR, MassTransit, Rebus, and NServiceBus publishers and consumers. Add
other buses with an `events` entry in `.cache-detective/workspace.json`, naming its publisher type,
publish methods, event argument, consumer interface, and handler method. registration with the bus is not checked.

External HTTP and gRPC reads can join a service endpoint through `services`, a mapping from client name
to project or solution. The join first uses an explicit `services` mapping, then a normalized client
name, then an unambiguous route or gRPC contract. Gateway configuration files are not read, so record
an explicit mapping or annotation when they are the only destination evidence.

Use `annotate` to record a proven key, SQL, call, event, role, or API fact without changing source.
The scan workflow delegates bounded unresolved review to the `static-analyst` subagent; it reports
each applied annotation and each safe refusal. The eShopOnContainers eval is documented in
[skills/scan/evals/eshop/README.md](skills/scan/evals/eshop/README.md).

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
response or output caching, and does not follow a call out of the workspace. It reports one reading of
orphan invalidation — a `Remove` of a key nothing caches — and deliberately not the other, because it
cannot yet see every writer of a table. The `services`, `verify` and `sensitive` configuration sections
are all interpreted now; a field none of their schemas knows is refused rather than ignored.

Three limits remain where a fold stops short of a value it could in principle name, and each is a
different reason rather than three faces of one. Multi-valued SQL is out of scope, and out of scope means
the branchy *value* and not the statement holding it: a fragment that stands for several collapses to one
neutral parameter where it arises, and the statement around it keeps every literal it had, so
`UPDATE dbo.Products SET Price = {branchy}` still resolves `dbo.Products` and still records its write.
What is not done is running the parser once per value and uniting the results, so a query whose *shape*
differs across a branch — a table name chosen by a condition, say — is read as the one shape the fold
could prove and the alternatives are not enumerated. A local that builds itself with `+=` is not
read, because a compound assignment is not a value the fold can see; the site folds to the local's
initialiser and is marked collapsed, so the template it names can never be mistaken for the one value
the run produces. And Orchard Core's `CacheContext` is not folded — see the bullet below.

### What analysis will not reduce

- **A cache key built as an object is folded only where the object carries its own template.** The
  recognizer may describe one: the type, the constructor argument the template literal comes from, and
  the factories that substitute arguments into its positional holes (`docs/adr/0016`). That is
  nopCommerce's `Nop.Core.Caching.CacheKey`, and describing it takes the corpus from 3 cache operations
  against 121 unresolved keys to 116 against 28, coverage 0.023 to 0.784. Orchard Core's `CacheContext`
  is not that shape and is not folded: it is a constructor id plus a fluent chain of `AddContext` calls
  composed at run time, which needs a second kind of description rather than a second entry of this one,
  and in v3.0.1 the cache id is usually a run-time string from a Razor tag-helper attribute or a Liquid
  filter argument, so even a perfect reader of it would fold a handful of sites. Orchard's coverage is
  reported with that as its measured reason.
- **A framework that hides data access behind interfaces implemented in its own packages yields a graph
  with no writes at all, and you can see that it did from the unresolved entries of kind call.** The
  call graph resolves an interface call only to implementations inside the workspace, so an ABP
  application produces "fifteen calls I could not follow" rather than "no writes" — the distinction the
  `unresolved` list exists to preserve.

### What runtime verification will not settle

- **Comparing values requires a `verify.tables` entry that gives both halves:**
  the key column and its source placeholder. Without both there is no way to fetch the row a cached
  value was built from, so no reading can refute anything.
- **The age of an entry never refutes a finding, and cannot be made to.** `EXPIRE` may extend a
  deadline long after the value was written, and a handler may read a row, wait while the table
  changes, and only then write the value it already held. A table written *after* an entry is evidence
  that staleness is possible; a table written *before* it settles nothing.
- **Refutation is impossible for a key whose value depends on another cache key** rather than on a
  table, because the row that would be compared against does not exist.
- **A Redis cluster is not supported, and neither is a connection naming more than one endpoint.**
  Verification reads a single node and a single database; anything else reports `not_verifiable` and
  says which topology it found.
- **A cache entry of an unsupported entry type is not read.** `TYPE` decides how an entry is fetched, a
  string with `GET` and a hash with `HGET`; a list, a set or a stream is reported as not verifiable
  rather than guessed at.
- **A value passed as a parameter of a verification SQL query is visible to anyone**
  subscribed to the SqlClient diagnostic source in the same process. The key's placeholder value
  travels to SQL Server as a command parameter, and `Microsoft.Data.SqlClient` publishes command
  events, parameters included, to any in-process listener. Redaction in the report does not reach that
  channel.
