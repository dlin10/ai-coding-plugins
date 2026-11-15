# Two library accesses to an object only the library refers to do not pair

## Context

Analysis at the call site ([ADR 0029](0029-a-deciding-gap-is-answered-by-a-record-the-call-site-or-ai.md)) brings a
library's own bodies into the scope, and with them the library's own state. A library keeps objects the program never
sees — a pool, a cache, the parts of an object it hands out — and synchronizes them by means the analysis does not
prove: Polly.Core 8 takes a `ResilienceContext` from the static `ResilienceContextPool.Shared` with
`Interlocked.CompareExchange`, writes its fields, and returns it; the pool gives a context to one caller at a time. To
the analysis the pooled contexts are one object reachable from a static, written by every request.

Measured on the demo with the bodies of Polly.Core 8.7.0 admitted at every direct call the program makes to them: 278
findings became 1380, and 1100 of the 1102 added ones have both accesses in Polly.Core bodies on objects Polly.Core
bodies allocated — the pooled `ResilienceContext` (864), the `ResilienceProperties` it creates (216), a
`ScheduledTaskExecutor` inside a circuit breaker's state controller (16) and the pool object itself (4); 32 of the 382
distinct findings were High. None is a defect of the program, and none can be fixed in it. The fresh-object rule
([ADR 0034](0034-a-fresh-object-reaches-the-calls-its-invocation-waits-for.md)) does not reach them: it is about an
object its invocation allocated, not one taken from storage.

## Decision

A **library-internal object** is an object a library body allocated during a call-site analysis to which no place in
the program's code refers: no value of a program body — a local, a parameter, the result of a call, the argument a
library hands to the program's delegate — and no field, cell or captured variable of an object the program created or
of the program's own static or injected storage. A reference held by a library-made object or by the library's static
storage does not count. Two accesses both made by library bodies to a library-internal object are not a candidate
pair; the pair is skipped under a reason of its own and counted.

Everything else keeps its pairs:

- an access made by the program's code, whatever it touches — a delegate the library runs is the program's code;
- an object the program created, however the library uses it;
- a library-made object the program holds itself: `_buffer.Append(x)` from two requests on a buffer a factory returned
  still pairs on the buffer's own fields;
- the library's static storage, which is one in the process and which two call sites share
  (ADR 0029, amendment of 2026-11-11);
- an object whose holders the analysis cannot tell — a region merged under a limit or opened for substitution.

The holder test is direct, not transitive: the inside of a library object the program holds is library-internal while
the object is not. The rule proves no synchronization and claims none; it states whose state it is.

Rejected:

- **Withdrawing the call site's answer** when its bodies bring a pair inside the library, so that the call stays the
  gap it was. It hides nothing the analysis shows today, but a library with a pool would then never be answered, and
  every withdrawal costs another analysis of the scope.
- **Unreachable from anything the program holds**, the transitive test. It keeps the pool's pairs away and leaves the
  pairs on the inside of every thread-safe object a library hands out — the circuit breaker's controller here.
- **No holder test**: both accesses in library code on a library-made object. It hides the race of a program that
  shares an object the library does not protect, which is the defect the analysis exists to find.
- **The library's static storage as library-internal.** A static is shared by every call site and by the program's own
  reads of it; its pairs stay until a decision of their own.

## Consequences

- With the bodies of Polly.Core 8 admitted, the pairs on the pooled context, its properties, the pool and the circuit
  breaker's executor go; the findings on the program's own state stay.
- A real race is missed where a program shares an object of a library type that is not safe for it and the race shows
  only on an object nested inside it, never on the shared object's own fields. The matrix of the rule lists that cell
  as its known narrowing.
- Without a call-site analysis there is no library body, so no run without one changes.
- Objects library bodies keep only in library storage no longer pair across two roots that share one call site; the
  tests that observed the place of an admitted call through such pairs observe it through objects the program holds.
- The engine's source changes, and with it the version of the model generator: the demo's generated models are
  re-recorded, and the framework census names a stale engine until its owner regenerates it.
