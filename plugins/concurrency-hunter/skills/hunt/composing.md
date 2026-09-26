# Composing narratives

Write an interpretation of the deterministic evidence rather than restating its text. Evidence ids
use this convention for finding `Fk`: `[E:Fk.A]` and `[E:Fk.B]` are the two accesses,
`[E:Fk.R]` is the resource, `[E:Fk.O]` is execution overlap, `[E:Fk.P]` is protection,
`[E:Fk.S]` is the scenario, and `[E:Fk.SP1]`, `[E:Fk.SP2]`, … are the spawn sites its listed
occurrences pass through.

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

## Calls the analysis cannot follow

- **An `unknown-effect` access.** A call the analysis cannot follow — a library member its table does
  not describe, an interface call with no known implementation object, a dynamic operation — is
  reported as an access with the operation `unknown-effect` (or `atomic-unknown-effect` on a
  thread-safe collection), at the call site and under the member that makes the call. It says what the
  call *may* do with the resource: read it and write it, or leave it alone. Never say the call wrote
  the field; say that it may, and that nothing the analysis can read rules it out. Such a pair
  conflicts as a write does, so the rule is `DCA1001`, or `DCA1002` against a read-modify-write, or
  `DCA1004` against a check-then-act sequence on a thread-safe collection. An object that only one
  execution uses is only read by such a call, so it never yields one of these findings; the resource
  of one is shared.
- **An unknown call of a delegate.** A root displayed as `unknown call of the delegate ...` is the
  body of a lambda or method handed to such a call. The call may run it at any time and any number of
  times, so it overlaps the other roots of the scope; nothing orders it, not even startup, and the locks
  held where it was handed over do not protect its body. The access symbol is still the member the
  lambda is written in. What the lambda captured of one request or worker stays that execution's; only
  shared state meets other roots. Say that the library may invoke the delegate concurrently, not that it
  does.
- **A semantic gap in the uncertainty.** An item reading `The unresolved call <callee> (<kind>) decides
  the <component> check: ...` means that call decides whether the finding holds: its unknown effect is
  one side (the operation check), the delegate it was handed runs one side or a task, timer or lock it
  returns orders or protects the pair (the overlap or protection check). That component costs confidence and the occurrence
  it decides is at most Medium; a finding keeps the label of its best occurrence, so one reached another
  way that no gap decides may still be High, and the uncertainty lists the gaps of every occurrence. Take the
  label as the evidence gives it, never lower it yourself. Put it in the uncertainty section and tell the reader what to check in that
  library's documentation. You may name the call in backticks by its type and member without type
  arguments or parameters, such as `CollectionsMarshal.SetCount`.
- **Collections.** `HashSet`, `Queue`, `Stack` and `LinkedList` are modelled as `List` and `Dictionary`
  are: a structure resource (the collection itself) and a cell resource (`Items.[?]`), none of them
  thread-safe. Add, Enqueue, Push, AddFirst, AddLast, AddBefore, AddAfter, Dequeue, Pop, RemoveFirst,
  RemoveLast and Clear change the structure and a cell; Count reads the structure, Peek the structure
  and a cell. A `LinkedListNode` is a cell of its list: setting its Value writes that cell without
  changing the structure, so it never races with Count. In backticks, name such a member with its
  collection type, as `Queue.Enqueue` or `LinkedListNode.Value`; the member alone is rejected.

## Occurrences through spawns and timers

An occurrence path can pass a `spawn:<API>@<member>` segment, where work such as `Task.Run` or a thread
started, or a `timer-callback:<timer type>@<member>` segment, where a timer's callback runs; a root of
`startup` means the work was started while the host was starting. Each such site is a `spawn` evidence
item `Fk.SPn` naming the API, the member and the `File.cs:line` of the site, which you may cite and put
in backticks. The analysis already removed every pair whose order it proved with its happens-before
graph (a proven join such as `await` or `Wait()` on the same task, a `Parallel.For` return, a continuation
after its antecedent, startup before requests), so a reported pair is one that order did not rule out.
Never claim a join, an `await`, a wait or a disposal that orders the two accesses unless the evidence
shows it; say instead that no proven join separates them.

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
- Each finding is one pair of access sites; its `occurrenceCount` says how many pairs of roots reach
  that pair. Occurrences are the ways one race is reached; never present them as separate findings.
- Include a heading named `Remediation`, written as a level 1 through 6 Markdown heading. Put at
  least one recommendation below it. Each recommendation must start at the beginning of a line with
  `-`, `*`, or a number followed by a period.
- Every recommendation must contain the words `verify manually`, ignoring case, and must contain an
  indented bullet or numbered item beginning `Check:` followed by a nonempty check.
- Put every source location inside backticks. A location is `File.cs` or `File.cs:line`, including
  paths with spaces. It must match whole path segments at the end of an access evidence path, of a
  read location of a read-modify-write or of a spawn site of a `spawn` evidence item, ignoring case;
  when present, the line must lie inside that evidence span. Do not mention any `.cs` location outside backticks.
- Backticked identifiers are checked, including Unicode names and verbatim `@` identifiers on every
  segment. Use only an exact evidence access symbol, root entry symbol, called method of a `call` step
  or method of a read location; a dot-suffix of any of them without its parameters; the evidenced
  region type without its `static:`, `di:` or `alloc:<method>#` prefix and its `@<Lifetime>` or `#<n>`
  suffix; the field; a held protection name without that prefix, suffix or its trailing note; the callee
  of a semantic gap the finding's uncertainty names, without type arguments or parameters, or a
  dot-suffix of it; an evidence id; `DCA1001` through `DCA1004`; the synchronization vocabulary below; or a reserved C# keyword. The lifetime on
  its own, such as `Singleton`, and the creating method of an `alloc:` region, are not accepted.
  Nor is a type name on its own unless it is the region type, a C# keyword, or a framework member the
  evidence does not name: a worker, controller or service is named by its member, a dot-suffix of the
  root entry symbol such as `OrderWorker.ExecuteAsync`, never by the class alone, and any other word
  stays outside backticks.
  Backticked snippets that do not match the identifier pattern are not checked.
- An identifier is also accepted when its first segment, before `.` or `(`, is in this vocabulary:
  `lock`, `Monitor`, `Interlocked`, `Volatile`, `volatile`, `SemaphoreSlim`,
  `ReaderWriterLockSlim`, `Lock`, `Mutex`, `ConcurrentDictionary`, `ConcurrentQueue`,
  `ConcurrentBag`, `ConcurrentStack`, `ImmutableInterlocked`, `ThreadLocal`, `AsyncLocal`,
  `readonly`, `static`, `const`, `async`, `await`, `Task`, `Dictionary`, `List`, `HashSet`, `Queue`,
  `Stack`, `LinkedList`, `LinkedListNode`, or the protection verdicts `unprotected`, `partial` and
  `sufficient`.
- A reserved C# keyword, the literals `true`, `false` and `null` among them, is accepted when it is the
  whole backticked snippet, such as `finally`, `catch`, `ref`, `out` or `this`. Anything longer is checked
  as an identifier: a keyword followed by a member is accepted only when that member would be, and a
  verbatim identifier with `@` names a symbol. Contextual keywords such as value, var or yield are not
  keywords here.
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
