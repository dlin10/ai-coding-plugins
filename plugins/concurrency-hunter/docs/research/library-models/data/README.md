# Raw results

Outputs of the runs in `../../library-models.md`, kept so a claim there can be checked. Labels use the research names:
`di-registration` is SPEC's `di-factory`, `framework-event` is `unknown-execution`.

| File | Content |
|---|---|
| `eshop-gap-callees.txt` | The 142 gap callees of eShop after phase 5b, with counts |
| `members-docs.json`, `ai-input.md` | XML documentation of the gold members and the docs-only AI input built from it |
| `ai-haiku.json`, `ai-sonnet.json` | AI labels from signature and XML documentation |
| `ai-haiku-decomp.json`, `ai-sonnet-decomp.json` | AI labels from the decompiled member and its direct callees |
| `il-verdicts.json` | IL probe events and labels per delegate parameter |
| `generator-verdicts.json` | Hand-written drivers on the 18 members outside ASP.NET Core and EF |
| `synth-gold.json`, `synth-breadth.json` | Synthesized drivers: per member whether it compiled, receiver and `default!` counts, triggers tried, label per parameter (the driver text is left out; see `../prototype/examples/`) |
| `oracle-members-*.json`, `oracle-sonnet-*.json` | The breadth sample's definite labels and a blind Sonnet labeller's reading of the decompiled code |
