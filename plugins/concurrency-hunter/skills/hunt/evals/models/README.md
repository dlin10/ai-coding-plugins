# Model evals

The gold set the model evals of SPEC 12.1 start from: 49 library members — the callees of eShop's semantic gaps after
phase 5b — and their 52 delegate parameters, each labelled with the delegate's fate and verified against the library's
source at eShop's version.

- `gold.json`: per member the declaration id in its XML-documentation form (no `~ReturnType`), the assembly and version,
  the delegate parameters, and the label. Labels use the research names: `di-registration` is SPEC's `di-factory`,
  `framework-event` is `unknown-execution`; `non-delegate` marks the three members that take no delegate. A `holder`
  label carries its holder kind: the five holder entries — `WaitAndRetry`, `WaitAndRetryAsync`,
  `WaitAndRetryForeverAsync`, the `MessageParser<T>` constructor and `FieldCodec.ForMessage` — have
  `"holder": "result"`, held by the returned or the constructed object, as their source verdicts say.
- `source-verdicts.json`: per member, in the same order, the label reached by reading the implementation, the URL of the
  source read at the exact version, the call chain followed, and notes on timing and lifetime.

The bar is zero unsafe narrowings — a generated fate less exposed than the gold one. The members of ASP.NET Core and EF
Core are verified but not yet measurable in the engine fixture, which stubs those frameworks (question 95 of
`demo/SCENARIOS.md`). How the set was built and what it measured: `docs/research/library-models.md`, until phase 5e closes.

## Generator evals

The model generator (SPEC TD-034b) runs on the 18 fate-gold members of four assemblies and their 24
gold parameters — the delegate parameters of the 18, and `ForMessage`'s `parser`, classified from the probe its value
carries. The gold files carry no package ids or platforms, so the evals ask for them as this table says:

| Gold members | Assembly | Version | Package | Platform |
|---|---|---|---|---|
| the 10 of `System.Linq` | `System.Linq` | `8.0` | — | `net8.0` |
| `ClaimsPrincipal.FindFirst` | `System.Security.Claims` | `8.0` | — | `net8.0` |
| the 5 of Polly | `Polly` | `7.2.3` | `Polly` | `net8.0` |
| the 2 of Google.Protobuf | `Google.Protobuf` | `3.21.9` | `Google.Protobuf` | `net8.0` |

- **Comparator.** A classified fate is safe when it is the gold fate with the gold holder kind, `unknown-execution`, or
  `holder` with holder `result` where the gold is `iterator`; anything else is an unsafe narrowing, a `holder` of the
  wrong kind included. A member the generator did not classify counts as `unknown-execution` for each gold parameter.
  The bar is zero unsafe narrowings. Every generated entry passes the project byte reader and its member check,
  and its fates equal the classified fates of the delegate-typed parameters. `ForMessage`'s carried `parser` has
  no fate in an entry: it is a non-delegate argument.
- **Floor.** At least 14 of the 24 gold parameters classified exactly — or, while fewer are, no fewer than the `exact`
  the snapshot recorded, each inexact parameter named with its cause in `demo/SCENARIOS.md`. The snapshot records 11:
  a holder stands only when a trigger calls every member a caller could invoke on it, within the driver's 24, and no
  gold holder is covered (questions 99 and 101). The inexact ones are in questions 97–101.
- **Entries.** The fate gold set is truth for fates only. Its whole answers are recorded, and the number of members
  with an entry never falls below the snapshot's `fateGoldEntries`; each member with no entry is named with its
  model reason in `demo/SCENARIOS.md`.
  Run B2 records 3 entries of 18, `effectsExact` 8 of 18 (exact opaque answers), and `linqExact`
  5 of 114 `System.Linq` entries and 0 of 15 `System.Linq.Queryable` entries. A refusal can be an exact whole
  answer only when the truth requires the member to stay opaque. These counts do not make the fate gold set
  whole-entry truth.
  The fate floor may be re-recorded only for parameters whose fate a seed or a witness changed; every such
  parameter and declaration id must be named with that cause in `demo/SCENARIOS.md`. Run B2 changes no recorded
  fate. The effects and LINQ exact-entry floors and the fate-gold entry floor cannot decrease.
- **Snapshot.** `generator-snapshot.json` holds the `members`, `effects` and `linq` answer arrays, sorted by declaration id:
  the implementation identity (assembly version, file version, MVID), the classification or the reason, the whole
  `model` entry or null, `modelReason`, and the bodies the driver reached; per
  implementation assembly the package, the version folder the resolver took, the method bodies of the decompiled
  compilation and those made `extern`, the gold members and the drivers synthesized. The counts are `exact` for
  fates, `fateGoldEntries`, `effectsExact`, and `linqExact` with separate `System.Linq` and `System.Linq.Queryable`
  counts. Every eval run
  compares a fresh snapshot with it and reports an implementation identity change (a runtime or package update) before
  any answer difference. It is rewritten only on purpose, by running the evals with `CH_MODEL_EVALS_RECORD=1`.
- **Flag.** The evals (`ModelEvalTests`, `EffectsEvalTests`, `LinqOracleTests`) share the serial `Model evals` collection
  and read the installed .NET 8 and .NET 10 shared frameworks and the NuGet global packages
  folder, so they run only with `CH_MODEL_EVALS=1` and skip otherwise; under the flag a missing package, shared
  framework or gold file fails them. The test baseline is recorded without the flag.

To record, from the plugin root in PowerShell:

```powershell
$env:CH_MODEL_EVALS = '1'; $env:CH_MODEL_EVALS_RECORD = '1'
dotnet test src/ConcurrencyHunter.Core.Tests --filter "Category!=Integration&(FullyQualifiedName~.ModelEvalTests.|FullyQualifiedName~.EffectsEvalTests.|FullyQualifiedName~.LinqOracleTests.)" *> $env:TEMP\ch-model-evals.log
$evalExit = $LASTEXITCODE
Get-Content $env:TEMP\ch-model-evals.log -Tail 50
Remove-Item Env:CH_MODEL_EVALS, Env:CH_MODEL_EVALS_RECORD
if ($evalExit -ne 0) { throw "Model evals failed ($evalExit)" }
```

## Effects

The effect set (2026-10-01): 18 members that take an argument able to hold a user object and are no delegate — the opaque
callees of eShop and the demo outside ASP.NET Core and EF Core, with AutoMapper and FluentValidation from nopCommerce, plus
`EqualityComparer<T>.Equals` — each read in the decompiled implementation of the version its corpus uses.

- `effects-gold.json`: per member the declaration id, the implementation and reference assembly, the version, the corpus,
  per argument what the member reads, writes, keeps and which user code it runs, the result, and `safeModel`: the narrowest
  model in the TD-034a vocabulary no narrower than that on any axis, taking `reads-deep` for user getters, converters,
  `ToString` and `Equals` as the built-in serializer and logger models do; `opaque` where the vocabulary cannot say it.
- `effects-source-verdicts.json`: the labels of a second reader who did not see the first, in the same order, with the
  chain of calls followed. Line numbers refer to the ICSharpCode.Decompiler 9.1 output of the assembly named in `evidence`,
  one file per type (`System.Private.CoreLib__<Type>.cs`) or per assembly. Its `safeModel` matches the gold one for every
  member; where the two readers first differed, the owner chose: `SetCount` stays opaque, `WaitAndRetry` keeps in its
  result, and `RepeatedField<T>.Add` keeps on `this`: TD-034a, amended in phase 5d run B1, describes a member whose only
  change to a library object's state is keeping what it was handed with `keeps`.

The bar is the same: a generated model narrower than `safeModel` on any axis is an unsafe narrowing, and a member whose
`safeModel` is `opaque` must stay opaque.

`EffectsEvalTests` uses `EntryComparator` on every whole answer. The input table is:

| Assembly | Version | Package | Platform |
|---|---|---|---|
| `System.Console`, `System.Text.Json`, `System.Threading.Channels` | `10.0` | — | `net10.0` |
| `System.Private.CoreLib` (six members) | `10.0` | — | `net10.0` |
| `Google.Protobuf` | `3.21.9` | `Google.Protobuf` | `net8.0` |
| `Polly` | `7.2.3` | `Polly` | `net8.0` |
| `AutoMapper` | `14.0.0` | `AutoMapper` | `net8.0` |
| `FluentValidation` | `11.5.1` | `FluentValidation` | `net8.0` |

The six CoreLib members answer `corelib`, with no entry. The number of exact whole answers never falls below
`effectsExact`; every inexact answer is named with its cause in `demo/SCENARIOS.md`.

## LINQ oracle

`LinqOracleTests` generates all 129 entries of `BuiltIn/linq.json` against the installed .NET 10 framework:
114 `Enumerable` entries in `System.Linq` and 15 `Queryable` entries in `System.Linq.Queryable`, as each entry's
`assemblies` names. Every member resolves in its own implementation assembly: neither `member-not-found` nor
`no-implementation` is allowed. Any other refusal counts as an inexact answer.

Every whole answer is compared with the built-in entry by `EntryComparator`, with zero unsafe narrowings.
The exact counts are reported separately for `Enumerable` and `Queryable`, and never fall below the snapshot's
`linqExact` counts. Every inexact answer is named with its cause in `demo/SCENARIOS.md`. This oracle reads the
built-in models without changing their behaviour in a run.
