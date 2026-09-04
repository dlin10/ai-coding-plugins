# Cache Detective — domain language

The vocabulary the code, the MCP tool schemas and the report must use. Written during the interview
that scoped phase 1; extended as later phases land.

| Term | Meaning |
|---|---|
| **Workspace** | Everything one scan covers: the solutions, and later the databases, named in `.cache-detective/workspace.json`. One graph spans the whole workspace, never one graph per solution. |
| **Code indexer** | The Roslyn half: walks solutions, emits cache keys, handlers, tables, calls. |
| **DB indexer** | The catalogue half: reads a live SQL Server's `sys.*` views for procedures, triggers and views. Not in phase 1. |
| **Sink** | A cache call site — a `Get`, `Set`, `Remove` on a caching API. |
| **Recognizer** | The declarative description of one caching library: its type, its methods, each method's semantic, which argument carries the key, which carries the TTL. Adding a library means adding a recognizer, never adding a branch. |
| **Semantic** | What a sink does to the entry: `get`, `set`, `remove`, `remove_by_tag`, `remove_by_prefix`, `increment`, `expire`, `lock`. The word for the operation's kind, never for meaning-of-the-value. A recognizer method may also name one argument and the constant that turns the call into a conditional set — the only shipped case is `StringSet` with `When.NotExists`. |
| **Template** | A cache key with its variable parts folded to names: `product:{id}`. Known substitutions become `{name}`, unknown ones `{?}`. Two sites that build the same template *in the same store* are the same `CacheKey` vertex; the same template in two stores is two vertices, because an invalidation in one store does not reach the other. When sites merge, TTL merges as the longest with "no TTL" meaning infinity. Tags merge twice, because a tag means different things to the two questions asked of it: the intersection across sites is what a tag removal must match to *cover* the key, since entries written by an untagged site are never evicted by it, and the union is what keeps a tag removal from being called dead. A merge must never grant a suppression that one site alone would not. |
| **Role** | `cache` — the value is derived from sources and can go stale. `store` — the cache *is* the storage (sessions, locks, idempotency, rate limits, tokens); there is no source of truth, so staleness is meaningless. Detection rules apply only to `role: cache`. |
| **Handler** | A method reached from an entry point, identified as `handler:<Solution>/<Type>.<Method>`. Entry points are controllers, minimal API endpoints, and anything recognised by interface or base class. |
| **Stored procedure** | `schema.name` in one database. Created from either half: the code half creates it on meeting a call, the catalogue half on meeting its definition, and they are the same vertex. A procedure with no outgoing edges means one of two different things, and the graph must say which — see **Hidden write** and `docs/adr/0007`. |
| **Trigger** | `schema.name`, carrying the table it hangs on and the events it fires for. A trigger participates in a chain only when the write's events intersect its own: a trigger declared `FOR DELETE` is not reached by an `INSERT`, and `TRUNCATE` reaches no trigger at all. |
| **View** | `schema.name`. Reads tables and other views; nothing writes through one. |
| **Hidden write** | A write to a table that the handler's own code does not contain: performed by a stored procedure it calls, or by a trigger that fires on a table it writes. The term exists because it is what phase 2 adds to the unguarded-write rule, and because these links of a chain look different in the report — they carry a database object's name where code carries a `file:line`. |
| **Table** | `schema.name`. The unit of joining: EF, Dapper, ADO.NET, stored procedures, triggers and views all reduce to the same vertex, and two solutions naming the same table share it. The database is an attribute, never part of the identity — which is what lets a table named by EF, where no database is known, meet the same table named by the catalogue, where one is. A workspace carries at most one database; see `docs/adr/0007`. |
| **depends_on** | The derived relation `CacheKey → Table | CacheKey | ExternalSource`: the transitive closure of `caches ← Handler → reads/calls → …`. The closure runs through database objects too: a handler that calls a stored procedure depends on what that procedure reads, and one that reads a view depends on the view's tables. Not stored; computed on query. |
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
