# Changelog

## 0.2.0 - 2026-09-03

- Added T-SQL parsing of raw SQL in code: Dapper, ADO.NET and EF raw-SQL calls are folded and parsed,
  and where an unknown fragment lands in the parse tree decides whether the statement's tables are
  extracted or the statement becomes unresolved with the position that defeated it.
- Added the database indexer and the `index_database` tool: stored procedures, triggers and views,
  what each reads and writes, and calls between procedures, read over a read-only connection that
  touches only `sys.` catalogue objects.
- Added hidden writes to the unguarded-write rule: a write performed by a called procedure or by a
  fired trigger is reported against the handler at the head of the chain, and a covering invalidation
  is looked for there. Triggers join a chain only where the write's events meet theirs.
- Added `depends_on` through stored procedures and views, and derived reasons that distinguish "no
  database is indexed" from "this procedure is not in the catalogue" without depending on the order
  the two halves were indexed in.
- Added typed `databases` configuration with `env:` connection references, refusing a committed
  connection string, a second database, or a provider other than SQL Server by name.
- Raised the `export_graph` version to 2 for the new vertex types and the `fires` edge, and extended
  `trace_table` and `workspace_status` with database objects.
- Added a demo workspace under `demo/` and integration tests against a live SQL Server, run in CI on
  Linux under a login holding only `VIEW DEFINITION` and no table access.

## 0.1.0 - 2026-09-03

- Added whole-workspace Roslyn indexing for handlers, cache keys, calls, and EF Core table access.
- Added cache/store role classification, staleness budgets, unresolved evidence, and cross-solution graphs.
- Added unguarded-write, orphan-invalidation, and near-miss template findings with evidence chains.
- Added the stdio MCP server, explicit `/cache-detective:scan` workflow, single-file Windows x64
  distribution, and CI/release automation.
