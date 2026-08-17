# CONTEXT format

Use this document as the durable domain model for the project. Record the vocabulary and measured
facts that the system, its documentation, and future work must share; leave transient discussion
and task-specific plans out.

## Content

- Give each important concept one canonical term. Name the term as it should appear in the
  implementation and documentation, and call out a misleading synonym when it is likely to return.
- Define terms in observable, operational language. Include the role, boundary, or invariant that
  makes the term different from nearby concepts.
- Record design constraints and measured protocol or vendor behaviour when they explain a decision.
  Distinguish measurements from assumptions or proposals.
- Keep entries short enough to scan. Prefer one fact per sentence and link an architectural decision
  when a detailed trade-off belongs there.
- Update an existing entry when its meaning changes; do not create competing definitions for the
  same concept.

## Format

Start with a level-one heading naming the system and the document's purpose. Follow it with a short
paragraph explaining what vocabulary the document governs. Put the core vocabulary in a two-column
Markdown table with `Term` and `Meaning` headers:

```markdown
| Term | Meaning |
|---|---|
| **Canonical term** | Precise operational meaning. |
```

Use level-two headings for constraints or measured facts that need more than one table row. Keep
prose paragraphs short, use Markdown emphasis sparingly, and preserve the existing headings and
table when adding or revising entries.
