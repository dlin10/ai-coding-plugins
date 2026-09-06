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
them — load time and coverage, not heuristic accuracy. Each was measured twice on **2026-09-05**, once
with the built-in recognizers alone and once with a declared `cache_api` file added, at a single
revision per repository with a clean worktree. Set `CD_NOP_ROOT` and `CD_ORCHARD_ROOT` to the
checkouts; the runs are `load-nopcommerce-{before,after}.json` and `load-orchard-{before,after}.json`.

| Run | Load | Index | Total | Projects | Vertices | Cache ops | Cache coverage |
| --- | --- | --- | --- | --- | --- | --- | --- |
| nopCommerce before | 20.3 s | 103.6 s | **124.0 s** | 33/33 | 5747 | 0 | 0.000 |
| nopCommerce after | 14.0 s | 93.5 s | **107.5 s** | 33/33 | 5743 | 3 | 0.013 |
| Orchard before | 79.7 s | 152.6 s | **232.3 s** | 227/227 | 2466 | 23 | 0.338 |
| Orchard after | 61.5 s | 139.4 s | **200.9 s** | 227/227 | 2466 | 23 | 0.338 |

This is the first run at real size that the incremental loader makes possible: both repositories used
to reach an hour of CPU time, and both now finish well inside the 20-minute budget with
`loadComplete` true — every one of nopCommerce's 33 and Orchard's 227 projects loaded, none missing.
nopCommerce reports no load diagnostics at all; Orchard reports 24 and still loads every project.

**Coverage after recognizers is below 0.7 in both, and `docs/adr/0013` already says why.** For
nopCommerce the declared `Nop.Core.Caching.IStaticCacheManager` recognizer works — cache operations go
from 0 to 3 and unresolved keys from 3 to 231 — and that jump *is* the finding: the ADR records that an
`IStaticCacheManager` takes a `CacheKey` object built by a key service rather than a string at the call
site, so the recognizer finds the call sites and the key folder, which reads string expressions, can
fold almost none of them. Coverage of 0.013 measures the key folder against object-valued keys, not the
recognizer. For Orchard the declared `OrchardCore.DynamicCache.IDynamicCacheService` recognizer changes
nothing measurable: cache operations stay at 23, unresolved `cache_api` stays at 8 and coverage stays
at 0.338 to the digit, because `GetCachedValueAsync`/`SetCachedValueAsync` are keyed on a `CacheContext`
object and the cache surface the ADR describes is a signal and tag machinery reached through
`ITagRemovedEventHandler`, which this file does not describe. Raising either number means teaching the
tool those APIs, which ADR 0013 rejects on purpose: these repositories were chosen to measure what
happens against an unfamiliar cache API.

One caveat on reading the deltas: edge and unresolved-`call` counts are not stable run to run on
Orchard. Two consecutive `before` runs gave 15252/906 and 15204/888, so the 384-edge difference between
the Orchard rows above is run-to-run variation and not an effect of the recognizer. Vertices, cache
operations and coverage were identical across those repeats, and they are the numbers this section
relies on.
