# A form of entry point is a finder, not a branch

`CallGraphIndexer.AddTypeEntryPoints` decides, for one type, which of seven kinds of entry point it is —
controller, gRPC service, MediatR-style request handler, event consumer, `BackgroundService`,
`IHostedService`, Quartz-style job — and it decides it as seven consecutive branches in one method.
Adding an eighth means editing that method, which is the one thing the rest of this project is built not
to require: `CONTEXT.md` states the principle as *adding a library means adding a recognizer, never
adding a branch*, and cache stores, cache key objects and event buses all obey it. Entry-point forms are
the only place that does not.

## What the branches are, measured

Four of the seven already pass through one table-shaped helper,
`AddHandlingMethods(type, shapeName, arity, methodName, kind)`:

| Form | Call |
|---|---|
| `IRequestHandler<,>` and `IRequestHandler<>` | `("IRequestHandler", 2 or 1, "Handle", "request_handler")` |
| `IHostedService` | `("IHostedService", 0, "StartAsync", "hosted_service")` |
| `IJob` | `("IJob", 0, "Execute", "job")` |
| `BackgroundService` | `AddMethods(type, "ExecuteAsync", "background_service")`, the base-class case of the same idea |

Three of those four are independent of each other and are a table in every sense. The fourth is not,
and the exception is worth stating because it is the one way a table would have changed the graph:
`BackgroundService` and `IHostedService` are an `if`/`else`, and they have to be, because
`BackgroundService` *implements* `IHostedService`. A table that ran both rows against such a type would
find `ExecuteAsync` **and** `StartAsync` and record two entry points where the branches record one. So
the hosted-service pair expresses an exclusion, which is a computation, and by the rule below it is
therefore its own finder rather than two rows.

A fifth form, the event consumer, is already declarative: `AddEventHandlingMethods` is driven by the
`EventRecognizer` list, so a workspace that declares its own bus in `workspace.json` gets its consumers
recognised as entry points without any code change at all. Only two forms are genuinely different, and
they are different for a reason that shows in their signatures: controllers and gRPC services compute
`HandlerRoute` values, from `[Route]`/`[HttpGet]` attributes and from a `BindServiceMethodAttribute` on
the generated base respectively. Everything else emits `EntryPoint(method, kind, [])`.

So the method is not seven unrelated decisions. It is one four-field table with two exceptions and one
recognizer loop, written as straight-line code.

## The stance this reverses

Nothing in `CacheDetective.Core` or `CacheDetective.Cli` declared an interface, an abstract class or a
virtual member — 63 source files, 14 814 lines, every type `sealed`. Every `interface` in the repository
sits in a test *fixture*, which is C# the tool analyses as input rather than C# the tool is built from.
That was never written down, and this record is what makes it a decision rather than an accident.

The convention was load-bearing in one respect worth keeping: substituting a collaborator in a test is
already solved here, and solved without interfaces. `WorkspaceSession` exposes `OpenCacheReader` as a
settable `Func`, takes a `CatalogueSource` delegate on an internal overload of `IndexDatabaseAsync`, and
takes a `Func<FindingSnapshot, …>` on an internal overload of `VerifyFindingAsync`. Three seams, three
delegates, no interface. Introducing interfaces *for testability* would therefore add a second idiom to
a question this project has already answered.

## Decision

**An interface is introduced when it removes a branch on form, and not to make something testable.**

Under that rule, entry-point discovery gets one: `IEntryPointFinder`, whose implementations replace the
seven branches, so that `AddTypeEntryPoints` becomes a loop over finders and disappears as a decision
site. The declarative half is kept, not replaced: `EntryPointRecognizer` is the four-field record the
table above already implies, and `RecognizerEntryPointFinder` is the single implementation that reads
it. Controllers, gRPC and event consumers become their own implementations, because each carries logic
a record cannot express — two compute routes, one writes `Consumes` edges and `unresolved` rows.

The interface and the record are layers, not rivals, and the layering is the one this project already
runs on elsewhere: `CacheRecognizers.All` is data and `CacheCallAnalyzer` is the code that reads it.
A record can be declared in `workspace.json`; a class cannot. A class can compute a route; a record
cannot. Each form is expressed in whichever of the two it fits.

Also decided, in the negative: the six analyzers `CallGraphIndexer` builds — `CacheCallAnalyzer`,
`EventCallAnalyzer`, `HttpCallAnalyzer`, `EfReadAnalyzer`, `EfWriteAnalyzer`, `SqlAnalyzer` — are **not**
put behind interfaces or a container. Each has exactly one construction site, all six inside
`IndexAsync`, and the 665 test methods reach every one of them through the real `IndexAsync` rather than
around it. There is no second implementation to select and no second consumer to serve, so an interface
there would name a seam that nothing uses. `IndexerOptions` remains the composition root; `Core` takes a
Roslyn `Solution` and has no host and no object lifetimes (`docs/adr/0002`), so it gets no container.

## Consequences

Adding an entry-point form that fits the four fields becomes a row in one of the three lists
`EntryPointTables.Default` holds — one list per position in the finder order, because the order forms
are emitted in is part of what must not move. Adding a form that does not fit becomes a new
`IEntryPointFinder`, a file that no existing file has to be edited to accommodate beyond the list that
names it.

The cost is that "how is an entry point found" is now answered in several files rather than one, and the
order those files run in stops being visible as the order of statements in a method. That order is not
cosmetic: it fixes the order handlers reach the graph, and therefore the ids of `unresolved` rows, and
an annotation binds to an id. The list that names the finders is the whole of that ordering and is
tested as such, rather than being an emergent property of where a branch was written.

Nothing about the graph changes. The finders emit what the branches emitted, in the order the branches
emitted it, and both behaviour snapshots — the demo stand's and eShopOnContainers' — are expected to
compare equal without being re-recorded. A snapshot that has to be re-recorded to accept this change
means the change was not this change.

The rule cuts both ways and is meant to. A future proposal to introduce an interface because a class is
"hard to test in isolation" is refused by this record; one that removes a `switch` or an `if`-chain over
a *kind* of thing is admitted by it.

## Rejected

**A table alone**, with no interface. It reaches three forms of seven and leaves the controller, gRPC,
hosted-service and event-consumer branches where they are, so `AddTypeEntryPoints` still exists, still
has to be edited for any form that computes anything, and still has to be read to learn what a handler
is. Half the stated problem would remain, and it is the half that is hard to read.

**An interface alone**, with each of the three uniform forms as its own class. Three classes differing
only in four literal values is a table written in the most expensive notation available, and it forfeits
the property that makes the recognizer pattern worth having here: a record is serialisable, so the same
shape can later arrive from `workspace.json`, and a class never can.

**Interfaces over the six analyzers, resolved from a container.** Rejected on measurement rather than on
taste: Roslyn `find-references` on `CacheCallAnalyzer`'s constructor returns one call site. A container
in `Core` would also make every one of 665 tests build a service provider to obtain an object that is
constructed once per index.

**Making the finders pure** — returning found entry points and a list of effects, with the caller
applying them — so that the event-consumer finder stops writing to the graph. This is the better design
and is deliberately not taken in the same change as the restructuring, because the moment a `Consumes`
edge is added is part of what fixes `unresolved` ids, and moving both the structure and the write order
at once forfeits the one guarantee this change is being held to.

**Splitting minimal-API discovery into a finder too.** Minimal APIs are found by walking *documents* for
`MapGet`-family invocations, not by walking types, so it would need either a second interface with a
single implementation or a two-method interface that six implementations of seven leave empty. It stays
the loop it already is, in `FindEntryPointsAsync`, until a second document-driven form exists to justify
the abstraction.
