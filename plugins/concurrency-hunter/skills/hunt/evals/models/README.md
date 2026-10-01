# Model evals

The gold set the model evals of SPEC 12.1 start from: 49 library members — the callees of eShop's semantic gaps after
phase 5b — and their 52 delegate parameters, each labelled with the delegate's fate and verified against the library's
source at eShop's version.

- `gold.json`: per member the declaration id in its XML-documentation form (no `~ReturnType`), the assembly and version,
  the delegate parameters, and the label. Labels use the research names: `di-registration` is SPEC's `di-factory`,
  `framework-event` is `unknown-execution`; `non-delegate` marks the three members that take no delegate.
- `source-verdicts.json`: per member, in the same order, the label reached by reading the implementation, the URL of the
  source read at the exact version, the call chain followed, and notes on timing and lifetime.

The bar is zero unsafe narrowings — a generated fate less exposed than the gold one. The members of ASP.NET Core and EF
Core are verified but not yet measurable in the engine fixture, which stubs those frameworks (question 41 of
`demo/SCENARIOS.md`). How the set was built and what it measured: `docs/research/library-models.md`, until phase 5e closes.

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
  result, and `RepeatedField<T>.Add` keeps on `this` although TD-034a still says a member that changes a library object's
  state has no model.

The bar is the same: a generated model narrower than `safeModel` on any axis is an unsafe narrowing, and a member whose
`safeModel` is `opaque` must stay opaque.
