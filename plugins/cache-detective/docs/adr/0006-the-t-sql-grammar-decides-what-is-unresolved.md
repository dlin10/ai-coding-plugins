# The parse tree, not a heuristic, decides what is unresolved

Phase 1 sent every Dapper, ADO.NET and EF raw-SQL call to `unresolved` without looking at the
string. Phase 2 folds the string and parses it, which raises a question phase 1 never had to
answer: a folded SQL string is often *partly* known, and something has to decide whether the
known part is enough.

`"SELECT * FROM dbo.Products WHERE Id = " + id` names its table perfectly well. `$"SELECT * FROM
{table}"` names nothing. Between them sits a spectrum, and the obvious implementation is a
heuristic over the string — count the literal segments, check whether an unknown sits adjacent to
a `FROM`, score the result. Every version of that heuristic is a second, worse T-SQL parser
written by us.

So the unknown fragments are replaced with a syntactically neutral parameter — `@__cd_p0`,
`@__cd_p1` — and `ScriptDom` parses the result. What decides is then **where each substituted
parameter landed in the parse tree**:

- in a position that determines a graph vertex — a table source, an `EXEC` target, a schema
  qualifier — the statement goes to `unresolved`, naming the position;
- anywhere else, it was a value, and the tables extracted from the statement stand.

Position is what the question was always about, and the tree reports it exactly. No heuristic over
the string survives.

## Why a failed parse is not the test

The first version of this decision said the parser would simply reject the bad cases, and that was
wrong. `SELECT * FROM @__cd_p0` is legal T-SQL — `@p` in a table source is a table variable — so an
unknown table name parses with zero errors. Relying on a parse failure would have silently
extracted no tables and reported no problem, which is the one outcome this project refuses.

A parse failure does still happen — `FROM @__cd_p0.Products` is not legal, because a variable
cannot qualify a schema — and when it does, the call goes to `unresolved` carrying the parser's own
first error. But it is the weaker of the two tests, not the mechanism.

## Columns are not distinguishable, and do not need to be

In an expression, a column reference and a value are grammatically interchangeable: `WHERE
@__cd_p0 = 5` parses, and nothing in the tree says whether the author wrote a column name or a
value. That is acceptable, and it is why the rule above names only tables, schemas and procedures.
The graph has no column vertices at all — a key's dependencies are tables — so an unknown column
cannot change a single vertex or edge. Refusing to extract tables because a *column* was unknown
would discard real information to protect nothing.

## Confidence

Edges built through a substituted string are `confirmed`, not `likely`. The substitution replaced
a value, and a parameter's value cannot change which table a statement touches. Had the unknown
occupied a table, schema or procedure position, the check above would have sent the statement to
`unresolved` and there would be no edge to grade.

## Cost

A pathological string can parse into something the application would never execute: two
half-statements concatenated may join into one valid statement whose tables are real but whose
shape no runtime sees. Accepted — the failure mode is an extra table in a chain, visible to a
reader from the evidence, and far rarer than the concatenated-value case the substitution exists
to rescue.

Rejected: requiring a fully literal string — it discards the concatenated-value case, which is the
single most common shape of hand-written SQL in the corpora this tool targets, and would leave
phase 2 barely better than phase 1. Rejected: scoring known segments with our own heuristic — it is
a parser, it will disagree with the real parser, and the disagreements will be silent.
