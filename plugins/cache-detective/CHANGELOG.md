# Changelog

## Unreleased

- **A form of entry point is a finder, not a branch** (`docs/adr/0019`). `AddTypeEntryPoints` decided
  seven kinds of entry point as seven consecutive branches in one method, so adding an eighth meant
  editing that method — the one thing the rest of this project is built not to require, since
  `CONTEXT.md` states the principle as *adding a library means adding a recognizer, never adding a
  branch* and cache stores, cache key objects and event buses all obey it. The forms are now six
  `IEntryPointFinder`s asked about a type in one recorded order, and adding a *uniform* form — one
  shape, one method, one kind — is a row in one of the three lists of `EntryPointTables.Default`,
  with no branch edited anywhere. A test adds a row for a shape no recognizer knows and finds a
  handler of that kind in the graph, which is the claim stated as a test rather than asserted in
  prose.
- **The finder order is written down rather than emergent.** The three uniform forms are three lists
  and not one because they emit at three different points in the order the branches ran, and that
  order must not move: an unresolved row's id is what an annotation binds to, so an id that shifts
  between runs binds the annotation to a different site. `BackgroundService` and `IHostedService`
  stay an exclusive pair rather than two rows, because `BackgroundService` *implements*
  `IHostedService` and a table running both against one type would find `ExecuteAsync` **and**
  `StartAsync` and record two entry points where the branches record one. The order test projects
  each finder to its type name *and* the rows it carries, because two of the six are the same class
  and a name-only assertion would pass with those two swapped — which is the one failure the test
  exists to catch.
- **`IndexAsync` reads as its own summary.** It was 127 lines whose local function closed over
  fourteen variables — a class with fourteen fields written in a syntax that hides them. The
  traversal is now `CallGraphWalk`, which owns the frontier and returns the handlers it expanded, and
  the six analyzers are one `SolutionAnalyzers` value; what is left builds the analyzers, finds the
  entry points, walks, resolves events, adds the EF write edges and classifies roles. The walk is
  unchanged: still breadth-first, still one expansion per method at its shortest distance from an
  entry point, still cut at twelve.
- **`WorkspaceSession` delegates to five collaborators while keeping its member surface and its
  single gate.** It was 1492 lines holding response fitting, database indexing, annotation, solution
  indexing and verification beside the state all five read; it is now 672. `ResponseFitting`,
  `DatabaseIndexing`, `AnnotationApplication`, `SolutionIndexing` and `VerificationRunner` hold
  nothing and are handed the graph, the configuration and the rest as arguments on each call. That is
  not a preference about style: `AnnotateAsync` re-indexes partway through and replaces the graph, so
  a collaborator built with the graph up front would go on reading one that is no longer in use.
  Every member a test names is still on the session, forwarding, and no test was edited.
- **Both behaviour snapshots compare equal**, `demo/behaviour-snapshot.json` and
  `skills/scan/evals/eshop/behaviour-snapshot.json` alike. That is the whole evidence
  that the graph did not move: every change above is a move, and snapshots that still match say no
  handler, edge, finding or unresolved id changed its position or its id. `build/test-baseline.txt`
  gained the two cases the ordering and recognizer-row tests added, so they sit inside the
  pass-to-skip guard rather than outside it.

- **The MCP server starts.** It never had, under any host. `cachedet` has required an `mcp`
  subcommand since the CLI grew one, and no host manifest passed it: `.claude-plugin/plugin.json`,
  `.mcp.json` and `.cursor-plugin/plugin.json` all invoke `bin/cachedet-launcher.cmd` with nothing
  after it, the launcher ended in `"%EXE%" %*`, and so the binary printed its usage to stderr and
  exited — which every host reports as `CONNECTION_CLOSED`, a message that describes a transport
  fault and this is not one. An argumentless launch now means `mcp`, which is what the launcher
  exists for; anything else is still forwarded as written, so `cachedet-launcher.cmd --version`
  keeps working. Fixing it in the launcher rather than in three manifests makes the next manifest
  correct by construction, including the `.mcp.json` a user may copy into their own configuration.
- **The end-to-end test launches the server the way the manifests do**, through
  `cmd /d /c cachedet-launcher.cmd` with no arguments. It previously called the published binary and
  added `mcp` itself, so it exercised an invocation no host performs — which is the whole reason a
  broken manifest survived five phases behind a green suite. Reverting the launcher fails the new
  case with *the server closed stdout before answering initialize*, the same symptom the host
  reports, so the guard is not vacuous.
- `build/test-baseline.txt` gained the new case and the eleven from the two preceding commits that
  were never recorded, which had left those tests outside the pass-to-skip guard.

- **The detection rules terminate on an enterprise-sized graph** (`docs/adr/0018`). `depends_on`
  enumerated one row per path and cut cycles with a path set, which is also the absence of a memo: at
  depth 12 and an out-degree of 4.6 that is ~4.6^12 walks per handler, and each visit scanned all
  24 687 edges for want of an adjacency index. On a 64-project solution `index_solution` never returned
  — one core at 100% for 46 minutes, killed by hand. It now returns in 152 s, which is the MSBuild load
  and nothing measurable beyond it. No prepared corpus had caught this because `MetricsCommand`
  evaluates no rules, so nopCommerce and Orchard were only ever measured on a path that never calls the
  closure.
- **`depends_on` returns one row per target**, at the strongest confidence the graph reaches it by and
  the shortest path at that confidence. That is the row `UnguardedWriteRule`, `StaleParentKeyRule`,
  `ExternalNoTtlRule` and both trace builders each already selected with
  `OrderBy(Confidence).ThenBy(Path.Count).First()`, so no finding moves; the walk now searches for it
  instead of enumerating everything and discarding all but one. Confidence is part of the search state
  rather than minimised after the fact, because `(Confirmed, 10)` and `(Likely, 2)` are incomparable —
  the first is the better row and the second the better prefix.
- **A walk that goes round a cycle no longer reports incompleteness.** Without a path set a two-method
  cycle spends the whole depth budget, and reporting that would have set the flag on every solution
  with a recursive call in it and made the verifier withhold refutation everywhere. The depth limit is
  now judged when the walk ends: a refused descent counts only if that source was never settled at a
  confidence at least as strong. The positive case — a chain longer than the limit, with a table below
  the cut — had no test before and now has one, so the rule cannot pass vacuously.
- `demo/behaviour-snapshot.json`, which records five findings with their chains and confidences, did
  **not** move.

- **A fold yields the set of values a site may produce, and says when it collapsed one**
  (`docs/adr/0015`). A local assigned in several places and a conditional expression are the same
  branching written two ways; both used to collapse to one unknown and lose the site. The folder now
  returns every value, de-duplicated, ordered ordinally, and capped at eight on the result rather than
  on the number of branches. Beside the set it carries a `collapsed` mark, set when the cap was
  reached, when a member could not be named, or when the value came from a variable that builds
  itself — and propagated through every composite, so a single template built over a collapsed part is
  still known to be uncertain.
- **`skills/scan/evals/eshop/expected.json` re-approved.** eShop's `API.GetAllCatalogItems` assembles
  its URL from a local assigned in three branches, one interpolating a conditional, so the call folded
  to `…/catalog/items{?}` and no endpoint matched its unknown tail. It now names four templates, and
  the one that adds no filter resolves a `serves` edge to `CatalogController.ItemsAsync` on its own.
  The file gains a `catalogItemsServes` expectation asserting exactly that, which is the corpus proof
  that the motivating case is fixed.
- `demo/behaviour-snapshot.json` and `skills/scan/evals/eshop/behaviour-snapshot.json` did **not**
  move. Both record findings and unresolved rows; the catalog-items URL always carried a literal
  segment, so it never was an unresolved row. What it lacked was a `serves` match, which those
  snapshots do not record.
- **Only a certain invalidation grants a suppression.** An `Invalidates` edge is now *must* when, and
  only when, the site's fold named exactly one value and was not marked collapsed; otherwise it is
  *may*. A handler whose `Remove` takes one of three templates removes exactly one at run time, so
  counting it as coverage would hide the findings for the other two — and a suppressed finding is
  invisible where a false one is merely noisy. The modality, not the size of the set, decides: a set of
  one whose part outgrew the fold's bound, or whose sibling could not be named, is a choice wearing the
  shape of a certainty. This covers `remove`, `remove_by_prefix` and `remove_by_tag` alike. What
  changed is the modality and never the matching — a prefix still covers by pattern and a tag by
  intersection.
- Two suppressions require certainty: the missing-invalidation suppression in `UnguardedWriteRule`, and
  the independent one in `StaleParentKeyRule` that skips a parent covered by a reachable removal.
  `ORPHAN_INVALIDATION`, `PATTERN_MISMATCH` and the search that finds the *child* removal
  `STALE_PARENT_KEY` starts from all go on accepting a may: the reason a may grants no suppression is
  uncertainty, not absence, and a removal that may fire is neither dead code nor a mismatch. A may-edge
  is otherwise unchanged and still appears in the chain the report prints.
- The modality travels on `PendingCacheOperation` and is honoured when an annotation turns it into an
  edge. An annotation resolves what the key *is*; it does not resolve whether the site had a choice, so
  naming the unnameable member of a mixed removal no longer creates the certainty the fold refused.
- **No recorded snapshot moved, and no existing test asserted a suppression this withdraws.** The demo
  corpus's two removals are both single interpolations that fold to one certain value, so their
  suppressions survive unchanged — which is the case the policy is required to leave alone — and the
  eShop snapshot records no findings at all.
- **A publish is attributed to the caller that named the event type** (`docs/adr/0017`), not to the body
  that physically contains the call. A shared helper taking its event as a parameter — eShop's
  `PublishThroughEventBusAsync` — now publishes nothing of its own and stays a link on the chain through
  its `Calls` edge, exactly as a stored procedure is. A method that constructs the event in its own body
  still names it itself. On eShop every one of the fourteen publishes now starts at the handler or
  controller that constructed the event, and none at an integration-event service.
- Recovery is per caller, and its failures are results too. A branch that ran out of hops, found no
  caller, hit a cycle or passed an expression naming nothing is recorded as an `UnresolvedKind.Event`
  row at the publish site naming that caller and that reason. One site may therefore produce edges and
  rows at once, which is the correct outcome: without it, a helper with one good caller and one
  exhausted branch left no trace of the second publisher at all.
- Attribution is decided after the walk rather than during it. The walk is still discovering handlers
  while the analyzer runs, so a caller met later would look unreachable purely for being met later; the
  triples are resolved once every handler exists. The handler is looked up and never created — a caller
  no entry point reaches gets a row saying so, because a chain head nothing reaches is a finding
  addressed to nobody. This does not reopen `docs/adr/0014`: the caller lookup is a symbol query, not
  part of the breadth-first walk, and each method is still expanded exactly once.
- **No snapshot moved for this change either.** The behaviour snapshots record findings and unresolved
  rows, not which vertex a publish starts from; eShop's publish and unresolved-event counts are
  unchanged at 14 and 1. The count did not fall because on eShop every caller named a distinct type, so
  the flat set had never merged two callers onto one — what moved is where each edge starts, which the
  new `PublishAttributionTests` pin directly.
- **The new work is order-independent, and says so by construction.** Both changes above introduced
  fresh order sensitivity that `docs/adr/0014` had removed from the walk: a fold set's element order
  reaches vertex creation, and `SymbolFinder.FindCallersAsync` does not specify the order it returns
  callers in — which now decides which handler a publish hangs on and in what order rows are recorded.
  The callers a recovery walks are collected and sorted by a stable key (the caller's display string,
  then the site's file and position) before folding, and the deferred attribution triples and failure
  rows are sorted by the same key before resolving. `Unresolved` ids are the sharp end, because an id is
  what an `annotate` binds to: an id that moves between two runs of one solution binds the annotation to
  a different site.
- No snapshot moved for the ordering either: the behaviour snapshots record findings and unresolved rows
  sorted, so an order that changed under them would not show. `Phase5DeterminismTests` therefore compares
  sequences *as recorded* rather than sorted, and asserts the recorded order **is** the stable key order
  — an assertion that fails when the sort is removed, which sorting both sides before comparing would
  not.
- **The phase is re-measured, and the numbers are in `skills/scan/evals/metrics/README.md`.** The three
  corpora were indexed again at the same pinned revisions, with clean worktrees and the same declaration
  files byte for byte — checked by comparing each row's recorded configuration hash against its `before`
  row, so the analyser is the only difference between the pair. On nopCommerce the key object takes cache
  operations from 3 to 116 and unresolved keys from 121 to 28, with coverage 0.023 to 0.784. On
  eShopOnContainers the fold set takes `serves` edges from 18 to 19 and `reads` from 68 to 71, because one
  `ExternalSource` is created per value a URL folds to. Orchard Core reproduces every count to the digit,
  which is what makes the other two readable as effects of the analyser rather than of drift. No coverage
  target is set: the number is the finding.
- The `after` rows were taken twice: once when the phase closed, and again after its code review, because
  two findings changed what the analyser recovers. nopCommerce read 27 operations against 97 unresolved
  keys the first time and 116 against 28 the second, and the larger part of that is the fix that
  recognises a factory call wherever it is met rather than only at the outermost expression — the form
  nopCommerce is written in throughout. The rows in the file are the second measurement. The `before` rows
  were not re-taken and did not need to be: they were always the phase-4 analyser against the final
  configuration, which is the baseline the pair wants.
- Two counts named in advance did not move, and both are recorded as results. eShop's `unresolved.call`
  stays at 48 because a URL with a literal segment that matches no route is not an unresolved row at all
  — only a fold with no values or no literal segment is — so that count never could have measured the
  `serves` improvement. And `publishes` stays at 14 against `docs/adr/0017`'s expectation that it would
  fall, because every caller of eShop's publish helper names a distinct event type, so the flat set had
  never merged two callers onto one edge; what moved is where each edge starts, which no aggregate here
  records and `PublishAttributionTests` pins instead.
- The `Boundaries` section of `README.md` now describes what remains rather than what was fixed: Orchard
  Core's `CacheContext`, multi-valued SQL, and a local that builds itself with `+=`, whose compound
  assignment the fold cannot read and whose initialiser it therefore names but marks collapsed.
- **`build/test-baseline.txt` re-recorded for one deliberate rename.**
  `WorkspaceWriteConfinementTests.Index_and_query_create_only_the_workspace_configuration` is now
  `Outside_msbuilds_intermediate_output_index_and_query_create_only_the_workspace_configuration`. The test
  was not removed and did not stop running; what changed is that it now indexes a project *inside* the
  workspace root it hashes, which is the arrangement the guarantee is about, and its name states the
  allowance that arrangement forces. Opening a project there runs MSBuild's design-time build, which writes
  its own intermediate output under the project's `obj` — measured, not assumed — so the exact assertion is
  not available and the name says which one is made instead. The re-recorded baseline holds 691 cases
  against the old 569; the only identity the old baseline had and the new one does not is that one rename,
  and the other 123 are cases this phase and this review round added.

- **An escaped positional hole is decoded in a non-constant interpolation, and used to be left doubled.**
  `InterpolatedStringTextSyntax.ValueText` hands back the braces as written — it decodes the string's
  escapes, not the interpolation's own — and phase 4 was built on the belief that it decoded both. The
  belief held for every template in the fixture, because a constant interpolation never reaches the
  interpolation walk at all: `GetConstantValue` answers it first with the value the compiler already
  decoded. nopCommerce's `NopEntityCacheDefaults<TEntity>` writes `$"Nop.{EntityTypeName}.byid.{{0}}"`,
  which is not constant, so the `{{0}}` arrived doubled, matched no format item, and the factory
  substituted into nothing and dropped its argument in silence. Measured rather than assumed: the fixture
  folded to `fixture.{EntityTypeName}.byid.{{0}}` before the fix. The regression's interpolation is
  deliberately non-constant, so it cannot pass by taking the constant path.
- **A possible removal of the parent now reaches the `STALE_PARENT_KEY` chain**, as it already did for
  `UNGUARDED_WRITE`. The suppression is withheld because of that removal, so a finding that omitted it sent
  the reader to a `Remove` of the parent and left them to conclude the tool was wrong. The may-edges are
  appended after the chain that was already there, so nothing that reads the chain by position moves, and
  each carries the reason it did not count. The search both rules use is now one helper,
  `PossibleInvalidations`, so a reader meets the same explanation whichever finding shows it.

- **The `after` rows were taken a third time after the escaped-hole fix, and not one count moved.** That is
  the outcome the fix predicted: `NopEntityCacheDefaults.ByIdCacheKey` and `ByIdsCacheKey` — the only two
  cache declarations in the corpus that write an escaped hole in a non-constant interpolation — were
  already producing a `CacheKey` before the fix, just one whose template kept its `{{0}}` and whose factory
  argument went nowhere. Decoding the hole changes what those two templates say, not how many there are, so
  nopCommerce holds at 116 operations against 28 unresolved keys and coverage 0.784, and eShopOnContainers
  and Orchard Core are identical to the digit as well.
- Two claims in `skills/scan/evals/metrics/README.md` contradicted the rows they described and are
  corrected. The seconds do not all fall: nopCommerce and eShopOnContainers finish slower than their
  `before` row and only Orchard faster, which is what a single run on a warm machine measures, and no claim
  is made about load time. And nopCommerce's unresolved `role` going 0 to 4 is not the first time a corpus
  gave that classifier something to say — Orchard's `before` row already carried four and still does. What
  survives is the part that matters: the §12 `role` debt was recorded because eShop offered zero candidates
  to label, and a corpus with 116 operations and templated keys is one that can now be sampled.
- The `Boundaries` note on multi-valued SQL described behaviour the fragment-level fallback had already
  restored. It now says what is true: the branchy value collapses to one parameter and the statement around
  it keeps its literals, so an `UPDATE`'s table and write survive; what is not done is parsing once per
  value and uniting the results.

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
