# Labelled samples on eShopOnContainers

Two samples measure the two heuristics that ship without a proof of their accuracy: the cache-key
`role` classifier and the EF write heuristic. Each row is one candidate the tool produced, labelled by
hand against the corpus source, never against the tool's own output.

## The corpus and why it is this one

The corpus is [eShopOnContainers](https://github.com/dotnet-architecture/eShopOnContainers), pinned at
revision `7e5ae7b54ec9f331a1c60b65bd45519f5aecc327`, solution
`src/eShopOnContainers-ServicesAndWebApps.sln`.

`docs/adr/0013` records the choice: nopCommerce uses linq2db rather than EF Core and hides its keys
behind a `CacheKey` object, Orchard's YesSql document store makes table-level joining meaningless, and
eShopOnAbp implements its repositories inside NuGet packages the call graph cannot follow. eShop calls
EF Core directly from code the indexer reads.

## Checkout and the cleanliness requirement

Set `CD_ESHOP_ROOT` to the checkout. **The corpus revision is what fixes the sample, not the absence of
a marker file**: generation records `git rev-parse HEAD` into every sample and refuses to run against a
tree that `git status --porcelain` reports as dirty, and `--compare` re-checks both the revision and the
cleanliness before it compares a stored sample with a fresh index. A checkout carrying the phase-3
`planted-webmvc-catalog-cache.patch` is dirty and will be refused; unapply it before regenerating.

Row paths are recorded relative to the corpus root, so a sample generated on one machine compares on
another.

## Commands

```powershell
$env:CD_ESHOP_ROOT = "C:\Dev\eShopOnContainers"
$sln = "src/eShopOnContainers-ServicesAndWebApps.sln"

# Regenerate the candidate rows (--force replaces existing labels; omit it to protect them)
cachedet metrics --sample role    --count 30 --root $env:CD_ESHOP_ROOT --solution $sln --out skills/scan/evals/metrics/role-sample.json
cachedet metrics --sample efwrite --count 20 --root $env:CD_ESHOP_ROOT --solution $sln --out skills/scan/evals/metrics/ef-write-sample.json

# Check a stored sample still matches a fresh index at the manifest revision
cachedet metrics --compare skills/scan/evals/metrics/ef-write-sample.json --root $env:CD_ESHOP_ROOT --solution $sln

# Write the metrics block from the labels (re-running verifies rather than overwrites)
cachedet metrics --score skills/scan/evals/metrics/ef-write-sample.json
```

Label by filling in `verdict` and `justification` only. Every other field is generated; editing one
makes `--compare` fail, which is the point. A justification is at least forty characters and cites the
lines it relies on.

## Verdicts

`role-sample.json` — does the key play the role the classifier assigned?

| Verdict | Meaning |
| --- | --- |
| `cache` | The key holds a derived copy that may be recomputed from its source. |
| `store` | The key holds the only copy; losing it loses data. |
| `unclear` | The source does not settle it. Excluded from both formulas. |

`ef-write-sample.json` — does the predicted write site really write that table?

| Verdict | Meaning |
| --- | --- |
| `true_write` | The site mutates rows of the named table. |
| `false_write` | It does not; the prediction is a false positive. |
| `unclear` | The source does not settle it. Excluded from both formulas. |

## Formulas

Both denominators count only labelled rows — a row labelled `unclear` is excluded from both and counted
separately as `unclearCount`, so an unreadable site can never flatter or punish a score. When no row is
labelled the status is `not_applicable` and both values are `null` rather than zero.

```
accuracy           = rows whose label confirms the prediction / labelled rows
falsePositiveRate  = rows labelled false_write            / labelled rows      (efwrite only)
```

For `role` the label is stated in the tool's own vocabulary, so confirming the prediction means the
verdict equals it. For `efwrite` every prediction is the single value `write`, and the label answers
whether that write is real, so `true_write` confirms it.

## Candidate counts and actual sizes

The target is 30 rows for `role` and 20 for `efwrite`. Where the corpus offers fewer candidates the
sample takes all of them; rows are never duplicated and never invented, so a short sample stays short.

| Sample | Target | Candidates | Rows |
| --- | --- | --- | --- |
| `role-sample.json` | 30 | 0 | 0 |
| `ef-write-sample.json` | 20 | 10 | 10 |

**`role` has no candidates.** The entire recognised cache surface of eShopOnContainers is a single
`IDatabase` field in `src/Services/Basket/Basket.API/Infrastructure/Repositories/RedisBasketRepository.cs`
— one match for `IDatabase` and none at all for `IMemoryCache`, `IDistributedCache` or `HybridCache`
across the 658 C# files in `src`. Its three call sites (`KeyDeleteAsync` on line 18, `StringGetAsync` on
line 31, `StringSetAsync` on line 46) are recognised as cache operations, but each keys on a method
parameter rather than a literal or an interpolation, so no key template can be folded. A full
measurement reports `cacheOperations: 0` against `unresolved.key: 3` — the three sites are counted as
unresolved keys, and a key with no template carries no role to label.

This is the one expectation in ADR 0013 that did not survive contact with the measurement: the ADR
counted the Redis-backed basket as "a genuine `store` alongside genuine caches", which it is in the
source, but not one the classifier can reach through a runtime key. Measuring `role` needs either a
corpus with literal keys or the annotation path that supplies a template by hand — not a row invented
here.

The ten `efwrite` candidates are every distinct write site the heuristic found, spanning four
projects and five tables (`dbo.Catalog`, `dbo.IntegrationEventLog`, `dbo.Subscriptions`,
`ordering.buyers`, `ordering.orders`).

## Manifest

`manifest.json` holds the expected shape of both samples: the corpus revision, and per file the sample
kind, the target count, the observed candidate count and the reason the actual size is what it is.
`--compare` reads the `samples` block; the `files` block is the same numbers keyed by file name, for
readers and for the gate.

## Load and coverage on nopCommerce and Orchard Core

`docs/adr/0013` gives these two repositories the role the specification's bench table always meant for
them — load time and coverage, not heuristic accuracy. Each was measured twice on **2026-09-07**, once
with the built-in recognizers alone and once with a declared `cache_api` file added, at a single
revision per repository with a clean worktree. Set `CD_NOP_ROOT` and `CD_ORCHARD_ROOT` to the
checkouts; the runs are `load-nopcommerce-{before,after}.json` and `load-orchard-{before,after}.json`.

| Run | Load | Index | Total | Projects | Vertices | Cache ops | Cache coverage |
| --- | --- | --- | --- | --- | --- | --- | --- |
| nopCommerce before | 14.5 s | 80.8 s | **95.3 s** | 33/33 | 5747 | 0 | 0.000 |
| nopCommerce after | 14.3 s | 83.1 s | **97.4 s** | 33/33 | 5743 | 3 | 0.023 |
| Orchard before | 68.4 s | 90.8 s | **159.2 s** | 227/227 | 2466 | 21 | 0.389 |
| Orchard after | 67.1 s | 92.4 s | **159.5 s** | 227/227 | 2466 | 21 | 0.389 |

This is the first run at real size that the incremental loader makes possible: both repositories used
to reach an hour of CPU time, and both now finish well inside the 20-minute budget with
`loadComplete` true — every one of nopCommerce's 33 and Orchard's 227 projects loaded, none missing.
nopCommerce reports no load diagnostics at all; Orchard reports 24 and still loads every project.

**Coverage after recognizers is below 0.7 in both, and `docs/adr/0013` already says why.** For
nopCommerce the declared `Nop.Core.Caching.IStaticCacheManager` recognizer works — cache operations go
from 0 to 3 and unresolved keys from 3 to 121 — and that jump *is* the finding: the ADR records that an
`IStaticCacheManager` takes a `CacheKey` object built by a key service rather than a string at the call
site, so the recognizer finds the call sites and the key folder, which reads string expressions, can
fold almost none of them. Coverage of 0.023 measures the key folder against object-valued keys, not the
recognizer. For Orchard the declared `OrchardCore.DynamicCache.IDynamicCacheService` recognizer changes
nothing measurable: cache operations stay at 21, unresolved `cache_api` stays at 8 and coverage stays
at 0.389 to the digit, because `GetCachedValueAsync`/`SetCachedValueAsync` are keyed on a `CacheContext`
object and the cache surface the ADR describes is a signal and tag machinery reached through
`ITagRemovedEventHandler`, which this file does not describe. Raising either number means teaching the
tool those APIs, which ADR 0013 rejects on purpose: these repositories were chosen to measure what
happens against an unfamiliar cache API.

These rows replace a set measured on 2026-09-05, and every count in them moved. That measurement was
taken while the call graph walk was depth-first over a memo of the shallowest depth each method had been
seen at, which expanded a method again whenever a later path reached it from higher up and recorded
everything below it a second time. Orchard's `before` row read 15252 edges and 906 unresolved `call`
rows against 7433 and 336 now; nopCommerce's `after` row read 231 unresolved keys against 121. The walk
is breadth-first since `docs/adr/0014`, every method is expanded once, and what those rows counted twice
they now count once. Coverage rises with them — Orchard 0.338 to 0.389, nopCommerce 0.013 to 0.023 —
because a duplicated site was inflating both sides of the fraction.

The old rows also carried a caveat this one does not: Orchard's edge and unresolved-`call` counts were
not reproducible, four runs at one revision giving 15252/906, 15204/888, 15027/871 and 14854/889, so no
delta between the two Orchard rows could be read as an effect of the recognizer. Five consecutive runs
of the rows above — three `before`, two `after` — agree on every count, which is the specification's
§11 stability target. Note that reproducible was never the same as right: nopCommerce reproduced its
1397 unresolved `call` rows exactly and they were inflated all the same.

## What phase 5 moved

The table above varies the *configuration* — one recognizer file declared or not — at one analyser. This
one varies the **analyser** and holds everything else still: same revision per corpus, same clean
worktree, same solution, and the same declaration file byte for byte. That last is checked rather than
assumed: every row records the SHA-256 of the recognizer or workspace file it read, and the phase gate
compares the recorded hashes between the `before` and `after` of each pair rather than trusting that two
files with the same name held the same content. The `before` rows were taken on **2026-09-07** against
the phase-4 analyser with the configuration already at its final content; the `after` rows on
**2026-09-08** against the phase-5 analyser as it stands after its code review — the review changed what
the analyser recovers, so the rows were taken again once every finding was fixed, and these are those
rows. The `before` rows were not re-taken and did not need to be: they were always the phase-4 analyser
against the final configuration, which is exactly the baseline this pair wants. The runs are
`load-<corpus>-phase5-{before,after}.json`.

| Corpus | Count | Before | After |
| --- | --- | --- | --- |
| nopCommerce | cache operations | 3 | **116** |
| nopCommerce | unresolved `key` | 121 | **28** |
| nopCommerce | cache coverage | 0.023 | **0.784** |
| nopCommerce | `caches` / `invalidates` / `reads` edges | 0 / 3 / 20 | **7 / 7 / 123** |
| nopCommerce | vertices, edges | 5743, 19750 | **5843, 19864** |
| nopCommerce | unresolved `role` | 0 | **4** |
| nopCommerce | unresolved `call` | 1007 | **1008** |
| nopCommerce | unresolved `cache_api` | 4 | 4 |
| nopCommerce | total seconds | 108.2 s | 114.0 s |
| eShopOnContainers | `serves` edges | 18 | **19** |
| eShopOnContainers | `reads` edges | 68 | **71** |
| eShopOnContainers | vertices, edges | 354, 431 | **357, 435** |
| eShopOnContainers | unresolved `call` | 48 | 48 |
| eShopOnContainers | `publishes` edges | 14 | 14 |
| eShopOnContainers | unresolved `event` | 1 | 1 |
| eShopOnContainers | cache operations, unresolved `key` | 0, 3 | 0, 3 |
| eShopOnContainers | total seconds | 24.8 s | 25.0 s |
| Orchard Core | every count above | — | **unchanged, to the digit** |
| Orchard Core | unresolved `role` | 4 | 4 |
| Orchard Core | total seconds | 180.3 s | 176.5 s |

**The key object, measured on nopCommerce: cache operations 3 to 116, unresolved `key` 121 to 28.**
Ninety-three sites stopped being rows the fold could not read and one hundred and thirteen operations
appeared, which is the right shape: a site may key on more than one template, so a recovered site can
yield several operations, but every operation that appeared came from a site already counted against the
tool. Coverage follows from 0.023 to 0.784. The mechanism is `docs/adr/0016`:
`Nop.Core.Caching.CacheKey` carries its template as a constructor argument and
`PrepareKey`/`PrepareKeyForDefaultCache` substitute into its positional holes, so the recognizer can
describe the object and the folder can read through it. The new operations arrive as 7 `caches`, 4 more
`invalidates` and the balance of the 103 new `reads` — one edge per operation once the single HTTP read
below is set aside — and vertices rise 100 because a `get` and a `set` on one template share a
`CacheKey` vertex.

**Most of that movement is the code review's, not the original task's.** The first measurement of this
pair read 27 operations against 97 unresolved keys, coverage 0.211. Two review findings moved it to 116
against 28: a key object reached through a local now folds to the set of values that local may hold, and
— the larger of the two — a factory call is now recognised wherever it is met rather than only at the
outermost expression, so `var key = _cacheKeyService.PrepareKeyForDefaultCache(defaults, id); await
_staticCacheManager.GetAsync(key, …)` resolves. That is the form nopCommerce is written in;
`CategoryService.cs:365` is the instance the review named and the corpus repeats it throughout. This
measurement does not separate the two fixes' contributions, and no attempt is made to guess the split.

**Cache coverage is 0.784, and no target is set for it.** No target, because the number is the finding
rather than the goal — reading a key the compilation does not contain would mean claiming a value. The
28 that remain are keys with no literal template to read, which is the honest residue; the earlier
wording here claimed that of all 97, and the review was right that it was wrong.

**nopCommerce's unresolved `role` went 0 to 4.** The classifier had no candidates on this corpus while
there were three cache operations, so it reported nothing; with 116 it has keys to classify and four it
cannot settle, which is a signal rather than a regression. It is not, however, the first time any corpus
has given that classifier something to say — Orchard Core's `before` row already carries four
unresolved-`role` entries and still does, so what is new is that a second corpus now produces them. What
survives is the point that matters: the `role` measurement debt in `docs/cache-detective-spec.md` §12 was
recorded because eShopOnContainers offered the classifier zero candidates to label, and a corpus with 116
cache operations and templated keys is one that can now be sampled. The debt is payable; these four rows
do not pay it, because §12 wants labelled rows and not a count of unsettled ones.

**nopCommerce's unresolved `call` went 1007 to 1008.** One site, and this measurement does not isolate
which. The reading the other counts support is one HTTP URL that now folds to two values where it folded
to one, the second having no literal segment and so recording a row of its own — which would also account
for one of the new `reads`, since `AddHttpReadAsync` creates an `ExternalSource` per fold value. That is a
reconciliation of aggregates, not an observation of the row.

**The fold set, measured on eShopOnContainers: `serves` 18 to 19, and `unresolved.call` unmoved at 48.**
`API.GetAllCatalogItems` assembles its URL from a local assigned in three branches, one interpolating a
conditional; it used to fold to `…/catalog/items{?}` and now names four templates, of which the one that
adds no filter matches `CatalogController.ItemsAsync` and resolves the `serves` edge. `reads` rises 68 to
71 and vertices 354 to 357 for the same reason: `HttpCallAnalyzer.AddHttpReadAsync` creates one
`ExternalSource` vertex and one `Reads` edge per value the fold yields, so four values where there was
one is three more of each. With the `serves` edge that is the whole of the +4 edges.

**`unresolved.call` is the wrong count for this improvement, and the measurement is what shows it.** A
row of kind `Call` is recorded when a fold yields no values at all (`HttpCallAnalyzer.cs:67`) or when a
value carries no literal segment (`HttpCallAnalyzer.cs:78`). A URL that *has* a literal segment but
matches no route is not recorded as unresolved at all — it is simply an `ExternalSource` with no `serves`
edge. The catalog-items URL always carried the literal `/catalog/items`, so it never was one of the 48,
and 48 is where it stays. The count that measures this change is `serves` against the `ExternalSource`
vertices behind `reads`.

**Publish attribution moved no count on eShop: `publishes` 14 to 14, unresolved `event` 1 to 1.**
`docs/adr/0017` expected the number of `publishes` edges to fall, on the reasoning that a helper merges
its callers' types onto one vertex. On this corpus it does not, because every caller of
`PublishThroughEventBusAsync` names a distinct event type, so the flat set never merged two callers into
one edge — and no publisher was outside the walk, so none became a row. What the change moved is which
vertex each of the fourteen edges *starts from*, which no aggregate in this file records. That is pinned
by `PublishAttributionTests` instead, and the ADR's prediction should be read as not borne out here
rather than as confirmed.

**Orchard Core reproduces every count exactly, and that is what makes the other two readable.** Same
binary, same recognizer hash, a corpus none of these changes reaches: 2466 vertices, 7433 edges, 21 cache
operations, 25 unresolved keys, 336 unresolved `call`, coverage 0.389 — identical in both rows, only the
seconds differ. It held through the review fixes too, which is the stronger statement: the change that
took nopCommerce from 3 operations to 116 moved nothing at all here. So nopCommerce's movement is an
effect of the analyser meeting a shape it can now read, and not of run-to-run drift. Orchard is unchanged
because its `IDynamicCacheService` is keyed on a `CacheContext`, which is a constructor id plus a fluent
chain of `AddContext` calls rather than a template with positional holes; `docs/adr/0016` declines to
describe it and says why, and this row is that decision measured.

All three corpora still load complete — 33/33, 227/227 and 30/30 projects, none missing. The seconds went
both ways: nopCommerce and eShopOnContainers finish slower than their `before` row (108.2 to 114.0 and
24.8 to 25.0) and Orchard Core faster (180.3 to 176.5). Nothing in this phase was aimed at load time and
no claim is made about it — these are single runs on a warm machine, and a spread of this size is what
that measures.
