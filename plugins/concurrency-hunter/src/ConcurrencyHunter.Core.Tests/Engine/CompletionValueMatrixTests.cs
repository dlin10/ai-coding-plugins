using System.Collections.Concurrent;
using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The completion-value matrix (R1, R2): one fixture program, compiled and analysed once, with one controller action per cell
/// and one per twin. A cell crosses what makes a task (the producer), how the task travels before it is consumed (the path), how it is
/// consumed (the consumer) and what it completes with (the payload); its twin carries the value itself the same way, with the producer
/// in its direct form. The consumed value of a cell must be analysed as its twin's value is — the same objects by role, the same answer
/// to whether it may be an object the analysis does not follow, and the same freshness and ownership of a write through it — and, where
/// R1 says so, hold what the producer was handed. Every cell must be exact.</summary>
public sealed class CompletionValueMatrixTests(ClassCache cache) : IClassFixture<ClassCache>
{
    private const string FLAG = "Environment.ProcessorCount > 1";

    private const string CELL_PREFIX = "body:Fixture:M:Cell_";

    private Matrix Results => cache.Get("results", Run);

    private ConcurrentDictionary<string, Observed> Observations => cache.Get("observations", () => new ConcurrentDictionary<string, Observed>(StringComparer.Ordinal));

    /// <summary>What makes the task.</summary>
    public enum Producer
    {
        AsyncReturn,
        AsyncReturnValueTask,
        FromResult,
        ValueTaskFromResult,
        ValueTaskOfValue,
        ValueTaskOfTask,
        AsTask,
        WaitAsync,
        ConfigureAwait,
        TaskRunSync,
        TaskRunAsync,
        TaskRunOfTask,
        StartNewSync,
        StartNewUnwrap,
        ContinueWith,
        CompletionSource,
        CompletionSourceTry,
        WhenAllElement,
        WhenAny,
        Opaque,
        StartNewOfTask,
        ContinueWithOfTask,
        TaskRunOfValueTask,
        TaskRunExplicitOfTask
    }

    /// <summary>How the task travels before it is consumed.</summary>
    public enum Route
    {
        Immediate,
        Local,
        Field,
        Argument,
        Return,
        Element,
        Captured
    }

    /// <summary>How the task is consumed.</summary>
    public enum Consumer
    {
        Await,
        Result,
        GetResult
    }

    /// <summary>What the task completes with.</summary>
    public enum Payload
    {
        Shared,
        Fresh,
        ProducerFresh,
        OpaqueValue,
        Mixed,
        Struct,
        Null,
        MixedUnseen
    }

    /// <summary>The direct form of a producer in a twin.</summary>
    public enum TwinForm
    {
        Value,
        Method,
        Work,
        Continuation,
        Array,
        Choice,
        OpaqueCall
    }

    /// <summary>The producers that have no code of their own: <c>ProducerFresh</c> is <c>Fresh</c> there.</summary>
    private static readonly Producer[] Codeless =
    [
        Producer.FromResult, Producer.ValueTaskFromResult, Producer.ValueTaskOfValue, Producer.CompletionSource, Producer.CompletionSourceTry
    ];

    /// <summary>The producers that wrap or run another task or work, which <c>MixedUnseen</c> makes partly unseen.</summary>
    private static readonly Producer[] Wrapping =
    [
        Producer.ValueTaskOfTask, Producer.AsTask, Producer.WaitAsync, Producer.ConfigureAwait, Producer.TaskRunSync, Producer.TaskRunAsync,
        Producer.TaskRunOfTask, Producer.StartNewSync, Producer.StartNewUnwrap, Producer.ContinueWith, Producer.WhenAllElement, Producer.WhenAny,
        Producer.StartNewOfTask, Producer.ContinueWithOfTask, Producer.TaskRunOfValueTask, Producer.TaskRunExplicitOfTask
    ];

    /// <summary>The producers whose first consumption yields a task, which a second consumption takes.</summary>
    private static readonly Producer[] Nested =
    [
        Producer.WhenAny, Producer.StartNewOfTask, Producer.ContinueWithOfTask, Producer.TaskRunOfValueTask, Producer.TaskRunExplicitOfTask
    ];

    /// <summary>A cell; its name is the controller action that runs it.</summary>
    /// <param name="Producer">What makes the task.</param>
    /// <param name="Route">How the task travels before it is consumed.</param>
    /// <param name="Consumer">How the task is consumed.</param>
    /// <param name="Payload">What the task completes with.</param>
    private sealed record Cell(Producer Producer, Route Route, Consumer Consumer, Payload Payload)
    {
        public string Name => $"C_{Producer}_{Route}_{Consumer}_{Payload}";
    }

    /// <summary>A twin: the route carrying the value instead of the task, with no consumer, and the producer in its direct form.</summary>
    /// <param name="Form">The direct form of the producer.</param>
    /// <param name="Route">How the value travels.</param>
    /// <param name="Payload">The value.</param>
    private sealed record Twin(TwinForm Form, Route Route, Payload Payload)
    {
        public string Name => $"TW_{Form}_{Route}_{Payload}";
    }

    /// <summary>What a cell's or a twin's consumed value is.</summary>
    /// <param name="Roles">The roles of the regions the value holds.</param>
    /// <param name="InnerRoles">The roles of the regions the first consumption of a nested producer yields.</param>
    /// <param name="Unfollowed">Whether a dispatch on the value is marked as reaching an object the heap does not follow.</param>
    /// <param name="Touches">How many instances of that dispatch the heap has.</param>
    /// <param name="Writes">How many writes of <c>Count</c> through the value the action makes.</param>
    /// <param name="Fresh">Whether every such write is fresh; null without writes.</param>
    /// <param name="Ownership">The weakest ownership of their regions; null without writes.</param>
    /// <param name="Elsewhere">Whether such a write reaches an object of the class that the execution model creates only in another
    /// execution than the write's.</param>
    private sealed record Observed(IReadOnlySet<string> Roles, IReadOnlySet<string> InnerRoles, bool Unfollowed, int Touches, int Writes, bool? Fresh,
                                   OwnershipKind? Ownership, bool Elsewhere);

    /// <summary>The run of the whole fixture.</summary>
    /// <param name="Execution">The executions over the solved heap.</param>
    /// <param name="Collection">The interprocedural accesses over its executions.</param>
    /// <param name="Shared">The region of <c>Boxes.Shared</c>.</param>
    /// <param name="Other">The region of <c>Boxes.Other</c>.</param>
    private sealed record Matrix(ExecutionRun Execution, InterproceduralCollection Collection, IReadOnlySet<string> Shared, IReadOnlySet<string> Other)
    {
        public HeapRun Heap => Execution.Heap;
    }

    /// <summary>A difference between a cell and its expectation, and whether it narrows what the expectation allows.</summary>
    /// <param name="Message">What differs.</param>
    /// <param name="Narrower">Whether the cell says less than expected.</param>
    /// <param name="Unfollowed">Whether it is the answer to whether the value may be an object the analysis does not follow.</param>
    private sealed record Failure(string Message, bool Narrower, bool Unfollowed = false);

    /// <summary>The cells narrower than R1 by the field rule (catalog question 133): a field load keeps no unknown source of the values
    /// stored in it, and an opaque call's task has no task region whose completion slot would keep the mark, so these are followed where
    /// R1 asks unfollowed.</summary>
    private static readonly IReadOnlySet<string> FieldRule = new HashSet<string>(StringComparer.Ordinal)
    {
        "C_Opaque_Field_Await_Shared", "C_Opaque_Field_Result_Shared", "C_Opaque_Field_GetResult_Shared"
    };

    // ---- the cells and their twins ----

    private static IEnumerable<Cell> Cells() =>
        from producer in Enum.GetValues<Producer>()
        from route in Enum.GetValues<Route>()
        from consumer in Enum.GetValues<Consumer>()
        from payload in Enum.GetValues<Payload>()
        let cell = new Cell(producer, route, consumer, payload)
        where Allowed(cell)
        select cell;

    /// <summary>The cells the plan keeps: a configured awaitable has no <c>Result</c>; an opaque producer has no payload; a producer
    /// without code of its own has no payload of its own; the other payloads cross the routes and consumers that check what they
    /// add.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Allowed(Cell cell)
    {
        if (cell is { Producer: Producer.ConfigureAwait, Consumer: Consumer.Result })
            return false;
        if (cell.Producer == Producer.Opaque && cell.Payload != Payload.Shared)
            return false;
        return cell.Payload switch
        {
            Payload.Shared => true,
            Payload.Fresh => cell.Consumer == Consumer.Await,
            Payload.ProducerFresh => !Codeless.Contains(cell.Producer) && cell.Route is Route.Immediate or Route.Local && cell.Consumer == Consumer.Await,
            Payload.Mixed => cell.Consumer == Consumer.Await || cell.Route == Route.Local,
            Payload.MixedUnseen => Wrapping.Contains(cell.Producer) && cell.Route == Route.Local && cell.Consumer == Consumer.Await,
            _ => cell.Route == Route.Local && cell.Consumer == Consumer.Await
        };
    }

    private static TwinForm FormOf(Producer producer) => producer switch
    {
        Producer.FromResult or Producer.ValueTaskFromResult or Producer.ValueTaskOfValue or Producer.CompletionSource or Producer.CompletionSourceTry =>
            TwinForm.Value,
        Producer.TaskRunSync or Producer.StartNewSync => TwinForm.Work,
        Producer.ContinueWith => TwinForm.Continuation,
        Producer.WhenAllElement => TwinForm.Array,
        Producer.WhenAny => TwinForm.Choice,
        Producer.Opaque => TwinForm.OpaqueCall,
        _ => TwinForm.Method
    };

    /// <summary>The twin of a cell; <c>MixedUnseen</c>'s is <c>Mixed</c>'s, the shared object beside an unseen alternative.</summary>
    /// <param name="cell">The cell.</param>
    private static Twin TwinOf(Cell cell) =>
        new(FormOf(cell.Producer), cell.Route, cell.Payload == Payload.MixedUnseen ? Payload.Mixed : cell.Payload);

    private static IEnumerable<Twin> Twins() => Cells().Select(TwinOf).Distinct();

    // ---- the expectation ----

    /// <summary>The roles the consumed value holds by R1: the payload's object, and the other input's beside it for the producers that
    /// take two tasks.</summary>
    /// <param name="cell">The cell.</param>
    private static IReadOnlySet<string> ExpectedRoles(Cell cell)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        switch (cell.Payload)
        {
            case Payload.Shared or Payload.Mixed or Payload.MixedUnseen or Payload.Struct:
                roles.Add("Shared");
                break;
            case Payload.Fresh:
                roles.Add("handing");
                break;
            case Payload.ProducerFresh:
                roles.Add("producer");
                break;
        }

        if (cell.Producer == Producer.Opaque)
            roles.Clear();
        if (cell.Producer is Producer.WhenAllElement or Producer.WhenAny)
            roles.Add("Other");
        return roles;
    }

    /// <summary>Whether the consumed value may be an object the analysis does not follow by R1, or null where only the twin decides.</summary>
    /// <param name="cell">The cell.</param>
    private static bool? ExpectedUnfollowed(Cell cell) => cell switch
    {
        { Producer: Producer.Opaque } => true,
        { Payload: Payload.OpaqueValue or Payload.Mixed or Payload.MixedUnseen } => true,
        // An element load is an unknown source of its own (the default arm of EvaluateUnknown), in the cell and the twin alike.
        { Payload: Payload.Null, Producer: not Producer.WhenAllElement } => false,
        _ => null
    };

    // ---- the tests ----

    [Fact]
    public void Every_axis_value_occurs_and_the_matrix_has_985_cells()
    {
        var cells = Cells().ToArray();
        Assert.Equal(985, cells.Length);
        Assert.Equal(497, cells.Count(cell => cell.Payload == Payload.Shared));
        Assert.Equal(161, cells.Count(cell => cell.Payload == Payload.Fresh));
        Assert.Equal(206, cells.Count(cell => cell.Payload == Payload.Mixed));
        Assert.Equal(69, cells.Count(cell => cell.Payload is Payload.OpaqueValue or Payload.Struct or Payload.Null));
        Assert.Equal(36, cells.Count(cell => cell.Payload == Payload.ProducerFresh));
        Assert.Equal(16, cells.Count(cell => cell.Payload == Payload.MixedUnseen));
        Assert.Equal(24, Enum.GetValues<Producer>().Length);
        Assert.Equal(16, Wrapping.Length);
        Assert.All(Enum.GetValues<Producer>(), producer => Assert.Contains(cells, cell => cell.Producer == producer));
        Assert.All(Enum.GetValues<Route>(), route => Assert.Contains(cells, cell => cell.Route == route));
        Assert.All(Enum.GetValues<Consumer>(), consumer => Assert.Contains(cells, cell => cell.Consumer == consumer));
        Assert.All(Enum.GetValues<Payload>(), payload => Assert.Contains(cells, cell => cell.Payload == payload));
        Assert.All(Enum.GetValues<TwinForm>(), form => Assert.Contains(Twins(), twin => twin.Form == form));
        // Every cell and every twin is a root of the run, and nothing else of the controller is.
        var roots = Results.Heap.Program.Input.Roots
                           .Select(root => root.Entry.Symbol)
                           .Where(symbol => symbol.StartsWith("MatrixController.", StringComparison.Ordinal))
                           .Select(symbol => symbol["MatrixController.".Length..^"()".Length])
                           .Order(StringComparer.Ordinal);
        Assert.Equal(cells.Select(cell => cell.Name).Concat(Twins().Select(twin => twin.Name)).Order(StringComparer.Ordinal), roots);
    }

    [Fact]
    public void No_cell_is_narrower_than_its_expectation()
    {
        var failures = Cells().SelectMany(cell => Failures(cell).Where(failure => failure.Narrower).Select(failure => $"{cell.Name}: {failure.Message}"))
                              .ToArray();
        Assert.True(failures.Length == 0, $"{failures.Length} narrowing(s):\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Every_shared_cell_is_exact() => AssertExact(Payload.Shared);

    [Fact]
    public void Every_fresh_cell_is_exact() => AssertExact(Payload.Fresh, Payload.ProducerFresh);

    [Fact]
    public void Every_mixed_cell_is_exact() => AssertExact(Payload.Mixed, Payload.MixedUnseen);

    [Fact]
    public void Every_opaque_value_struct_and_null_cell_is_exact() => AssertExact(Payload.OpaqueValue, Payload.Struct, Payload.Null);

    // ---- comparing ----

    private void AssertExact(params Payload[] payloads)
    {
        var failures = Cells().Where(cell => payloads.Contains(cell.Payload))
                              .SelectMany(cell => Failures(cell).Select(failure => $"{cell.Name}: {failure.Message}{(failure.Narrower ? " (narrower)" : " (wider)")}"))
                              .ToArray();
        Assert.True(failures.Length == 0, $"{failures.Length} cell failure(s):\n" + string.Join("\n", failures));
    }

    /// <summary>The differences of a cell that count: a cell <see cref="FieldRule"/> lists must be narrower in its unfollowed answer,
    /// and that narrowing alone is excused.</summary>
    /// <param name="cell">The cell.</param>
    private IEnumerable<Failure> Failures(Cell cell)
    {
        var failures = Compare(cell).ToArray();
        if (!FieldRule.Contains(cell.Name))
            return failures;
        var excused = failures.Where(failure => failure is { Narrower: true, Unfollowed: true }).ToArray();
        return excused.Length == 0
            ? [.. failures, new Failure("listed as narrower by the field rule (catalog question 133), but its unfollowed answer is not narrower", Narrower: false)]
            : failures.Except(excused);
    }

    /// <summary>Every difference between a cell and its expectation: against its twin (R2) and, where R1 says so, absolutely.</summary>
    /// <param name="cell">The cell.</param>
    private IEnumerable<Failure> Compare(Cell cell)
    {
        var observed = Observe(cell.Name, Handing(cell.Route));
        var twin = TwinOf(cell);
        var expected = Observe(twin.Name, Handing(twin.Route));
        if (observed.Touches == 0 || expected.Touches == 0)
        {
            yield return new Failure($"no dispatch on the consumed value found (cell {observed.Touches}, twin {expected.Touches})", Narrower: true);
            yield break;
        }

        foreach (var failure in Roles("regions against the twin", observed.Roles, expected.Roles))
            yield return failure;
        foreach (var failure in Roles("regions", observed.Roles, ExpectedRoles(cell)))
            yield return failure;
        if (Nested.Contains(cell.Producer))
        {
            var inner = new HashSet<string>(StringComparer.Ordinal) { cell.Producer == Producer.WhenAny ? "task:method" : "task:lambda" };
            foreach (var failure in Roles("first consumption", observed.InnerRoles, inner))
                yield return failure;
        }

        // A field load keeps no unknown source today, so the twin's value loses the mark through a field while the task's completion
        // slot keeps it: on Field the cell answers as the same cell on Local does.
        var (reference, against) = cell.Route == Route.Field
            ? (Observe((cell with { Route = Route.Local }).Name, Handing(Route.Local)).Unfollowed, "same cell on Local")
            : (expected.Unfollowed, "twin");
        if (observed.Unfollowed != reference)
            yield return new Failure($"unfollowed {observed.Unfollowed}, {against} {reference}", Narrower: !observed.Unfollowed, Unfollowed: true);
        if (ExpectedUnfollowed(cell) is { } unfollowed && observed.Unfollowed != unfollowed)
            yield return new Failure($"unfollowed {observed.Unfollowed}, expected {unfollowed}", Narrower: !observed.Unfollowed, Unfollowed: true);

        if (cell.Payload == Payload.Null)
            yield break;
        // An object the execution model creates in another execution than the consuming access's — a spawned work, or an async body
        // whose task is not awaited at once after its first await — is handed from one execution to another.
        var (writes, fresh, ownership) = observed.Elsewhere
            ? (1, (bool?)false, (OwnershipKind?)OwnershipKind.Escaped)
            : (expected.Writes, expected.Fresh, expected.Ownership);
        if (observed.Writes == 0 != (writes == 0))
            yield return new Failure($"{observed.Writes} write(s) of Count, expected {(writes == 0 ? "none" : "some")}", Narrower: observed.Writes == 0);
        if (observed.Fresh != fresh)
            yield return new Failure($"fresh {Show(observed.Fresh)}, expected {Show(fresh)}", Narrower: observed.Fresh == true);
        if (observed.Ownership != ownership)
            yield return new Failure($"ownership {Show(observed.Ownership)}, expected {Show(ownership)}",
                                     Narrower: observed.Ownership is { } kind && ownership is { } want && kind < want);
    }

    private static IEnumerable<Failure> Roles(string what, IReadOnlySet<string> observed, IReadOnlySet<string> expected)
    {
        var missing = expected.Except(observed).Order(StringComparer.Ordinal).ToArray();
        var extra = observed.Except(expected).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            yield return new Failure($"{what}: missing [{string.Join(",", missing)}] of [{string.Join(",", expected.Order(StringComparer.Ordinal))}]", Narrower: true);
        if (extra.Length > 0)
            yield return new Failure($"{what}: extra [{string.Join(",", extra)}] beyond [{string.Join(",", expected.Order(StringComparer.Ordinal))}]", Narrower: false);
    }

    private static string Show<T>(T? value) where T : struct => value?.ToString() ?? "none";

    private static string Show(bool? value) => value?.ToString() ?? "none";

    // ---- observing ----

    /// <summary>The method of a route that creates a <c>Fresh</c> payload and hands it to the producer.</summary>
    /// <param name="route">The route.</param>
    private static string Handing(Route route) => route switch
    {
        Route.Field => "Store",
        Route.Return => "Make",
        _ => "Run"
    };

    /// <summary>What an action's consumed value is.</summary>
    /// <param name="name">The action, which runs the class <c>Cell_</c> of the same name.</param>
    /// <param name="handing">The method of the class that creates a <c>Fresh</c> payload.</param>
    private Observed Observe(string name, string handing) => Observations.GetOrAdd(name, _ => ObserveOnce(name, handing));

    private Observed ObserveOnce(string name, string handing)
    {
        var (heap, collection) = (Results.Heap.Heap, Results.Collection);
        var prefix = $"{CELL_PREFIX}{name}.";
        var instances = heap.Instances.Values.Where(instance => instance.BodyId.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

        IReadOnlySet<string> RolesOf(string variable) =>
            instances.SelectMany(instance => instance.Summary.Variables.Where(value => value.SymbolKey.Contains($"|{variable}|", StringComparison.Ordinal))
                                                     .SelectMany(value => heap.Resolve(instance.Id, value.Values)))
                     .Select(region => Role(region, prefix, handing))
                     .ToHashSet(StringComparer.Ordinal);

        var touches = instances.SelectMany(instance => instance.Summary.Calls.Where(call => call.Target.Contains("Box.Touch", StringComparison.Ordinal))
                                                               .Select(call => (instance.Id, call.OperationId)))
                               .ToArray();
        var writes = collection.Accesses.Where(access => !access.IsConstructionLocal && access.Resource.Member.Name == "Count" &&
                                                         access.Operation == AccessOperation.Write &&
                                                         access.Root.Symbol.EndsWith($".{name}()", StringComparison.Ordinal))
                               .ToArray();
        return new Observed(RolesOf("value"), RolesOf("inner"), touches.Any(heap.UnresolvedCallTargets.Contains), touches.Length, writes.Length,
                            writes.Length == 0 ? null : writes.All(write => write.IsFresh),
                            writes.Length == 0 ? null : writes.Max(write => write.Ownership),
                            writes.Any(write => CreatedElsewhere(write, prefix)));
    }

    /// <summary>Whether a write reaches an allocation of the class that the execution model runs in another execution than the write's
    /// and not in the write's own: the part of its body after an async body's first await counts, the part before it does not.</summary>
    /// <param name="write">The write.</param>
    /// <param name="prefix">The body id prefix of the action's class.</param>
    private bool CreatedElsewhere(Access write, string prefix)
    {
        var heap = Results.Heap.Heap;
        if (write.Resource.RegionId is not { } regionId || heap.Regions[regionId].SiteBodyId is not { } site ||
            !site.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var instances = heap.Instances.Values.Where(instance => instance.BodyId == site).Select(instance => instance.Id).ToHashSet(StringComparer.Ordinal);
        var executions = Results.Execution.Analysis.WalkNodeVisits.Keys
                                .Where(visit => instances.Contains(visit.Instance) && visit.Segment != BodySegment.Prefix)
                                .Select(visit => visit.Execution)
                                .ToHashSet(StringComparer.Ordinal);
        return executions.Count > 0 && !executions.Contains(write.ExecutionId);
    }

    /// <summary>The role of a region a cell's or a twin's value holds, which compares a cell with its twin: the shared and the other
    /// object by identity, an allocation of the class by whether the handing method or the producer's own code makes it, a task of the
    /// class by whether a method or a lambda makes it; anything else is foreign.</summary>
    /// <param name="regionId">The region.</param>
    /// <param name="prefix">The body id prefix of the action's class.</param>
    /// <param name="handing">The method of the class that creates a <c>Fresh</c> payload.</param>
    private string Role(string regionId, string prefix, string handing)
    {
        if (Results.Shared.Contains(regionId))
            return "Shared";
        if (Results.Other.Contains(regionId))
            return "Other";
        var region = Results.Heap.Heap.Regions[regionId];
        if (region.SiteBodyId is { } site && site.StartsWith(prefix, StringComparison.Ordinal))
        {
            var rest = site[prefix.Length..];
            if (region.Kind == HeapRegionKind.Task)
                return rest.Contains('#') ? "task:lambda" : "task:method";
            if (region.Kind == HeapRegionKind.Allocation && region.TypeKey == "Fixture:Box")
                return !rest.Contains('#') && (rest == handing || rest.StartsWith(handing + "(", StringComparison.Ordinal)) ? "handing" : "producer";
        }

        return $"foreign:{region.Display}";
    }

    // ---- the fixture ----

    private static Matrix Run()
    {
        var text = Source();
        var solution = FixtureSolution.Create(("Case.cs", Usings + text));
        var compilation = solution.Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Take(20).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(error => error.ToString())));
        var heap = Solve(ReachScope(solution, "scope:Fixture"));
        var execution = Execute(heap);
        var statics = heap.Heap.Regions.Values.Where(region => region.Kind == HeapRegionKind.Static && region.TypeKey == "Fixture:Boxes").ToArray();
        IReadOnlySet<string> Of(string field) =>
            statics.SelectMany(region => heap.Heap.PointsTo(region.Identity, field)).ToHashSet(StringComparer.Ordinal);
        var (shared, other) = (Of("Shared"), Of("Other"));
        Assert.Single(shared);
        Assert.Single(other);
        return new Matrix(execution, Collect(execution), shared, other);
    }

    /// <summary>The program: the payload types, the shared objects, the methods nothing describes, one class per cell and twin, and the
    /// controller whose actions run them.</summary>
    private static string Source()
    {
        var text = new StringBuilder("""
            using System.Collections.Generic;

            public class Box
            {
                public int Count;
                public virtual void Touch() { }
            }

            public struct Pair
            {
                public Box B;
            }

            public static class Boxes
            {
                public static readonly Box Shared = new Box();
                public static readonly Box Other = new Box();
            }

            public static class Externals
            {
                public static extern Box MakeBox();
                public static extern Task<Box> MakeBoxAsync();
                public static extern Func<Box> MakeWork();
                public static extern Func<Task<Box>> MakeAsyncWork();
                public static extern Func<ValueTask<Box>> MakeValueTaskWork();
                public static extern Func<Task<Box>, Box> MakeContinuation();
                public static extern Func<Task<Box>, Task<Box>> MakeTaskContinuation();
            }

            """);
        var actions = new List<string>();
        foreach (var cell in Cells())
        {
            text.Append(CellClass(cell));
            actions.Add(cell.Name);
        }

        foreach (var twin in Twins())
        {
            text.Append(TwinClass(twin));
            actions.Add(twin.Name);
        }

        text.Append("public sealed class MatrixController : ControllerBase\n{\n");
        foreach (var action in actions)
            text.Append($"    public async Task {action}() => await new Cell_{action}().Run();\n");
        text.Append("}\n");
        return text + Startup();
    }

    private static string TypeOf(Payload payload) => payload == Payload.Struct ? "Pair" : "Box";

    /// <summary>The payload as an expression: a <c>Fresh</c> payload is the handing method's local <c>x</c>.</summary>
    /// <param name="payload">The payload.</param>
    private static string Value(Payload payload) => payload switch
    {
        Payload.Shared or Payload.MixedUnseen => "Boxes.Shared",
        Payload.Fresh => "x",
        Payload.ProducerFresh => "new Box()",
        Payload.OpaqueValue => "Externals.MakeBox()",
        Payload.Mixed => $"({FLAG} ? Boxes.Shared : Externals.MakeBox())",
        Payload.Struct => "new Pair { B = Boxes.Shared }",
        Payload.Null => "(Box)null",
        _ => throw new ArgumentOutOfRangeException(nameof(payload), payload, null)
    };

    private static string OtherValue(Payload payload) => payload == Payload.Struct ? "new Pair { B = Boxes.Other }" : "Boxes.Other";

    /// <summary>The members every class may call: the producer's async methods for a cell, their direct forms for a twin, each handed a
    /// <c>Fresh</c> payload as an argument and making any other payload itself.</summary>
    /// <param name="payload">The payload.</param>
    /// <param name="twin">Whether the class is a twin's.</param>
    private static string Members(Payload payload, bool twin)
    {
        var type = TypeOf(payload);
        var (parameter, returned) = payload == Payload.Fresh ? ($"{type} item", "item") : ("", Value(payload));
        return twin
            ? $"""
              private static {type} Produce({parameter}) => {returned};
              private static {type} Other() => {OtherValue(payload)};

              """
            : $$"""
              private static async Task<{{type}}> WorkAsync({{parameter}}) { await Task.Yield(); return {{returned}}; }
              private static async ValueTask<{{type}}> WorkValueAsync({{parameter}}) { await Task.Yield(); return {{returned}}; }
              private static async Task<{{type}}> OtherAsync() { await Task.Yield(); return {{OtherValue(payload)}}; }

              """;
    }

    /// <summary>The statements that create a <c>Fresh</c> payload in the handing method.</summary>
    /// <param name="payload">The payload.</param>
    private static string Create(Payload payload) => payload == Payload.Fresh ? "var x = new Box();\n" : "";

    private static string CellClass(Cell cell)
    {
        var type = TypeOf(cell.Payload);
        var (prelude, expression) = Produce(cell);
        var carrier = cell.Producer switch
        {
            Producer.AsyncReturnValueTask or Producer.ValueTaskFromResult or Producer.ValueTaskOfValue or Producer.ValueTaskOfTask => $"ValueTask<{type}>",
            Producer.ConfigureAwait => $"System.Runtime.CompilerServices.ConfiguredTaskAwaitable<{type}>",
            Producer.WhenAllElement => $"Task<{type}[]>",
            Producer.WhenAny or Producer.StartNewOfTask or Producer.ContinueWithOfTask or Producer.TaskRunExplicitOfTask => $"Task<Task<{type}>>",
            Producer.TaskRunOfValueTask => $"Task<ValueTask<{type}>>",
            _ => $"Task<{type}>"
        };

        string Consume(string task) => cell.Consumer switch
        {
            Consumer.Await => $"await ({task})",
            Consumer.Result => $"({task}).Result",
            Consumer.GetResult => $"({task}).GetAwaiter().GetResult()",
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };

        string Consumption(string task) =>
            (Nested.Contains(cell.Producer) ? $"var inner = {Consume(task)};\nvar consumed = {Consume("inner")};\n"
                : cell.Producer == Producer.WhenAllElement ? $"var all = {Consume(task)};\nvar consumed = all[0];\n"
                : $"var consumed = {Consume(task)};\n") + Use(cell.Payload);

        var (members, run) = RouteCode(cell.Route, carrier, Create(cell.Payload) + prelude, expression, Consumption);
        return Class(cell.Name, Members(cell.Payload, twin: false) + members, run);
    }

    private static string TwinClass(Twin twin)
    {
        var type = TypeOf(twin.Payload);
        var value = Value(twin.Payload);
        var argument = twin.Payload == Payload.Fresh ? "x" : "";
        var (prelude, expression) = twin.Form switch
        {
            TwinForm.Value => ("", value),
            TwinForm.Method => ("", $"Produce({argument})"),
            TwinForm.Work => ("", $"((Func<{type}>)(() => {value}))()"),
            TwinForm.Continuation => (ANTECEDENT, $"((Func<Task<Box>, {type}>)(_ => {value}))(antecedent)"),
            // The array travels as the cell's task does, and its element is read where the cell reads the element of the array it
            // awaited.
            TwinForm.Array => ("", $"new {type}[] {{ Produce({argument}), Other() }}"),
            TwinForm.Choice => ("", $"({FLAG} ? Produce({argument}) : Other())"),
            TwinForm.OpaqueCall => ("", "Externals.MakeBox()"),
            _ => throw new ArgumentOutOfRangeException(nameof(twin), twin, null)
        };
        var (members, run) = RouteCode(twin.Route, twin.Form == TwinForm.Array ? $"{type}[]" : type, Create(twin.Payload) + prelude, expression,
                                       carried => (twin.Form == TwinForm.Array ? $"var all = {carried};\nvar consumed = all[0];\n"
                                                      : $"var consumed = {carried};\n") + Use(twin.Payload));
        return Class(twin.Name, Members(twin.Payload, twin: true) + members, run);
    }

    private const string ANTECEDENT = "var antecedent = Task.FromResult((Box)null);\n";

    /// <summary>The statements before a cell's producer and the producer expression.</summary>
    /// <param name="cell">The cell.</param>
    private static (string Prelude, string Expression) Produce(Cell cell)
    {
        var type = TypeOf(cell.Payload);
        var value = Value(cell.Payload);
        var argument = cell.Payload == Payload.Fresh ? "x" : "";
        var unseen = cell.Payload == Payload.MixedUnseen;
        var task = unseen ? $"({FLAG} ? WorkAsync({argument}) : Externals.MakeBoxAsync())" : $"WorkAsync({argument})";
        var work = unseen ? $"{FLAG} ? (Func<{type}>)(() => {value}) : Externals.MakeWork()" : $"() => {value}";
        var asyncWork = unseen
            ? $"{FLAG} ? (Func<Task<{type}>>)(async () => {{ await Task.Yield(); return {value}; }}) : Externals.MakeAsyncWork()"
            : $"async () => {{ await Task.Yield(); return {value}; }}";
        var taskWork = unseen ? $"{FLAG} ? (Func<Task<{type}>>)(() => WorkAsync({argument})) : Externals.MakeAsyncWork()" : $"() => WorkAsync({argument})";
        return cell.Producer switch
        {
            Producer.AsyncReturn => ("", $"WorkAsync({argument})"),
            Producer.AsyncReturnValueTask => ("", $"WorkValueAsync({argument})"),
            Producer.FromResult => ("", $"Task.FromResult({value})"),
            Producer.ValueTaskFromResult => ("", $"ValueTask.FromResult({value})"),
            Producer.ValueTaskOfValue => ("", $"new ValueTask<{type}>({value})"),
            Producer.ValueTaskOfTask => ("", $"new ValueTask<{type}>({task})"),
            Producer.AsTask => ("", $"new ValueTask<{type}>({task}).AsTask()"),
            Producer.WaitAsync => ("", $"{task}.WaitAsync(CancellationToken.None)"),
            Producer.ConfigureAwait => ("", $"{task}.ConfigureAwait(false)"),
            Producer.TaskRunSync => ("", $"Task.Run({work})"),
            Producer.TaskRunAsync => ("", $"Task.Run({asyncWork})"),
            Producer.TaskRunOfTask => ("", $"Task.Run(() => {task})"),
            Producer.StartNewSync => ("", $"Task.Factory.StartNew({work})"),
            Producer.StartNewUnwrap => ("", $"Task.Factory.StartNew({taskWork}).Unwrap()"),
            Producer.ContinueWith => (ANTECEDENT, unseen
                ? $"antecedent.ContinueWith({FLAG} ? (Func<Task<Box>, {type}>)(_ => {value}) : Externals.MakeContinuation())"
                : $"antecedent.ContinueWith(_ => {value})"),
            Producer.CompletionSource => ($"var source = new TaskCompletionSource<{type}>();\nsource.SetResult({value});\n", "source.Task"),
            Producer.CompletionSourceTry => ($"var source = new TaskCompletionSource<{type}>();\nsource.TrySetResult({value});\n", "source.Task"),
            Producer.WhenAllElement => ("", $"Task.WhenAll({task}, OtherAsync())"),
            Producer.WhenAny => ("", $"Task.WhenAny({task}, OtherAsync())"),
            Producer.Opaque => ("", "Externals.MakeBoxAsync()"),
            Producer.StartNewOfTask => ("", $"Task.Factory.StartNew({taskWork})"),
            Producer.ContinueWithOfTask => (ANTECEDENT, unseen
                ? $"antecedent.ContinueWith({FLAG} ? (Func<Task<Box>, Task<{type}>>)(_ => WorkAsync({argument})) : Externals.MakeTaskContinuation())"
                : $"antecedent.ContinueWith(_ => WorkAsync({argument}))"),
            Producer.TaskRunOfValueTask => ("", unseen
                ? $"Task.Run({FLAG} ? (Func<ValueTask<{type}>>)(() => WorkValueAsync({argument})) : Externals.MakeValueTaskWork())"
                : $"Task.Run(() => WorkValueAsync({argument}))"),
            Producer.TaskRunExplicitOfTask => ("", $"Task.Run<Task<{type}>>({taskWork})"),
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
    }

    /// <summary>The statements through the consumed value: its object (a pair's <c>B</c>) is dispatched on, and written unless it is
    /// <c>null</c>.</summary>
    /// <param name="payload">The payload.</param>
    private static string Use(Payload payload) =>
        $"var value = consumed{(payload == Payload.Struct ? ".B" : "")};\nvalue.Touch();\n" + (payload == Payload.Null ? "" : "value.Count = 1;\n");

    /// <summary>The members a route needs and its <c>Run</c> method: <paramref name="setup"/> and <paramref name="expression"/> stand in
    /// the handing method, and <paramref name="consume"/> takes what the route carries where it is consumed.</summary>
    /// <param name="route">The route.</param>
    /// <param name="carrier">The type the route carries.</param>
    /// <param name="setup">The statements before the expression.</param>
    /// <param name="expression">The expression whose value the route carries.</param>
    /// <param name="consume">The statements that consume the carried value, given the expression that reads it.</param>
    private static (string Members, string Run) RouteCode(Route route, string carrier, string setup, string expression, Func<string, string> consume)
    {
        static string Run(string body) => $"public async Task Run()\n{{\n{body}}}\n";
        return route switch
        {
            Route.Immediate => ("", Run(setup + consume(expression))),
            Route.Local => ("", Run(setup + $"var t = {expression};\nGC.KeepAlive(this);\n" + consume("t"))),
            Route.Field => ($"private {carrier} _t;\nprivate void Store()\n{{\n{setup}_t = {expression};\n}}\n" +
                            $"private async Task Consume()\n{{\n{consume("_t")}}}\n",
                            Run("Store();\nawait Consume();\n")),
            Route.Argument => ($"private async Task Use({carrier} t)\n{{\n{consume("t")}}}\n", Run(setup + $"await Use({expression});\n")),
            Route.Return => ($"private {carrier} Make()\n{{\n{setup}return {expression};\n}}\n", Run("var t = Make();\n" + consume("t"))),
            Route.Element => ("", Run(setup + $"var list = new List<{carrier}>();\nlist.Add({expression});\n" + consume("list[0]"))),
            Route.Captured => ("", Run(setup + $"var t = {expression};\nFunc<Task> f = async () =>\n{{\n{consume("t")}}};\nawait f();\n")),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null)
        };
    }

    private static string Class(string name, string members, string run) => $"public sealed class Cell_{name}\n{{\n{members}{run}}}\n\n";
}
