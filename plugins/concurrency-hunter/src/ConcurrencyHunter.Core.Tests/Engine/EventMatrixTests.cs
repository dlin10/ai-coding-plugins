using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The event matrix (R1, R2): one fixture program, compiled and analysed once, with one group of classes per cell. Every cell
/// of the first table has a singleton <c>Tally</c> whose <c>Hits</c> the handler increments, a controller action <c>Read</c> that reads
/// it, a singleton that subscribes the handler in its constructor, and a hosted worker that raises or provokes the event; the table
/// crosses how the event is declared with how it is subscribed and raised. The second table crosses the handler operand of two
/// declarations with what it is, against a twin that puts the same handler into a plain delegate field of the same object; the third
/// crosses the field-like storage's own operations pairwise in two executions.</summary>
public sealed class EventMatrixTests
{
    /// <summary>The line the raise of a second-table cell or twin stands on.</summary>
    private const string INVOKED = "/*I*/";

    private static readonly Lazy<Matrix> Results = new(Run, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>How the event is declared.</summary>
    public enum Declaration
    {
        FieldLike,
        FieldLikeStatic,
        FieldLikeVirtual,
        CustomVirtualOverride,
        BaseAccessor,
        InterfaceEvent,
        ExplicitInterface,
        CustomAccessors,
        LibraryHolderModel,
        LibraryNoModel,
        TimerElapsed
    }

    /// <summary>How the handler is subscribed in the subscribing singleton's constructor.</summary>
    public enum Subscription
    {
        Add,
        AddThenRemove
    }

    /// <summary>How the event is raised; for the library and timer declarations <c>Invoke</c> stands for the provoking call.</summary>
    public enum Raise
    {
        Invoke,
        NullCheckedCall,
        LocalCopy
    }

    /// <summary>What the handler operand of a second-table cell is.</summary>
    public enum Operand
    {
        Known,
        Null,
        Unknown
    }

    /// <summary>What a field-like event's storage stores in the third table.</summary>
    public enum Storage
    {
        Instance,
        Static
    }

    /// <summary>An operation on a field-like event's storage.</summary>
    public enum StorageOperation
    {
        Subscribe,
        Unsubscribe,
        Raise,
        AssignNull,
        ExchangeNull
    }

    /// <summary>Where the handler is expected to run.</summary>
    private enum Where
    {
        Worker,
        HolderCalls,
        UnknownExecution,
        TimerCallback
    }

    /// <summary>The declarations the second table crosses.</summary>
    private static readonly Declaration[] OperandDeclarations = [Declaration.FieldLike, Declaration.CustomAccessors];

    /// <summary>The declarations whose accessors have no body in the run, or whose subscription is the timer recognizer's.</summary>
    private static readonly Declaration[] Provoked = [Declaration.LibraryHolderModel, Declaration.LibraryNoModel, Declaration.TimerElapsed];

    /// <summary>A cell of any table; its name prefixes its classes.</summary>
    private abstract record Cell
    {
        public abstract string Name { get; }
    }

    /// <summary>A cell of the first table.</summary>
    /// <param name="Declaration">How the event is declared.</param>
    /// <param name="Subscription">How the handler is subscribed.</param>
    /// <param name="Raise">How the event is raised.</param>
    private sealed record RunCell(Declaration Declaration, Subscription Subscription, Raise Raise) : Cell
    {
        public override string Name => $"T1_{Declaration}_{Subscription}_{Raise}";
    }

    /// <summary>A cell of the second table: a declaration and its handler operand.</summary>
    /// <param name="Declaration">How the event is declared.</param>
    /// <param name="Operand">What the handler operand is.</param>
    private sealed record OperandCell(Declaration Declaration, Operand Operand) : Cell
    {
        public override string Name => $"T2_{Declaration}_{Operand}";
    }

    /// <summary>A cell of the third table: a storage and the operations its two executions run.</summary>
    /// <param name="Storage">Whether the field-like event is an instance or a static one.</param>
    /// <param name="First">The operation the action <c>First</c> runs.</param>
    /// <param name="Second">The operation the action <c>Second</c> runs.</param>
    private sealed record StorageCell(Storage Storage, StorageOperation First, StorageOperation Second) : Cell
    {
        public override string Name => $"T3_{Storage}_{First}_{Second}";
    }

    /// <summary>What a first-table cell is expected to do: where the handler's increment runs, and the accessor calls a gap names.</summary>
    /// <param name="Where">Where the handler's increment runs.</param>
    /// <param name="Gaps">The accessors whose calls a semantic gap names.</param>
    private sealed record Expectation(Where Where, IReadOnlyList<string> Gaps);

    /// <summary>The run, the analyzer's result over the same solution and models, and the lines of the marked raises.</summary>
    /// <param name="Run">The engine run.</param>
    /// <param name="Analyzed">The analyzer's result with the model file in a repository.</param>
    /// <param name="InvokedLines">The lines of the marked raises.</param>
    private sealed record Matrix(EngineRun Run, AnalysisResult Analyzed, IReadOnlySet<int> InvokedLines);

    // ---- the cells ----

    private static IEnumerable<RunCell> RunCells() =>
        from declaration in Enum.GetValues<Declaration>()
        from subscription in Enum.GetValues<Subscription>()
        from raise in Enum.GetValues<Raise>()
        select new RunCell(declaration, subscription, raise);

    private static IEnumerable<OperandCell> OperandCells() =>
        from declaration in OperandDeclarations
        from operand in Enum.GetValues<Operand>()
        select new OperandCell(declaration, operand);

    private static IEnumerable<StorageCell> StorageCells() =>
        from storage in Enum.GetValues<Storage>()
        from first in Enum.GetValues<StorageOperation>()
        from second in Enum.GetValues<StorageOperation>()
        where first <= second
        select new StorageCell(storage, first, second);

    private static IEnumerable<Cell> Cells() => RunCells().Cast<Cell>().Concat(OperandCells()).Concat(StorageCells()).Where(Allowed);

    /// <summary>Every combination of the axes, apart from the raise forms of a declaration whose raise is not the program's own: a library
    /// or timer event is provoked by one call, which <c>Invoke</c> stands for.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Allowed(Cell cell) => cell is not RunCell run || !Provoked.Contains(run.Declaration) || run.Raise == Raise.Invoke;

    /// <summary>The twin of a second-table cell: the same handler in a plain delegate field of the same object, invoked as the raise is.</summary>
    /// <param name="cell">The cell.</param>
    private static string Twin(OperandCell cell) => $"TW_{cell.Declaration}_{cell.Operand}";

    // ---- the table ----

    /// <summary>Where a first-table cell's handler runs and which accessor calls are gaps.</summary>
    /// <param name="cell">The cell.</param>
    private static Expectation Expected(RunCell cell)
    {
        var removes = cell.Subscription == Subscription.AddThenRemove;
        return cell.Declaration switch
        {
            // R1: a library model of the accessor applies as to any known call; the holder rule runs what the holder keeps at every call
            // of its members without a body: Poke() in the worker, the subscribing add_Changed and, when it is called, remove_Changed.
            Declaration.LibraryHolderModel => new Expectation(Where.HolderCalls, []),
            // R1: without a model the accessor call is an opaque call handed the delegate, which runs in an unknown execution, and each
            // unmodelled accessor call is a semantic gap.
            Declaration.LibraryNoModel => new Expectation(Where.UnknownExecution, removes ? ["add_Changed", "remove_Changed"] : ["add_Changed"]),
            // R2: Elapsed stays the timer recognizer's: the subscription runs its handler in a timer callback, the unsubscription does nothing.
            Declaration.TimerElapsed => new Expectation(Where.TimerCallback, []),
            // R2: a source event's accessor bodies run by whatever route the call takes, a field-like event's storage is a field holding
            // the combination, and the raise runs every delegate it may hold in the raising execution; removal removes nothing.
            _ => new Expectation(Where.Worker, [])
        };
    }

    /// <summary>Whether a pair on the storage of a third-table cell is expected: only a non-atomic write conflicts (R2, TD-072), and
    /// <c>AssignNull</c> is the one non-atomic write among the operations.</summary>
    /// <param name="cell">The cell.</param>
    private static bool ExpectedPair(StorageCell cell) => cell.First == StorageOperation.AssignNull || cell.Second == StorageOperation.AssignNull;

    // ---- the tests ----

    [Fact]
    public void Every_cell_runs_the_handler_where_expected()
    {
        var failures = new List<string>();
        foreach (var cell in RunCells().Where(Allowed))
            failures.AddRange(Check(cell));
        Assert.True(failures.Count == 0, $"{failures.Count} cell failure(s):\n" + string.Join("\n", failures));

        // The holder model is a project model the analyzer reads from the repository, as ProjectModelTests' are.
        var analyzed = Results.Value.Analyzed;
        Assert.Equal(0, Assert.Single(analyzed.Coverage).Skips.GetValueOrDefault(CoverageCounters.MODEL_ENTRY_REJECTED));
        foreach (var cell in RunCells().Where(cell => Allowed(cell) && cell.Declaration == Declaration.LibraryHolderModel))
        {
            var hits = analyzed.Accesses.Where(access => access.Resource.Member.Name == "Hits" && IsOf(access, cell.Name)).ToArray();
            Assert.True(hits.Any(access => access.Operation != AccessOperation.Read && InWorker(access, cell.Name)),
                        $"{cell.Name}: the analyzer finds no increment in the worker; found " +
                        string.Join("; ", hits.Select(access => $"{access.Operation} root={access.Root.Symbol}")));
        }
    }

    [Fact]
    public void Every_handler_operand_reaches_the_raise_as_combined()
    {
        var failures = new List<string>();
        foreach (var cell in OperandCells())
        {
            var (observed, twin) = (ObserveRaise(cell.Name), ObserveRaise(Twin(cell)));
            if (observed.Invocations != 1 || twin.Invocations != 1)
            {
                failures.Add($"{cell.Name}: {observed.Invocations} and its twin {twin.Invocations} marked raise(s) found");
                continue;
            }

            // R2: a field-like event's storage and a custom event's backing field are fields, so the raise runs what the field holds:
            // whatever the twin's plain field runs, and through the same unresolved targets.
            if (!observed.Hits.SetEquals(twin.Hits))
                failures.Add($"{cell.Name}: Hits [{Show(observed.Hits)}], twin [{Show(twin.Hits)}]");
            if (observed.Marked != twin.Marked)
                failures.Add($"{cell.Name}: raise {(observed.Marked ? "" : "not ")}marked as reaching an unfollowed delegate, twin {(twin.Marked ? "" : "not ")}");
            if (cell.Operand == Operand.Null && observed.Hits.Count > 0)
                failures.Add($"{cell.Name}: a null handler makes an access of Hits");
            if (cell.Operand == Operand.Known && twin.Hits.Count == 0)
                failures.Add($"{Twin(cell)}: the known handler's twin makes no access of Hits");        }

        Assert.True(failures.Count == 0, $"{failures.Count} cell failure(s):\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Event_storage_operations_pair_only_with_a_plain_write()
    {
        var run = Results.Value.Run;
        var failures = new List<string>();
        foreach (var cell in StorageCells())
        {
            var accesses = run.Of("E").Where(access => access.Root.Symbol.StartsWith($"{cell.Name}_Controller.", StringComparison.Ordinal)).ToArray();
            var (first, second) = (accesses.Where(access => IsAction(access, cell, "First")).ToArray(),
                                   accesses.Where(access => IsAction(access, cell, "Second")).ToArray());
            if (first.Length == 0 || second.Length == 0)
            {
                failures.Add($"{cell.Name}: {first.Length} access(es) of the storage in First, {second.Length} in Second");
                continue;
            }

            // A pair names both sides: one access in First's execution and one in Second's; a self-pair of either never counts.
            var paired = run.Pairs.Pairs.Any(pair => first.Contains(pair.First) && second.Contains(pair.Second) ||
                                                     second.Contains(pair.First) && first.Contains(pair.Second));
            if (paired != ExpectedPair(cell))
            {
                failures.Add($"{cell.Name}: {(paired ? "a" : "no")} pair on the storage; accesses " +
                             string.Join(", ", accesses.Select(access => $"{access.Root.Symbol}:{access.Operation}").Distinct()));
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} cell failure(s):\n" + string.Join("\n", failures));
    }

    [Fact]
    public void The_matrix_has_exactly_the_allowed_cells()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in Enum.GetValues<Declaration>())
        foreach (var subscription in Enum.GetValues<Subscription>())
        foreach (var raise in Enum.GetValues<Raise>())
        {
            if (raise == Raise.Invoke || declaration is not (Declaration.LibraryHolderModel or Declaration.LibraryNoModel or Declaration.TimerElapsed))
                expected.Add($"T1_{declaration}_{subscription}_{raise}");
        }

        foreach (var declaration in new[] { Declaration.FieldLike, Declaration.CustomAccessors })
        foreach (var operand in Enum.GetValues<Operand>())
            expected.Add($"T2_{declaration}_{operand}");
        var operations = Enum.GetValues<StorageOperation>();
        foreach (var storage in Enum.GetValues<Storage>())
        {
            for (var i = 0; i < operations.Length; i++)
            {
                for (var j = i; j < operations.Length; j++)
                    expected.Add($"T3_{storage}_{operations[i]}_{operations[j]}");
            }
        }

        Assert.Equal((11, 2, 3, 3, 5), (Enum.GetValues<Declaration>().Length, Enum.GetValues<Subscription>().Length, Enum.GetValues<Raise>().Length,
                                        Enum.GetValues<Operand>().Length, operations.Length));
        Assert.Equal(8 * 2 * 3 + 3 * 2 + 2 * 3 + 2 * 15, expected.Count);
        Assert.Equal(expected.Order(StringComparer.Ordinal), Cells().Select(cell => cell.Name).Order(StringComparer.Ordinal));
        // Every cell and twin has its roots in the run: the first two tables' readers and workers, the third table's two actions.
        var roots = Results.Value.Run.Execution.Heap.Program.Input.Roots.Select(root => root.Entry.Symbol).ToHashSet(StringComparer.Ordinal);
        var missing = Cells().Where(cell => cell is not StorageCell).Select(cell => cell.Name)
                             .Concat(OperandCells().Select(Twin))
                             .SelectMany(name => new[] { $"{name}_Controller.Read()", $"{name}_Worker.ExecuteAsync(CancellationToken)" })
                             .Concat(StorageCells().SelectMany(cell => new[] { $"{cell.Name}_Controller.First()", $"{cell.Name}_Controller.Second()" }))
                             .Where(root => !roots.Contains(root))
                             .ToArray();
        Assert.True(missing.Length == 0, "roots missing: " + string.Join(", ", missing));
    }

    // ---- observing ----

    private static IEnumerable<string> Check(RunCell cell)
    {
        var run = Results.Value.Run;
        var expected = Expected(cell);
        var increments = run.Of("Hits").Where(access => access.Operation != AccessOperation.Read && IsOf(access, cell.Name)).ToArray();
        var reads = run.Of("Hits").Where(access => access.Operation == AccessOperation.Read &&
                                                   access.Root.Symbol == $"{cell.Name}_Controller.Read()").ToArray();
        if (increments.Length == 0 || reads.Length == 0)
        {
            yield return $"{cell.Name}: {increments.Length} increment(s) and {reads.Length} read(s) of Hits found";
            yield break;
        }

        // R1, R2: the handler never runs only in the request; its increment meets the action's read.
        if (!run.Pairs.Pairs.Any(pair => increments.Contains(pair.First) && reads.Contains(pair.Second) ||
                                         increments.Contains(pair.Second) && reads.Contains(pair.First)))
            yield return $"{cell.Name}: no increment pairs with Read";
        if (increments.Any(access => access.Root.Symbol.StartsWith($"{cell.Name}_Controller.", StringComparison.Ordinal)))
            yield return $"{cell.Name}: the handler runs in the request";

        var kinds = increments.Select(access => (Kind: KindOf(access), Worker: InWorker(access, cell.Name))).ToArray();
        switch (expected.Where)
        {
            case Where.Worker:
                if (!kinds.Any(kind => kind is { Kind: ExecutionKind.Root, Worker: true }))
                    yield return $"{cell.Name}: no increment in the worker's execution; found {Show(increments)}";
                break;
            case Where.UnknownExecution:
                if (!kinds.Any(kind => kind.Kind == ExecutionKind.UnknownDelegateCall))
                    yield return $"{cell.Name}: no increment in an unknown execution; found {Show(increments)}";
                break;
            case Where.TimerCallback:
                if (!kinds.Any(kind => kind.Kind == ExecutionKind.TimerCallback))
                    yield return $"{cell.Name}: no increment in a timer callback; found {Show(increments)}";
                break;
            case Where.HolderCalls:
                var callers = CallersOf(increments);
                if (!kinds.Any(kind => kind is { Kind: ExecutionKind.Root, Worker: true }) || !callers.Any(caller => caller.Contains(".Poke(", StringComparison.Ordinal)))
                    yield return $"{cell.Name}: no increment in the worker's execution at Poke(); found {Show(increments)}, run by [{Show(callers)}]";
                var calls = cell.Subscription == Subscription.AddThenRemove ? new[] { "add_Changed", "remove_Changed" } : ["add_Changed"];
                foreach (var accessor in calls)
                {
                    if (!callers.Any(caller => caller.Contains($".{accessor}(", StringComparison.Ordinal)))
                        yield return $"{cell.Name}: no increment at the {accessor} call; run by [{Show(callers)}]";
                }

                break;
        }

        var gaps = run.Collection.Coverage.Gaps.Where(gap => gap.Sites.Any(site => site.BodyId.Contains($":{cell.Name}_", StringComparison.Ordinal)))
                      .Select(gap => gap.Callee)
                      .ToArray();
        foreach (var accessor in new[] { "add_Changed", "remove_Changed" })
        {
            var gap = gaps.Any(callee => callee.Contains(accessor, StringComparison.Ordinal));
            if (gap != expected.Gaps.Contains(accessor))
                yield return $"{cell.Name}: {(gap ? "a" : "no")} semantic gap names {accessor}; gaps [{string.Join(", ", gaps)}]";
        }
    }

    /// <summary>The methods of the calls whose heap edges run the instances holding <paramref name="accesses"/>: where the holder rule
    /// ran the held delegate, a call of a holder member without a body.</summary>
    /// <param name="accesses">The accesses.</param>
    private static IReadOnlySet<string> CallersOf(IEnumerable<Access> accesses)
    {
        var heap = Results.Value.Run.Execution.Heap;
        var instances = accesses.Select(access => access.InstanceId).ToHashSet(StringComparer.Ordinal);
        return heap.Heap.Edges.Where(edge => instances.Contains(edge.CalleeInstance))
                   .Select(edge => heap.Program.Result.Bodies[heap.Heap.Instances[edge.CallerInstance].BodyId].Blocks
                                       .SelectMany(block => block.Operations)
                                       .OfType<IrCallOperation>()
                                       .FirstOrDefault(call => call.Id == edge.OperationId)?.Method ?? "")
                   .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>What the raise of a second-table cell or twin does: the operations and execution kinds of its accesses of <c>Hits</c>,
    /// whether its marked delegate call is in <c>HeapSolution.UnresolvedCallTargets</c>, and how many marked calls its source has.</summary>
    /// <param name="name">The cell or twin.</param>
    private static (IReadOnlySet<string> Hits, bool Marked, int Invocations) ObserveRaise(string name)
    {
        var (run, lines) = (Results.Value.Run, Results.Value.InvokedLines);
        var hits = run.Of("Hits").Where(access => IsOf(access, name) && access.Root.Symbol.StartsWith($"{name}_Worker.", StringComparison.Ordinal))
                      .Select(access => $"{access.Operation}@{KindOf(access)}")
                      .ToHashSet(StringComparer.Ordinal);
        var heap = run.Execution.Heap;
        var invocations = heap.Program.Result.Bodies.Values.Where(body => body.BodyId.Contains($"{name}_Source.", StringComparison.Ordinal))
                              .SelectMany(body => body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>()
                                                      .Where(call => call.CallKind == IrCallKind.Delegate && lines.Contains(call.Provenance.Span.StartLine))
                                                      .Select(call => (body.BodyId, call.Id)))
                              .ToArray();
        var marked = invocations.Any(invocation => heap.Instances(invocation.BodyId)
                                                       .Any(instance => heap.Heap.UnresolvedCallTargets.Contains((instance.Id, invocation.Id))));
        return (hits, marked, invocations.Length);
    }

    private static ExecutionKind KindOf(Access access) => Results.Value.Run.Execution.Analysis.Execution(access.ExecutionId).Kind;

    private static bool InWorker(Access access, string name) => access.Root.Symbol.StartsWith($"{name}_Worker.", StringComparison.Ordinal);

    /// <summary>Whether an access belongs to a cell: the member holding it is one of the cell's classes, or its root is.</summary>
    /// <param name="access">The access.</param>
    /// <param name="name">The cell.</param>
    private static bool IsOf(Access access, string name) =>
        access.Symbol.StartsWith($"{name}_", StringComparison.Ordinal) || access.Root.Symbol.StartsWith($"{name}_", StringComparison.Ordinal);

    private static bool IsAction(Access access, StorageCell cell, string action) => access.Root.Symbol == $"{cell.Name}_Controller.{action}()";

    private static string Show(IEnumerable<string> values) => string.Join(",", values.Order(StringComparer.Ordinal));

    private static string Show(IEnumerable<Access> accesses) =>
        string.Join("; ", accesses.Select(access => $"{access.Operation}@{KindOf(access)} root={access.Root.Symbol} path=[{string.Join(" > ", access.CallPath)}]"));

    // ---- the fixture ----

    private static Matrix Run()
    {
        var text = Usings + Source();
        var solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = [Library.Value] }, ("Case.cs", text));
        var compilation = solution.Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var lines = text.Split('\n').Select((line, index) => (line, index))
                        .Where(pair => pair.line.Contains(INVOKED, StringComparison.Ordinal))
                        .Select(pair => pair.index + 1).ToHashSet();
        var run = AnalyzeScope(solution, "scope:Fixture", ModelCellFixture.Resolve(solution, Models()));
        using var repo = new CellModelRepository(Models());
        var analyzed = PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, repo.Root, CancellationToken.None).GetAwaiter().GetResult();
        return new Matrix(run, analyzed, lines);
    }

    /// <summary>The project model of the holder library: <c>add_Changed</c> keeps its handler in <c>this</c>, <c>remove_Changed</c>
    /// neither runs nor keeps it, and the constructor and <c>Poke</c> have no effect of their own.</summary>
    private static string Models()
    {
        string Entry(string member, string decision) =>
            "{\"member\":\"" + Id(member) + "\",\"effects\":{}" + (decision.Length == 0 ? "" : "," + decision) + "}";
        return "{\"schemaVersion\":1,\"assemblies\":[\"Events\"],\"models\":[" + string.Join(",",
            Entry(".ctor", ""),
            Entry("Poke", ""),
            Entry("add_Changed", """ "fates":{"value":{"fate":"holder","holder":"this"}}""".Trim()),
            Entry("remove_Changed", """ "fates":{"value":{"fate":"not-run"}}""".Trim())) + "]}";
    }

    /// <summary>The declaration id of the member of <c>Events.Held</c> with that name.</summary>
    /// <param name="member">The member's metadata name.</param>
    private static string Id(string member) =>
        DocumentationCommentId.CreateDeclarationId(LibrarySource.Value.GetTypeByMetadataName("Events.Held")!.GetMembers(member).Single())!;

    private static readonly Lazy<CSharpCompilation> LibrarySource = new(() => CSharpCompilation.Create("Events", [CSharpSyntaxTree.ParseText("""
        using System;

        namespace Events
        {
            public sealed class Held
            {
                public event Action Changed;
                public void Poke() => Changed?.Invoke();
            }

            public sealed class Plain
            {
                public event Action Changed;
                public void Poke() => Changed?.Invoke();
            }
        }
        """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));

    /// <summary>The library whose events have no accessor bodies in the run: <c>Held</c> has a project model, <c>Plain</c> none.</summary>
    private static readonly Lazy<MetadataReference> Library = new(() =>
    {
        using var stream = new MemoryStream();
        var emitted = LibrarySource.Value.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });

    /// <summary>The program: the opaque source of unknown delegates, the classes of every cell and twin, and their registrations.</summary>
    private static string Source()
    {
        var text = new StringBuilder("""
            public static class Opaque
            {
                public static extern Action Unknown();
            }

            """);
        var registrations = new StringBuilder();
        foreach (var cell in RunCells().Where(Allowed))
            Append(text, registrations, cell.Name, cell.Declaration, cell.Raise, Subscribe(cell.Subscription), "() => tally.Hits++", twin: false);
        foreach (var cell in OperandCells())
        {
            Append(text, registrations, cell.Name, cell.Declaration, Raise.Invoke, Subscribe(Subscription.Add), OperandCode(cell.Operand), twin: false);
            Append(text, registrations, Twin(cell), cell.Declaration, Raise.Invoke, target => $"{target} = h;", OperandCode(cell.Operand), twin: true);
        }

        foreach (var cell in StorageCells())
            AppendStorage(text, registrations, cell);
        return text + Startup(registrations.ToString());
    }

    private static Func<string, string> Subscribe(Subscription subscription) => subscription == Subscription.Add
        ? target => $"{target} += h;"
        : target => $"{target} += h;\n{target} -= h;";

    private static string OperandCode(Operand operand) => operand switch
    {
        Operand.Known => "() => tally.Hits++",
        Operand.Null => "null",
        Operand.Unknown => "Opaque.Unknown()",
        _ => throw new ArgumentOutOfRangeException(nameof(operand), operand, null)
    };

    /// <summary>The statements that raise a delegate held in <paramref name="storage"/>, marked.</summary>
    /// <param name="raise">The raise form.</param>
    /// <param name="storage">The event or field the raise reads.</param>
    private static string RaiseCode(Raise raise, string storage) => raise switch
    {
        Raise.Invoke => $"{storage}?.Invoke(); {INVOKED}",
        Raise.NullCheckedCall => $"if ({storage} != null) {storage}(); {INVOKED}",
        Raise.LocalCopy => $"var copy = {storage}; copy?.Invoke(); {INVOKED}",
        _ => throw new ArgumentOutOfRangeException(nameof(raise), raise, null)
    };

    /// <summary>The classes of a first- or second-table cell, or of a twin: its tally, the event's source, the singleton that subscribes
    /// in its constructor (the source itself for <c>BaseAccessor</c>), the worker that raises or provokes the event and the controller
    /// that reads the tally.</summary>
    /// <param name="text">The program text.</param>
    /// <param name="registrations">The registrations.</param>
    /// <param name="p">The cell's or twin's name, which prefixes its classes.</param>
    /// <param name="declaration">How the event is declared.</param>
    /// <param name="raise">How it is raised.</param>
    /// <param name="subscribe">The subscription statements over an event expression and the handler <c>h</c>.</param>
    /// <param name="handler">The handler expression, over <c>tally</c>.</param>
    /// <param name="twin">Whether to put the handler into the source's plain delegate field and invoke that instead.</param>
    private static void Append(StringBuilder text, StringBuilder registrations, string p, Declaration declaration, Raise raise,
                               Func<string, string> subscribe, string handler, bool twin)
    {
        var handlerType = declaration == Declaration.TimerElapsed ? "System.Timers.ElapsedEventHandler" : "Action";
        var lambda = declaration == Declaration.TimerElapsed ? "(sender, e) => tally.Hits++" : handler;
        var plain = twin ? $"public Action Plain;\npublic void FirePlain() {{ {RaiseCode(raise, "Plain")} }}\n" : "";
        string Fire(string storage) => twin ? "" : $"public void Fire() {{ {RaiseCode(raise, storage)} }}";
        var (types, prelude, target, provoke) = declaration switch
        {
            Declaration.FieldLike => ($"public sealed class {p}_Source {{ public event Action E; {Fire("E")}\n{plain}}}", "", "source.E", "source.Fire();"),
            Declaration.FieldLikeStatic =>
                ($"public sealed class {p}_Source {{ public static event Action E; {Fire("E")}\n{plain}}}", "", $"{p}_Source.E", "source.Fire();"),
            Declaration.FieldLikeVirtual =>
                ($"public class {p}_Base {{ public virtual event Action E; {Fire("E")} }}\npublic sealed class {p}_Source : {p}_Base {{ {plain}}}",
                 $"{p}_Base b = source;", "b.E", "source.Fire();"),
            Declaration.CustomVirtualOverride =>
                ($"public class {p}_Base {{ private Action _b; public virtual event Action E {{ add {{ _b += value; }} remove {{ _b -= value; }} }} }}\n" +
                 $"public sealed class {p}_Source : {p}_Base {{ private Action _h; " +
                 $"public override event Action E {{ add {{ _h += value; }} remove {{ _h -= value; }} }} {Fire("_h")}\n{plain}}}",
                 $"{p}_Base b = source;", "b.E", "source.Fire();"),
            Declaration.BaseAccessor =>
                ($"public class {p}_Base {{ private Action _b; public virtual event Action E {{ add {{ _b += value; }} remove {{ _b -= value; }} }} {Fire("_b")} }}\n" +
                 $"public sealed class {p}_Source : {p}_Base {{ private Action _h; " +
                 $"public override event Action E {{ add {{ _h += value; }} remove {{ _h -= value; }} }}\n" +
                 $"public {p}_Source({p}_Tally tally) {{ {handlerType} h = {lambda};\n{subscribe("base.E")} }}\n{plain}}}",
                 "", "", "source.Fire();"),
            Declaration.InterfaceEvent =>
                ($"public interface {p}_IFeed {{ event Action E; void Fire(); }}\n" +
                 $"public sealed class {p}_Source : {p}_IFeed {{ public event Action E; {(twin ? "public void Fire() { }" : Fire("E"))}\n{plain}}}",
                 $"{p}_IFeed f = source;", "f.E", $"{p}_IFeed feed = source; feed.Fire();"),
            Declaration.ExplicitInterface =>
                ($"public interface {p}_IFeed {{ event Action E; }}\n" +
                 $"public sealed class {p}_Source : {p}_IFeed {{ private Action _h; event Action {p}_IFeed.E {{ add {{ _h += value; }} remove {{ _h -= value; }} }} {Fire("_h")}\n{plain}}}",
                 $"{p}_IFeed f = source;", "f.E", "source.Fire();"),
            Declaration.CustomAccessors =>
                ($"public sealed class {p}_Source {{ private Action _h; public event Action E {{ add {{ _h += value; }} remove {{ _h -= value; }} }} {Fire("_h")}\n{plain}}}",
                 "", "source.E", "source.Fire();"),
            Declaration.LibraryHolderModel =>
                ($"public sealed class {p}_Source {{ public readonly Events.Held Inner = new Events.Held(); }}", "", "source.Inner.Changed", "source.Inner.Poke();"),
            Declaration.LibraryNoModel =>
                ($"public sealed class {p}_Source {{ public readonly Events.Plain Inner = new Events.Plain(); }}", "", "source.Inner.Changed", "source.Inner.Poke();"),
            Declaration.TimerElapsed =>
                ($"public sealed class {p}_Source {{ public readonly System.Timers.Timer Timer = new System.Timers.Timer(100); }}", "", "source.Timer.Elapsed",
                 "source.Timer.Start();"),
            _ => throw new ArgumentOutOfRangeException(nameof(declaration), declaration, null)
        };
        if (twin)
            (prelude, target, provoke) = ("", "source.Plain", "source.FirePlain();");
        var subscriber = declaration != Declaration.BaseAccessor || twin;
        text.Append($"public sealed class {p}_Tally {{ public int Hits; }}\n{types}\n");
        if (subscriber)
        {
            text.Append($$"""
                public sealed class {{p}}_Sub
                {
                    public {{p}}_Sub({{p}}_Tally tally, {{p}}_Source source)
                    {
                        {{handlerType}} h = {{(twin ? handler : lambda)}};
                        {{prelude}}
                        {{subscribe(target)}}
                    }
                }

                """);
        }

        text.Append($$"""
            public sealed class {{p}}_Worker({{p}}_Source source{{(subscriber ? $", {p}_Sub sub" : "")}}) : BackgroundService
            {
                private readonly {{p}}_Source _source = source;
                {{(subscriber ? $"public readonly {p}_Sub Sub = sub;" : "")}}

                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var source = _source;
                    {{provoke}}
                    return Task.CompletedTask;
                }
            }

            public sealed class {{p}}_Controller({{p}}_Tally tally) : ControllerBase
            {
                private readonly {{p}}_Tally _tally = tally;
                public int Read() => _tally.Hits;
            }

            """);
        registrations.Append($"services.AddSingleton<{p}_Tally>(); services.AddSingleton<{p}_Source>(); ");
        if (subscriber)
            registrations.Append($"services.AddSingleton<{p}_Sub>(); ");
        registrations.Append($"services.AddHostedService<{p}_Worker>();\n");
    }

    /// <summary>The classes of a third-table cell: a singleton hub declaring a field-like event over the cell's storage, and a controller
    /// whose actions <c>First</c> and <c>Second</c> run the cell's two operations on it.</summary>
    /// <param name="text">The program text.</param>
    /// <param name="registrations">The registrations.</param>
    /// <param name="cell">The cell.</param>
    private static void AppendStorage(StringBuilder text, StringBuilder registrations, StorageCell cell)
    {
        var p = cell.Name;
        var modifier = cell.Storage == Storage.Static ? "static " : "";
        var receiver = cell.Storage == Storage.Static ? $"{p}_Hub" : "_hub";
        string Operation(StorageOperation operation) => operation switch
        {
            StorageOperation.Subscribe => $"{receiver}.E += Handler",
            StorageOperation.Unsubscribe => $"{receiver}.E -= Handler",
            StorageOperation.Raise => $"{receiver}.Raise()",
            StorageOperation.AssignNull => $"{receiver}.Clear()",
            StorageOperation.ExchangeNull => $"{receiver}.Exchange()",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
        text.Append($$"""
            public sealed class {{p}}_Hub
            {
                public {{modifier}}event Action E;
                public {{modifier}}void Raise() => E?.Invoke();
                public {{modifier}}void Clear() => E = null;
                public {{modifier}}void Exchange() => Interlocked.Exchange(ref E, null);
            }

            public sealed class {{p}}_Controller({{p}}_Hub hub) : ControllerBase
            {
                private readonly {{p}}_Hub _hub = hub;
                private static void Handler() { }
                public void First() => {{Operation(cell.First)}};
                public void Second() => {{Operation(cell.Second)}};
            }

            """);
        registrations.Append($"services.AddSingleton<{p}_Hub>();\n");
    }
}
