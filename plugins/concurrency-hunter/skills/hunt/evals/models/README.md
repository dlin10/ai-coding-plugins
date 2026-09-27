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
