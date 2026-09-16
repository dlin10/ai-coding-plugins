# Composing narratives

Write an interpretation of the deterministic evidence rather than restating its text. Evidence ids
use this convention for finding `Fk`: `[E:Fk.A]` and `[E:Fk.B]` are the two accesses,
`[E:Fk.R]` is the resource, `[E:Fk.O]` is execution overlap, `[E:Fk.P]` is protection, and
`[E:Fk.S]` is the scenario.

## Reading the evidence

- **`Fk.R`, the resource.** It names the field, the region that holds it, the process scope and the
  binding evidence. A `static:<Type>` region is a static field: one object for the whole process. A
  `di:<Type>@<Lifetime>` region is an object the dependency injection container creates, such as
  `di:Ledger@Singleton`; the text says which service type it was registered as and gives the
  registration and constructor-assignment locations that prove the object reaches the access. Two
  accesses pair only inside one process scope, which is an executable project and the projects it
  loads, so a static field shared by two applications is two objects and never one finding. An
  `alloc:<method>#<Type>` region is an object created with `new` inside that method, such as
  `alloc:SlotWorker.ExecuteAsync(CancellationToken)#Slot`; a trailing `#2` only tells two creation
  sites of one type in that method apart. When you name the region's type in backticks, write the type
  alone, `Ledger` or `Slot`, not `di:`, `static:` or `alloc:`, not the `@Singleton` suffix, and not the
  creating method or the `#` parts.
  The resource text also gives the region's ownership and the evidence for it: `Shared` (reachable
  from more than one execution, such as a singleton or static storage), `Escaped` (the object was
  stored somewhere another execution can reach it, such as a static field, a singleton's field or an
  array), `ThreadConfined` (reached only by one execution, such as a scoped service within one request),
  `Owned` (never reached by any access) or `Unknown` (the analysis could not tell, for example after
  contexts were merged). Explain a finding on an `alloc:` object through that evidence: say how the
  object escaped rather than just naming the region.
  A resource whose path is `*` is a wildcard: an access path deeper than the analysis follows was
  collapsed onto the region it starts from, so the finding stands for some field reached through that
  path. Such a finding is labeled Medium because the exact field is not identified; say so in the
  uncertainty section.
- **`Fk.O`, the overlap.** It says either that one root may run concurrently with itself, for example
  an HTTP action serving two requests at once, or that two different roots may run concurrently in the
  scope, for example an action and a hosted service. Two lifecycle methods of one hosted service, such
  as `ExecuteAsync` and `StopAsync`, pair because the host can stop the service while it is still
  executing; their relative order is not analyzed, and the finding's uncertainty says so.
  A root may also be a construction execution, displayed as `construction of <region>` or as a type
  initializer: a lazily resolved singleton is constructed the first time something resolves it, and
  a type initializer runs the first time its type is used. Either runs at most once, so it never
  overlaps itself, but it may run while requests or workers are running, so it pairs with them.
  Accesses a constructor makes to the object it is still building do not pair unless that object has
  already been published.
- **Code paths.** Each access's code path starts at its root and lists the steps that lead to the
  access: `constructs <region>` for a construction, `calls <method> on <region>` for every call
  between the root and the method that touches the field, `acquires <lock>` for a lock held there, and
  the access itself. Use the `call` steps to explain how a request or a worker reaches a field several
  layers down; the called methods are accepted as identifiers like access symbols.
- **`Fk.P`, the protection.** It gives the result (`unprotected`, `partial` when only one side holds a
  lock, `different-identity` when both hold locks but not a common one) and what A and B each hold. A
  lock marked as not one object per process, such as a lock on a per-request field or on `this` in a
  controller, is a different object in every invocation, so it excludes nothing between two requests
  and protects nothing; say so rather than calling it a lock that failed. Name a held lock the way you
  name a region type, without its prefix, suffix or note.
- **`DCA1002`, a lost update.** One side is a read-modify-write, such as `Hits++` or `Total += n`: it
  reads the value, computes a new one and writes it back. When the other side writes in between, the
  write-back overwrites that update and it is lost. The read and the write need not be in one
  method: in `SetLevel(GetLevel() + 1)` the read is in `GetLevel` and the write in `SetLevel`. The
  access is reported at the write, and the access evidence adds `reads it at <location> in <method>`
  for each read it depends on; explain the lost update across both methods and cite that read location.
  `DCA1001` is any other unsynchronized write paired with a read or a write.

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
  paths with spaces. It must match whole path segments at the end of an access evidence path or of a
  read location of a read-modify-write, ignoring case; when present, the line must lie inside that
  evidence span. Do not mention any `.cs` location outside backticks.
- Backticked identifiers are checked, including Unicode names and verbatim `@` identifiers on every
  segment. Use only an exact evidence access symbol, root entry symbol, called method of a `call` step
  or method of a read location; a dot-suffix of any of them without its parameters; the evidenced
  region type without its `static:`, `di:` or `alloc:<method>#` prefix and its `@<Lifetime>` or `#<n>`
  suffix; the field; a held protection name without that prefix, suffix or its trailing note; an
  evidence id; `DCA1001` through `DCA1004`; or the synchronization vocabulary below. The lifetime on
  its own, such as `Singleton`, and the creating method of an `alloc:` region, are not accepted.
  Backticked snippets that do not match the identifier pattern are not checked.
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
