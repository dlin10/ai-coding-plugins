# Changelog

## 0.4.0 - 2026-09-05

- Added runtime verification, built into the server rather than driven through third-party MCP servers,
  so a cached value is sampled, compared and redacted before a byte of it becomes a tool result
  (`docs/adr/0011`). A `runtime-verifier` subagent calls one tool of it, and a scan verifies only with a
  `verify` section and either `--verify` or `"auto": true`.
- Verification observes and never suppresses: it reports `refuted`, `possible` or `not_verifiable`
  beside a finding and never changes its confidence or whether it is reported (`docs/adr/0012`).
- Only field comparison refutes. The age of an entry never does, because `EXPIRE` extends a deadline
  long after the value was written and a handler may read a row, wait, and only then write what it
  already had — so age contributes the `possible` signal and nothing stronger.
- The Redis reader sends only `SCAN`, `TYPE`, `GET`, `HGET`, `TTL` and `OBJECT IDLETIME` through a
  single seam a test inspects, walks the keyspace with a raw `SCAN` rather than `KEYS` under a budget of
  50 iterations or 5 seconds, and refuses a connection string that disables `INFO` because the client
  then writes a probe key of its own.
- **Guarded an array-valued attribute argument.** `AttributeTemplate` read the first constructor
  argument's `Value` without checking its kind, and an array-valued argument — `[AcceptVerbs(...)]` in
  Orchard Core, `[FormValueRequired(...)]` in nopCommerce — threw and took the whole index down with it.
  Both repositories now index end to end.
- **The call graph walk is breadth-first, and its output no longer depends on load order**
  (`docs/adr/0014`). The depth-first walk kept a memo of the shallowest depth each method had been seen
  at and expanded a method again whenever a later path reached it from higher up — recording every edge
  below it, and every cache operation and `unresolved` row in it, a second time. Neither
  `Solution.Projects` nor `SymbolFinder.FindImplementationsAsync` specifies its order, so four runs over
  one clean Orchard Core checkout gave four different edge counts. Every method is now expanded exactly
  once, at its shortest distance from an entry point, and five consecutive runs agree on every count.
- Added `cachedet metrics`: load and coverage measurement, labelled `role` and `efwrite` samples with
  accuracy and false-positive rate, `--compare` against a pinned corpus revision, and declared
  `cache_api` recognizer files.
- Measured the corpora. nopCommerce indexes in 95 s over 33/33 projects and Orchard Core in 159 s over
  227/227, both complete, where both previously reached an hour of CPU time. Coverage rose with the walk
  fix, which removed sites the old numbers counted twice: Orchard 0.338 to 0.389, nopCommerce 0.013 to
  0.023.
- Measured the EF write heuristic on eShopOnContainers, for the reasons in `docs/adr/0013`: ten labelled
  write sites, accuracy 1.0 and a false-positive rate of 0.0. The sample is small because the corpus
  holds ten candidates, not because ten were chosen.
- **The `role` classifier is not measured.** Its sample came back with zero candidates against a target
  of thirty: eShopOnContainers' whole recognised cache surface is three `IDatabase` call sites keyed on
  runtime variables, so no key carries a template to classify. Only the reason is recorded, not a number,
  and phase 5 carries the debt — see `docs/cache-detective-spec.md` §12.

## 0.3.0 - 2026-09-04

- Added events, external service joins, annotations, derived cross-service coverage, and the scan
  subagent workflow.
- Added the Notifications demo cases and the read-only eShopOnContainers eval.
- Event recognizers may declare a publisher-only or consumer-only integration point.
- A published event's type is what the argument can be — a construction, both branches of a
  conditional, every assignment to a local, a parameter followed to its callers — never the declared
  base type; a local assigned differently in several places folds to `{?}`.
- HTTP `serves` matching cuts a leading base-address placeholder, tolerates a gateway prefix, and
  reports a chosen service with no matching endpoint as an annotatable `call`; a template-less verb
  attribute no longer creates its own route beside the method's `[Route]`.
- Tool paging partitions a result set once, so pages never overlap; derived structures are memoised
  per graph version, which takes `annotate` on eShopOnContainers from about fifteen seconds to under
  half a second.

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
