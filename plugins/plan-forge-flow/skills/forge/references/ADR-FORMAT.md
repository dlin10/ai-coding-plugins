# ADR format

Use an architectural decision record for a durable decision that is hard to reverse, surprising
without context, or involves a real trade-off. Do not turn every implementation choice or interview
answer into an ADR; keep ordinary details in the plan or the domain model.

## Content

Explain the problem and forces that made a decision necessary. Record the chosen decision, the
alternatives considered, and the consequences, including costs, constraints, and deliberate things
the system does not guarantee. State evidence or measurements when they support the decision, and
link related decisions when one supersedes or limits another.

An ADR must stand on its own for a reader who was not in the interview. Prefer concrete examples
over slogans, and make the boundary of the decision explicit without inventing rules about where
the document is kept.

## Format

Start with a level-one heading containing a concise imperative or descriptive decision title. Use
short paragraphs and level-two headings for:

- `Context` — the problem, forces, evidence, and relevant constraints;
- `Decision` — what is chosen and what alternatives were rejected;
- `Consequences` — benefits, costs, risks, and intentionally un-enforced behaviour.

If a decision replaces another one, say so near the title and link the related record. Use Markdown
links for references, fenced blocks only for exact data or commands, and keep the record focused on
one decision.

Security note: a file name containing `secret`, `token`, `password`, or `credential` is matched by
the sensitive-path regex. Excluding that path from the prompt is not sufficient because workers can
read the workspace; the complete changed-path guard therefore still refuses it.
