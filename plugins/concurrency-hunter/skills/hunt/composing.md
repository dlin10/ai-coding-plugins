# Composing narratives

Write an interpretation of the deterministic evidence rather than restating its text. Evidence ids
use this convention for finding `Fk`: `[E:Fk.A]` and `[E:Fk.B]` are the two accesses,
`[E:Fk.R]` is the resource, `[E:Fk.O]` is execution overlap, `[E:Fk.P]` is protection, and
`[E:Fk.S]` is the scenario.

## Group narrative

Use these `###` headings in this exact order, and support each section with the relevant evidence
ids:

1. `### What can be lost`
2. `### Why the accesses overlap`
3. `### Why the protection is insufficient`
4. `### Interleaving`
5. `### Evidence mode`
6. `### Uncertainty`
7. `### Remediation`

Follow every validation rule:

- Submit nonempty text of at most 8192 UTF-8 bytes.
- A group narrative must contain at least one `[E:<id>]` citation. Cite only evidence ids belonging
  to a finding in that group, and cite at least one evidence id of every finding `get_groups` listed
  for the group. A summary may cite evidence from any group in the run.
- Include a heading named `Remediation`, written as a level 1 through 6 Markdown heading. Put at
  least one recommendation below it. Each recommendation must start at the beginning of a line with
  `-`, `*`, or a number followed by a period.
- Every recommendation must contain the words `verify manually`, ignoring case, and must contain an
  indented bullet or numbered item beginning `Check:` followed by a nonempty check.
- Put every source location inside backticks. A location is `File.cs` or `File.cs:line`, including
  paths with spaces. It must match whole path segments at the end of an access evidence path,
  ignoring case; when present, the line must lie inside that access's evidence span. Do not mention
  any `.cs` location outside backticks.
- Backticked identifiers are checked, including Unicode names and verbatim `@` identifiers on every
  segment. Use only an exact evidence access symbol; a dot-suffix of an evidence symbol without its
  parameters; the evidenced region type, field, or held protection; an evidence id; `DCA1001`
  through `DCA1004`; or the synchronization vocabulary below. Backticked snippets that do not match
  the identifier pattern are not checked.
- An identifier is also accepted when its first segment, before `.` or `(`, is in this vocabulary:
  `lock`, `Monitor`, `Interlocked`, `Volatile`, `volatile`, `SemaphoreSlim`,
  `ReaderWriterLockSlim`, `Lock`, `Mutex`, `ConcurrentDictionary`, `ConcurrentQueue`,
  `ConcurrentBag`, `ConcurrentStack`, `ImmutableInterlocked`, `ThreadLocal`, `AsyncLocal`,
  `readonly`, `static`, `const`, `async`, `await`, or `Task`.
- Keep citations, locations, and identifiers grounded in the supplied group. Do not invent them and
  do not copy the evidence prose back into the narrative.

Offer remediation in the categories that fit the finding: synchronization or atomicity, ownership
or state organization, and execution order. Make recommendations specific enough for their nested
`Check:` items to be verified in the application code.

Example of a valid group narrative for a group whose only finding is `F1`:

```markdown
### What can be lost
One request can replace another request's update to `_lastVisitor` before it is observed [E:F1.S].

### Why the accesses overlap
The action roots can execute for different requests at the same time [E:F1.O].

### Why the protection is insufficient
The accesses do not share an evidenced guard [E:F1.P].

### Interleaving
The harmful ordering is a write from one request between the other request's access and use of the value [E:F1.A] [E:F1.B] [E:F1.S].

### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence [E:F1.R].

### Uncertainty
Path feasibility is not established, so confirm that both paths can execute in the same deployment [E:F1.S].

### Remediation
- Guard every access to `_lastVisitor` with the same `lock`; verify manually [E:F1.P].
  - Check: confirm every read and write uses that one guard [E:F1.A] [E:F1.B].
```

## Executive summary

Write a short paragraph citing evidence ids from any group, for example: `One group of unprotected
static state can lose updates across concurrent requests [E:F1.R] [E:F1.O].` A summary does not
need a `Remediation` section. If it includes one, every recommendation in that section still needs
`verify manually` and an indented `Check:` item. The nonempty, 8192-byte, citation-grounding,
location, and identifier rules still apply.
