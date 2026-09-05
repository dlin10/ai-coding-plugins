# Cache Detective — domain language

The vocabulary the code, the MCP tool schemas and the report must use. Written during the interview
that scoped phase 1; extended as later phases land.

| Term | Meaning |
|---|---|
| **Workspace** | Everything one scan covers: the solutions, the database, the service mapping and the event recognizers named in `.cache-detective/workspace.json`. One graph spans the whole workspace, never one graph per solution. |
| **Code indexer** | The Roslyn half: walks solutions, emits cache keys, handlers, tables, calls. |
| **DB indexer** | The catalogue half: reads a live SQL Server's `sys.*` views for procedures, triggers and views. Not in phase 1. |
| **Sink** | A cache call site — a `Get`, `Set`, `Remove` on a caching API. |
| **Recognizer** | The declarative description of one caching library: its type, its methods, each method's semantic, which argument carries the key, which carries the TTL. Adding a library means adding a recognizer, never adding a branch. |
| **Semantic** | What a sink does to the entry: `get`, `set`, `remove`, `remove_by_tag`, `remove_by_prefix`, `increment`, `expire`, `lock`. The word for the operation's kind, never for meaning-of-the-value. A recognizer method may also name one argument and the constant that turns the call into a conditional set — the only shipped case is `StringSet` with `When.NotExists`. |
| **Template** | A cache key with its variable parts folded to names: `product:{id}`. Known substitutions become `{name}`, unknown ones `{?}`. A value that depends on a branch — a local assigned differently in two places — is unknown, `{?}`, never its first assignment. Two sites that build the same template *in the same store* are the same `CacheKey` vertex; the same template in two stores is two vertices, because an invalidation in one store does not reach the other. When sites merge, TTL merges as the longest with "no TTL" meaning infinity. Tags merge twice, because a tag means different things to the two questions asked of it: the intersection across sites is what a tag removal must match to *cover* the key, since entries written by an untagged site are never evicted by it, and the union is what keeps a tag removal from being called dead. A merge must never grant a suppression that one site alone would not. |
| **Role** | `cache` — the value is derived from sources and can go stale. `store` — the cache *is* the storage (sessions, locks, idempotency, rate limits, tokens); there is no source of truth, so staleness is meaningless. Detection rules apply only to `role: cache`. |
| **Handler** | A method reached from an entry point, identified as `handler:<Solution>/<Type>.<Method>`. Entry points are controllers, minimal API endpoints, event consumers, and anything recognised by interface or base class. A handler also carries the **service** it belongs to. |
| **Service** | The unit a chain crosses when it becomes cross-service: the project (assembly) a handler is compiled in, not the solution. A solution may hold one service or twenty — eShopOnContainers holds every service in one `.sln` — so the report names services by project, and "invalidation: not found in Catalog.API, Basket.API" lists projects. An explicit `services` mapping in the workspace config may name a solution or a project. |
| **Event** | A message published by one handler and consumed by others, the second way a chain crosses a service boundary (the first is an HTTP or gRPC call). Its identity is the contract type's full name, and nothing else: two solutions sharing a contract package produce one vertex; a contract duplicated per service under different namespaces — as eShopOnContainers does, and as its RabbitMQ bus routes by short type name — produces one vertex per full name. What joins those vertices is the **event hop**, a derived pair of one `publishes` and one `consumes`: `confirmed` when both sit on the same vertex, `likely` with the reason that the contract is duplicated when the vertices differ but the short names agree and the two handlers are in different services. Two different types with one short name inside one service never pair. The stored `consumes` edge is never altered by a hop. See `docs/adr/0009`. |
| **Event recognizer** | The declarative description of one event bus: the publishing type and method and where the event's type comes from (the argument's static type, or a type argument), and the consumer interface's name, arity and handling method. Built in for MediatR, MassTransit, Rebus and NServiceBus; a workspace declares its own bus — eShop's `IEventBus` / `IIntegrationEventHandler<T>` — in the `events` section of its config, or the agent declares one through an `annotate` of kind `event_api`. A recognizer may name a publisher with no consumer side: an outbox whose *add* is the moment a service commits to publishing — eShop's Ordering service publishes only that way — while the consumer side comes from another recognizer. Implementing the consumer interface is what makes a consumer; registration with the bus is not checked, and the README says so. |
| **publishes / consumes** | `Handler → Event` and `Event → Handler`. The event's type is what the published expression *can be*, not what it is declared as: a construction names its type, a conditional names both branches, a local names every value assigned to it, a parameter is followed to the callers up to five hops, and a property or return value whose declared type other events derive from names nothing. When that yields nothing, or when an event has no consumer anywhere in the workspace, the event is `unresolved` with kind `event`. |
| **ExternalSource** | Something a handler reads that is not a table and not a key: an HTTP call (`http:GET /products/{id}`) or a gRPC call (`grpc:Catalog.GetItems`). The URL is folded like a cache key. Kinds `config` and `clock` are out of scope until a rule needs them. |
| **serves** | `ExternalSource → Handler`: the call is joined to an endpoint elsewhere in the workspace, and `depends_on` continues into that handler's own reads. Three levels, tried in order: an explicit `services` mapping (`confirmed`), a client name matched to a service name (`likely`), a route matched across the whole workspace (`likely`; several candidates are `unresolved` with kind `call`, listing them). Within a service chosen by the first two levels the match is on the **known tail** of the path, because an API gateway rewrites prefixes and the tail is what survives: a leading placeholder is the call's base address, never a route segment, the tail begins after it and after the last `{?}`, and behind a gateway the tail may be longer than the route, so the comparison runs from the end over whichever is shorter. At the third level only a full match counts. See `docs/adr/0010`. A gRPC call joins by service and method name to the override in the class deriving from the generated base — deterministic, `confirmed`. |
| **Cross-service gap** | The unguarded-write finding's cross-service form, not a second finding beside it. The rule is `CROSS_SERVICE_GAP` when the handler at the head of the chain publishes an event that has consumers and no consumer covers the key; it is `UNGUARDED_WRITE` when no such event exists. An invalidation reached through `publishes → consumes` covers the key exactly as one reached through `calls` does, at no better confidence than the `consumes` edge it crossed. |
| **Annotation** | The agent's resolution of one `unresolved` item, applied to the in-memory graph for the rest of the session and recorded so the report can list what the agent assumed. Every edge an annotation creates is `likely`. Resolving a `cache_api` or `event_api` item registers the recognizer and re-indexes the affected solution through the ordinary path, so the graph is only ever built one way. Nothing persists between runs. |
| **Stored procedure** | `schema.name` in one database. Created from either half: the code half creates it on meeting a call, the catalogue half on meeting its definition, and they are the same vertex. A procedure with no outgoing edges means one of two different things, and the graph must say which — see **Hidden write** and `docs/adr/0007`. |
| **Trigger** | `schema.name`, carrying the table it hangs on and the events it fires for. A trigger participates in a chain only when the write's events intersect its own: a trigger declared `FOR DELETE` is not reached by an `INSERT`, and `TRUNCATE` reaches no trigger at all. |
| **View** | `schema.name`. Reads tables and other views; nothing writes through one. |
| **Hidden write** | A write to a table that the handler's own code does not contain: performed by a stored procedure it calls, or by a trigger that fires on a table it writes. The term exists because it is what phase 2 adds to the unguarded-write rule, and because these links of a chain look different in the report — they carry a database object's name where code carries a `file:line`. |
| **Table** | `schema.name`. The unit of joining: EF, Dapper, ADO.NET, stored procedures, triggers and views all reduce to the same vertex, and two solutions naming the same table share it. The database is an attribute, never part of the identity — which is what lets a table named by EF, where no database is known, meet the same table named by the catalogue, where one is. A workspace carries at most one database; see `docs/adr/0007`. |
| **depends_on** | The derived relation `CacheKey → Table | CacheKey | ExternalSource`: the transitive closure of `caches ← Handler → reads/calls/serves → …`. The closure runs through database objects too: a handler that calls a stored procedure depends on what that procedure reads, and one that reads a view depends on the view's tables. It runs through `serves` into another service's handler and on to that service's tables; an `ExternalSource` with no `serves` is a leaf, and only a TTL can guard it (`EXTERNAL_NO_TTL`). Not stored; computed on query. |
| **Budget** | How stale a table's data may legitimately be, in seconds. A key whose TTL is within the budget is not a finding. Default 60 s; set per table or mask in the workspace config. |
| **Confidence** | `confirmed` — the static analysis proved every edge on the path. `likely` — an edge on the path is inferred. `unknown` — the path crosses an `unresolved`. The agent's inference is never reported as fact. The catalogue and the T-SQL grammar are both deterministic, so the database half produces no `likely` edges at all; where it cannot answer, it produces an `unresolved` instead. |
| **Unresolved** | A construct the indexer met and could not reduce, recorded with its snippet and a reason. Never a silent skip and never a guess. |
| **Finding** | One rule firing on one path, carried with its confidence and its evidence. |
| **Chain** | The report's only shape for a finding: a linear top-to-bottom path from the write to the key, each line carrying a file:line or a database object name. No diagrams. |

## The cached value is opaque; only what was read matters

Nothing in the graph inspects what a handler *put* into the cache. Whether the entry holds an EF
entity, a DTO assembled from five tables, a computed price or another service's answer changes
nothing: a key's dependencies are everything the handler **read** on the way to its `Set`. This is
what lets one model cover EF, Dapper, stored procedures and HTTP without a special case for each,
and it is why a key whose write path reads nothing has no dependencies at all — a pure computation,
correctly, changes only with a deploy.

## A key with no reads is a store, and no rule applies to it

Session entries, distributed locks, idempotency keys, rate-limit counters and tokens live in the
same Redis as the caches and look identical at the call site. They have no source of truth, so
"this write has no invalidation" is not a defect there — it is the design. Role is therefore
computed during indexing, not asked for later, and every rule in the detection set filters on it
first. Getting this wrong in either direction is a precision bug, which is why an unclassifiable
key becomes `unresolved` with kind `role` rather than defaulting to `cache`.

## A finding belongs to the handler, never to the procedure or the trigger

Once procedures and triggers can write, the unguarded-write rule stops having an obvious subject:
three different vertices on one chain performed a write. The subject is always the handler at the
head of the chain. Procedures and triggers are links, because the person reading the finding fixes
code and adds the invalidation to a handler — and because a chain with no handler at its head has
nobody to fix it.

The consequence is a deliberate blind spot. The catalogue knows procedures that no indexed code
calls, and those procedures write tables that cached keys depend on. They produce no findings. That
is the same restraint as `docs/adr/0003`: a graph that reports every write it can see, without
knowing whether anything reaches it, reports the tool's own incompleteness as the code's defect.

## Orphan invalidation is measured against cache writes, not database writes

"A `Remove` of a key nobody writes" has two readings, and only one of them holds without a database
in the graph. See `docs/adr/0003`.

## The core never sees a path

`CacheDetective.Core` takes a Roslyn `Solution` and an open database connection; turning `.sln`,
`.slnx` and `.csproj` into one, and an `env:` reference into the other, is the CLI's job. The seam exists because the analysis is semantic-model work that is fully testable
on sources compiled in memory, while the MSBuild half is an adapter with almost no logic and a very
slow test. See `docs/adr/0002`.

## Roslyn no longer loads MSBuild in the calling process

Measured on 2026-09-02 against `Microsoft.CodeAnalysis.Workspaces.MSBuild` 5.9.0: the package ships
`contentFiles/any/any/BuildHost-net472/` and `BuildHost-netcore/`, and `MSBuildWorkspace` starts one
of them as a **separate process**. Every deployment question about this plugin follows from that
one fact, because a plain single-file publish leaves those folders loose beside the executable.
See `docs/adr/0001`.

## The MCP server reads code and writes exactly one file

The server writes `.cache-detective/workspace.json` and nothing else. The report is composed and
written by the skill, from `get_evidence`, because the report is prose and prose belongs to the
agent. Everything the server touches beyond that — solutions, and later SQL Server and Redis — is
read-only, and the read-only requirement is stated in the README rather than assumed.

## Responses are small on purpose

Every tool answers in compact JSON, paginated, bounded at 8 KB. The agent must never receive the
whole graph: the graph is the server's, and the tools are the questions worth asking of it. A tool
that would exceed the bound pages instead of truncating, and says so.
