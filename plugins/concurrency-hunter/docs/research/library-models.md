# Library models — research notes for phases 5c–5e

Evidence, recipes and pitfalls from the research of 2026-09-27 that produced
[ADR 0012](../adr/0012-library-semantics-are-data-the-analysis-derives-from-decompiled-code.md). The decisions live in
ADR 0012, SPEC TD-034a–TD-034c and the 5c–5g rows of [PLAN](../PLAN.md); this file is what those do not carry: what was measured,
how the prototypes worked, and where they went wrong. Read it before planning or implementing 5c (models and fates),
5d (generator and drivers) or 5e (AI models).

This file and `library-models/` are temporary. Delete both when phase 5e closes, once their content lives in code, tests
and the SPEC. The gold set in `skills/hunt/evals/models/` is not temporary: it is the seed of the model evals (SPEC 12.1).

| What | Where |
|---|---|
| Gold set: 49 library members behind eShop's gaps, 52 delegate parameters, labelled and verified against the libraries' sources | `skills/hunt/evals/models/gold.json`, evidence per member in `source-verdicts.json` |
| Prototypes: IL probe, decompiler harness, compile check, engine probe with the generator, the driver synthesizer and the defect repros | `library-models/prototype/` — how to rebuild them is in its `README.md` |
| Three synthesized drivers, as the synthesizer wrote them | `library-models/prototype/examples/` |
| Raw results of every run below | `library-models/data/` |

## What was measured

eShop after phase 5b: 652 known calls, 2226 opaque calls, 142 semantic gaps. 132 of the gaps are callees that take a
delegate; 7 more are protobuf's `FieldCodec.ForMessage`, whose parser holds one. By behaviour: LINQ with lambdas (~47),
host and options callbacks (~50), protobuf parsers (~27), Polly (~11), Serilog (3). The callee list is
`data/eshop-gap-callees.txt`.

Labels on the gold set (52 parameters). An unsafe narrowing is a label less exposed than the truth — the only error that
hides a race:

| Method | Exact | Unsafe |
|---|---|---|
| IL probe, labels as produced | 34 | 6 |
| IL probe, sound reading (only "ran before return" and "returned in a sequence" narrow) | 23 | 0 |
| Haiku, signature + XML doc | 43 | 8 |
| Sonnet, signature + XML doc | 50 | 1 |
| Haiku, decompiled member + direct callees | 46 | 4 |
| Sonnet, decompiled member + direct callees | 51 | 0 |

The generator on the 18 gold members outside ASP.NET Core and EF (24 parameters): hand-written drivers 14 exact, IL
probe 14, their union 22, synthesized drivers 14 — unsafe 0 in all four. Synthesized drivers over a breadth sample
(protobuf 3, `System.Security.Claims` 8, Polly 60 of 594, `System.Linq.Enumerable` 60 of 111 members with a delegate
parameter): 131 of 131 drivers compile, no receiver left null, 21 drivers with a `default!` argument (comparers, Polly's
cache-provider interfaces, `BinaryReader`), 130 engine runs finish, 131 of 183 parameters get a definite label, and a
blind Sonnet labeller reading the decompiled code agrees on 125 of 131.

Cost, for planning the budget of TD-034c: whole-module decompilation of Polly (1.1 MB of C#) 4 s, protobuf 5 s, EF Core
8.0 (9.2 MB) 15 s; the decompiled code compiles with 100% (Polly, `Microsoft.AspNetCore.Cors`,
`Microsoft.Extensions.Options`), 99.9% (protobuf) and 99.6% (EF Core) of method bodies free of errors. One synthesized
driver takes about 1 s for a LINQ member and 5 s for a Polly member with its trigger actions; the breadth sample took
6.6 minutes. The engine on one decompiled Polly call reached about 150 bodies against 7 with Polly as metadata.

## Rules the measurements forced

Each of these was found by a wrong answer, not by design. SPEC TD-034b states them; here is why.

- **Open world.** With protobuf as decompiled source, a parser factory that only the framework calls — through
  `ParseFrom`, which no root reaches — made no access at all: the finding metadata mode had found vanished with no gap
  and no counter. A delegate the driver did not see run is `unknown-execution`, unless the analysis followed it into the
  call, nothing keeps it, it was handed to nothing the analysis cannot follow, and the call reached no code the analysis
  did not see (no receiverless dispatch, no dropped body, no unsupported operation): then it is `not-run` (run B2b, ADR
  0015). A remove accessor whose body only removes the handler from its slot (`_h -= value`) is the case: it neither
  runs nor keeps its handler. A remove that invokes or keeps `value` is classified like any member.
- **Kept beats fired.** `CircuitStateController`'s constructor calls `Reset()`, which calls `_onReset` behind
  `if (circuitState != CircuitState.Closed)`; the engine does not follow the field's value, so the probe fired during the
  call and the first classifier said `invoke-now`. The callback runs later, in the requests. A delegate is `invoke-now`
  only when it fired during the call and is not kept afterwards; kept is decided on the heap, not by probes. The check
  moved 5 of 27 Polly `invoke-now` labels.
- **Holder by reachability.** For a circuit breaker's `onBreak` the driver saw `Isolate()` run it and never saw
  `Execute`, although `ExecuteAsync → breakerController.OnActionFailure → Break_NeedsLock → _onBreak` is plainly in the
  code. The observed triggers are a lower bound; a model built on them loses every race in the requests that execute the
  policy. A holder runs its delegate wherever any of its members runs.
- **Own root.** The first synthesizer put all variants of one member in one driver, as separate actions, and read a probe
  anywhere. The heap merged the policies built in different actions — the allocation sits deep in Polly's call chain,
  where the context is one call site — so `ExecuteAsync` in a trigger action ran the call action's lambda, and
  `WaitAndRetryAsync`'s `onRetry` came out `invoke-now`. A probe counts for its variant only in executions whose root is
  that variant's action.
- **Implementation, never reference.** A reference assembly's bodies are `throw null`; decompiled, they read as "touches
  nothing". Framework references are ref packs, so the generator must find the implementation of the same version
  (question 41 of `docs/QUESTIONS.md`).
- **Frontier.** Polly keeps `sleepDurationProvider` as `Enumerable.Range(1, n).Select(provider)`; LINQ is metadata to
  Polly, so the provider landed in an unknown execution. Models of a library's own dependencies must exist before the
  library's are generated.

## Driver synthesis

The recipe `prototype/engineprobe/Synth.cs` follows. Every step is decided from symbols of the decompiled library; no
line is written per member.

1. **Resolve the member.** Roslyn's `DocumentationCommentId.CreateDeclarationId` appends `~ReturnType`; XML
   documentation and ILSpy's `IdStringProvider` do not (they add it only for `op_Implicit` and `op_Explicit`). Strip it
   before asking the decompiler. Every accessor — getter, setter, indexer accessor, event add or remove — is reached
   through its own `M:` id (run B2b); a `P:` or `E:` id answers `accessor` and names its accessors' `M:` ids, and an
   init-only setter answers `driver-not-synthesized`.
2. **Choose type arguments.** No constraint: `int`, or `object` under `class`. A class constraint that is concrete and
   not generic: the constraint itself (`Handle<TException>` gets `Exception`). Interface constraints, self-referential
   ones included (`MessageParser<T> where T : IMessage<T>`): a stub class that implements every abstract member of the
   interface and its bases explicitly, with empty bodies, `out` parameters assigned and `default!` returned. Constructing
   the interface over the stub needs the stub's symbol, so compile once with empty placeholders `Gen0`…`Gen3`, construct
   against them, then write the full stub.
3. **Produce every value.** In order: a probe lambda for a delegate; a canned BCL value (`List<T>` for the collection
   interfaces, `Dictionary`, `MemoryStream`, `Task.CompletedTask`, `typeof(object)`, `new Exception()`, `default` for
   value types); a public constructor — the one taking a delegate first when a probe is to be placed, then the fewest
   parameters; a public static or extension factory of the library whose result converts to the type, fewest parameters
   first, skipping generic factories with interface constraints and factories that take the very type they produce;
   non-abstract library classes that convert to an abstract target; otherwise `default!`, counted. Receivers get depth 3,
   arguments depth 2. Polly shows why factories matter: `Policy` came from `Policy.NoOp()`, `PolicyBuilder` from
   `Policy.Handle<Exception>()`.
4. **Place probes.** Each probe lambda has explicit parameter types, is cast to its delegate type, writes one static field
   `P{ordinal}_{variant}`, returns `Task.CompletedTask` for `Task` and `default!` otherwise, and is made by a static factory
   method of its own, `L_P{ordinal}_{variant}_{n}()`, so that its delegate region's `SiteBodyId` names the probe. A value
   produced for a non-delegate parameter carries that parameter's probes into the delegates it takes — that is how
   `ForMessage(tag, parser)` gets a probe in the parser's factory.
5. **Lay out variants as actions** of one controller: `V_Call` (the call), `V_Enum` (the call and a `foreach` over a result
   that implements `IEnumerable`, strings aside), and `T0`…`T23`, one per public instance method of the holder — the
   constructed object, else a result whose type the library declares, else the receiver — and its library bases and
   interfaces, non-generic, without `ref`/`in`/ref-like parameters, awaited when they return `Task` or `ValueTask`.
   A static property setter is written `Type.Prop = value`; `Type.set_Prop(value)` does not compile.
6. **Keep the result.** `V_Call` stores the result and the receiver in `Keep.R` and `Keep.Recv`. A side effect to know:
   a stored lazy sequence escapes and gets an unknown enumeration (ADR 0011), so in `V_Call` an iterator's probe fires in
   an unknown execution — check `V_Enum` before reading that as "unknown".
7. **Classify** in this order: fired in `V_Call`'s own root and not kept → `invoke-now`; fired in `V_Enum`'s root →
   `iterator`; fired in some `T_i`'s root and not during the call → `holder`, with the triggers as information; fired only
   in unknown executions → unknown; otherwise `unknown-execution`. Kept means a delegate region made by one of the
   `V_Call` factories is reachable from a `Static` region through `FieldsOf`/`PointsTo`, `DelegateCaptures` and
   `Collections` storage.

## Engine surface the prototypes used

- `EngineFixture.AnalyzeScope(solution, "scope:Fixture")` runs the pipeline on any Roslyn `Solution`.
  `FixtureSolution.Create` rejects every compile error, so a decompiled library with errors (protobuf has 11, in two
  bodies) needs a solution built by hand: `AdhocWorkspace`, the user project on `StubAssemblies.PlatformWithout([library])`
  plus every `StubAssemblies.Get(name, StubAssemblies.DefaultVersion(name))`, the library as its own project with
  `allowUnsafe` and `LanguageVersion.Preview`, and a project reference. Swapping only the user document with
  `WithDocumentText` keeps the library's compilation.
- The fixture's platform is the running .NET 10; a decompiled .NET 8 `System.*` assembly needs its `AssemblyVersion`
  raised to 10.0.0.0, or the platform's references to it do not unify.
- `run.Of(member)` lists the accesses to a field by name, construction-local ones aside;
  `run.Execution.Analysis.Execution(id)` gives `Kind` and `TreeRootId`. `TreeRootId` is a root-descriptor id such as
  `aspnetcore:controller-action:Fixture:T:DriverController:M:DriverController.V_Call`, not an execution id.
- `run.Execution.Heap.Heap`: `Regions` (`Kind` `Delegate` or `Static`; a delegate's `SiteBodyId` reads
  `body:Fixture:M:DriverController.L_P0_Call_0`), `FieldsOf`, `PointsTo`, `DelegateCaptures`, `Collections`.
- `run.Collection.Coverage.Gaps` (`Callee`, `Kind`), `run.Counter("no-receiver-object")`.
- `EngineFixture.ReachScope` catches only `ArgumentException` from lowering; `PhaseOneAnalyzer` also catches
  `InvalidOperationException` and drops the body with a `lowering:` diagnostic. That difference is how question 37 showed.

## Defects the library code exposed

All recorded in `docs/QUESTIONS.md`; each turns a precise model into `unknown-execution` or loses accesses:

- 36: a virtual call on `this` in a base method, receiver from a static factory, is resolved and also left unresolved —
  the leftover unknown executions around Polly's `Execute`.
- 37: `At(ref x) = 1` drops the whole body (protobuf's `WriteAsciiStringToBuffer`).
- 38: type tests such as `source is Iterator<T>` are not pruned — LINQ's `Where` and `Select` came out unknown.
- 39: enumeration of a library iterator through `IEnumerable<T>` does not reach its `MoveNext` — `SelectMany`, `OrderBy`
  and `GroupBy`'s result selector came out unknown.
- 40: Polly's `ExecuteAsync` gains an unexplained `Spawn` execution.
- 41, 42, 43: implementation assemblies, the 5b demo cases that rely on framework members staying opaque, and the result
  of a known call.

## Semantics the sources showed

What the gold set's labels do not say and a model must (from `source-verdicts.json`):

- LINQ: `All`, `Any`, `FirstOrDefault`, `SingleOrDefault`, `Sum`, `ToDictionary` run the delegate during the call;
  `Where`, `Select`, `SelectMany` run it per element on enumeration; `OrderBy` computes every key at the first
  `MoveNext`; `GroupBy` runs key and element selectors over the whole source when enumeration starts and the result
  selector once per group.
- `AddDbContext(options => …)`: the options delegate is a factory of `DbContextOptions` with lifetime `Scoped` by default,
  so it runs once per request scope; registered with `TryAdd`, so a second `AddDbContext` for the same context drops its
  delegate silently.
- `AddSwaggerGen(setup)`: runs once, lazily, on the first request that reaches the middleware, on that request's thread,
  through the options machinery of the next item.
- `IOptions<T>.Value`: the first read runs every `Configure`/`PostConfigure`/`Validate` delegate under a lock — where
  the `di-factory` delegates of options actually run.
- Host callbacks (`ConfigureAppConfiguration`, `ConfigureServices`, `ConfigureLogging`, `UseSerilog`): deferred to
  `Build()` under `WebHostBuilder`/`HostBuilder`, immediate under `WebApplicationBuilder`; startup either way.
  `ConfigureWebHostDefaults` runs its delegate at the call.
- `CancellationToken.Register`: synchronous when the token is already cancelled, otherwise on the thread that cancels.
- Polly: `Execute` runs the action 0..n times in the caller, except under a pessimistic timeout, where the action runs on
  a pool thread through `Task.Run` and may outlive the call; the engine found that `Task.Run` itself when Polly was source.
- CORS origin predicates, JWT bearer events, health-check predicates and writers, `AddCheck` delegates: one instance
  shared by all requests, so they run concurrently; health checks also on every publisher tick through `Task.Run`.

## AI models (5e)

- Model class decides everything: Sonnet with decompiled code made no unsafe label and every answer at confidence ≥ 0.85
  was right; Haiku labelled Polly's retry callbacks `invoke-now` at 0.85 from docs, and with code still called four
  setters `holder` that the framework invokes. Record the model identity in the model file and gate the AI layer on a
  model class measured on the gold set.
- What the member's own code cannot show is who calls a stored delegate: `SetIsOriginAllowed`, `JwtBearerEvents`
  setters and `HealthCheckOptions` properties only store. A question to AI needs the readers of the stored field, which a
  reference search over the decompiled library finds.
- An IL-probe veto — keep an AI label only at confidence ≥ 0.85 and only when the IL does not contradict it — caught 6 of
  Haiku's 8 unsafe labels, and cost Sonnet 15 correct answers from docs and 20 from code, mostly to the threshold; it
  cannot see holder-versus-event mistakes, because both look "stored" to IL.
- The famous packages flatter the docs-only runs; AI on libraries the model has not read is unmeasured.

## The IL probe

`prototype/ilprobe` is a taint interpreter over IL on `System.Reflection.Metadata` with class-hierarchy analysis and no
decompiler. Read soundly it made no unsafe label on the gold set and proved `invoke-now` for 11 parameters, but it
cannot see time (startup, requests) and it fails on async state machines, closures and wide hierarchies — the three
things the decompiler reconstructs. ADR 0012 rejected it as a frontend; it remains a cheap cross-check of where a delegate
goes.
