# Context Map

Each plugin under `plugins/` is its own bounded context with its own vocabulary and its own
decisions. System-wide decisions live in `docs/adr/`.

## Contexts

- [Cache Detective](./plugins/cache-detective/CONTEXT.md) — scans a .NET workspace for cache
  consistency risks: keys, tables, invalidation, runtime verification
- [Plan Forge Flow](./plugins/plan-forge-flow/CONTEXT.md) — hardens an implementation plan through
  review rounds, builds it stepwise through vendor agents, reviews the result
- [Concurrency Hunter](./plugins/concurrency-hunter/CONTEXT.md) — finds data races and lost
  updates on the managed heap of one process, with AI resolving what static analysis cannot
- `plugins/Common/` — adapter code shared by the Roslyn analyzers; it has no vocabulary of its own
  and holds no analysis semantics. See `docs/adr/0001`.

## Relationships

- **Cache Detective ↔ Concurrency Hunter**: both build on `Common.Roslyn` (solution loading) and
  `Common.Mcp` (paged responses). They share no analysis code and no vocabulary: a cache-detective
  **Entry point** is a root of a call-graph walk, a concurrency-hunter **Execution root** is a
  point a concurrent execution starts from; cache-detective's **Unresolved** and **Annotation**
  carry no validation, concurrency-hunter's **Semantic gap** and **Inferred fact** do;
  cache-detective's confidence is `confirmed | likely | unknown`, concurrency-hunter's is a 0–100
  score with an evidence mode. The words are kept apart on purpose.
- **Plan Forge Flow → Concurrency Hunter**: plan-forge-flow's vendor runner is the candidate for a
  future `Common.Agents`, should the concurrency-hunter server one day launch vendor sessions
  itself. Until then plan-forge-flow shares only the umbrella solution and the package versions.
