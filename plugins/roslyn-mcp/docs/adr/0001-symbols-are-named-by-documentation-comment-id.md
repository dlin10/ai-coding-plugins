# Symbols are named by documentation comment ID

Every symbol-level tool took only a **position**, so a client that knew which symbol it wanted still
had to call `roslyn_search_symbols` for a line and column first, and a position drifts as soon as the
file is edited. The tools now also accept a **symbol ID**, and that ID is the documentation comment
ID Roslyn already defines (`M:Ns.Type.Method(System.Int32)`), built and resolved through
`DocumentationCommentId`. Every result that reports a symbol carries its ID, so a client copies an ID
out of a result rather than composing one.

## Considered Options

- **A fully-qualified display name** such as `Ns.Type.Method(int)` reads better, but it is ambiguous
  across overloads and generic arities, and accepting it means owning a parser and a grammar for
  parameter types, generics and nested types that Roslyn does not provide.
- **Both forms**, the display name resolved by search and rejected with a candidate list when
  ambiguous, gives two resolution paths and makes a request's meaning depend on what else the
  solution happens to declare.

## Consequences

- Locals and parameters have no documentation comment ID, so a **position** remains the only way to
  name them, and every tool keeps accepting one.
- An ID resolves inside one compilation, not a solution, which is why a request resolves it in a
  **project context** and every response names the one it used.
- `roslyn_go_to_definition` stays position-only: it answers what the identifier at a position refers
  to, and where a symbol named by ID is declared is already what `roslyn_get_symbol_info` returns.
