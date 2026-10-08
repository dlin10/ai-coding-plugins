using System.Collections.Concurrent;
using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The delegate-combination matrix (R3): two fixture programs, each compiled and analysed once, with one controller action
/// per cell. The first table crosses every form that combines or removes delegates with what its left and right operands are; the
/// second crosses four forms with the side an unknown operand stands on and where that operand comes from; the third observes the
/// channels besides the runs — the cells a combination reads and what the stored combination depends on. The matrix checks the
/// combination rule, not how precisely the engine keeps a value in each place: every operand of a cell has a twin action that puts it
/// alone in the same place by plain assignment and invokes it the same way, and <see cref="Expected(Cell)"/> combines what the twins
/// do by R3. The forms whose places are exact also meet R3's formulas over the operands themselves. The first two tables share a
/// program that stops before pairing: its six hundred roots all write the two <c>Tally</c> fields, and pairing them made a hundred
/// million pairs that no test reads. The third table, whose array-cell check reads a pair, is a program of its own.</summary>
public sealed class DelegateCombinationMatrixTests
{
    /// <summary>The line the invocation of a cell's or a twin's result stands on.</summary>
    private const string INVOKED = "/*I*/";

    private const string REF_LOCAL = "a store through a ref local does not reach the later read through it";

    private const string CAPTURED_LOCAL = "a store to a captured local inside a lambda does not reach the read after the lambda ran";

    private static readonly Lazy<Matrix> Results = new(Run, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>A form that combines or removes delegates.</summary>
    public enum Form
    {
        Plus,
        LocalCompound,
        ParameterCompound,
        RefLocalCompound,
        FieldCompound,
        AutoPropertyCompound,
        AccessorPropertyCompound,
        IndexerCompound,
        CapturedLongFormCompound,
        ElementCompound,
        CapturedLocalCompound,
        CombinePair,
        CombineExpandedParams,
        CombineCollection,
        CombineArrayCreation,
        CombineArray,
        Minus,
        LocalRemove,
        ParameterRemove,
        RefLocalRemove,
        FieldRemove,
        AutoPropertyRemove,
        AccessorPropertyRemove,
        IndexerRemove,
        CapturedLongFormRemove,
        ElementRemove,
        CapturedLocalRemove,
        Remove,
        RemoveAll,
        RemoveNamed,
        RemoveAllNamed,
        RemoveAfterCombine,
        CombineSpread
    }

    /// <summary>What an operand is.</summary>
    public enum Operand
    {
        Known,
        Null,
        Unknown
    }

    /// <summary>The side an operand stands on.</summary>
    public enum Side
    {
        Left,
        Right
    }

    /// <summary>Where the unknown operand of an origin cell comes from.</summary>
    public enum Origin
    {
        SourceCall,
        Parameter,
        Field,
        Captured,
        Element,
        DictionaryKey,
        Kept
    }

    /// <summary>A channel the third table observes.</summary>
    public enum Channel
    {
        ArrayCells,
        FieldCompound,
        FieldRemove,
        SelfRightOfCombine,
        SelfRightOfRemove
    }

    /// <summary>The forms of the origins table.</summary>
    private static readonly Form[] OriginForms = [Form.CombinePair, Form.FieldCompound, Form.Remove, Form.FieldRemove];

    /// <summary>The forms that combine, every operand contributing.</summary>
    private static readonly Form[] Combining =
    [
        Form.Plus, Form.LocalCompound, Form.ParameterCompound, Form.RefLocalCompound, Form.FieldCompound, Form.AutoPropertyCompound,
        Form.AccessorPropertyCompound, Form.IndexerCompound, Form.CapturedLongFormCompound, Form.ElementCompound, Form.CapturedLocalCompound,
        Form.CombinePair, Form.CombineExpandedParams, Form.CombineCollection, Form.CombineArrayCreation, Form.CombineArray
    ];

    /// <summary>The forms that remove, the left operand alone contributing.</summary>
    private static readonly Form[] Removing =
    [
        Form.Minus, Form.LocalRemove, Form.ParameterRemove, Form.RefLocalRemove, Form.FieldRemove, Form.AutoPropertyRemove,
        Form.AccessorPropertyRemove, Form.IndexerRemove, Form.CapturedLongFormRemove, Form.ElementRemove, Form.CapturedLocalRemove,
        Form.Remove, Form.RemoveAll, Form.RemoveNamed, Form.RemoveAllNamed
    ];

    /// <summary>The forms whose result stays in a local of the body that invokes it, where the engine keeps a value exactly.</summary>
    private static readonly Form[] Exact =
    [
        Form.Plus, Form.LocalCompound, Form.CombinePair, Form.CombineExpandedParams, Form.CombineCollection, Form.CombineArrayCreation,
        Form.Minus, Form.LocalRemove, Form.Remove, Form.RemoveAll, Form.RemoveNamed, Form.RemoveAllNamed
    ];

    /// <summary>The cells a <c>Known</c> operand's twin of which writes nothing, by name with their place's reason: they cannot show
    /// that operand's writes in the combination, and their expectation still holds over the twins.</summary>
    private static readonly IReadOnlyDictionary<string, string> Unobservable = Listed(
        (REF_LOCAL, [
            "T1_RefLocalCompound_Known_Known", "T1_RefLocalCompound_Known_Null", "T1_RefLocalCompound_Known_Unknown",
            "T1_RefLocalCompound_Null_Known", "T1_RefLocalCompound_Unknown_Known",
            "T1_RefLocalRemove_Known_Known", "T1_RefLocalRemove_Known_Null", "T1_RefLocalRemove_Known_Unknown",
            "T1_RefLocalRemove_Null_Known", "T1_RefLocalRemove_Unknown_Known"
        ]),
        (CAPTURED_LOCAL, [
            "T1_CapturedLocalCompound_Known_Known", "T1_CapturedLocalCompound_Null_Known", "T1_CapturedLocalCompound_Unknown_Known",
            "T1_CapturedLocalRemove_Known_Known", "T1_CapturedLocalRemove_Null_Known", "T1_CapturedLocalRemove_Unknown_Known"
        ]));

    private static readonly ConcurrentDictionary<string, Observed> Observations = new(StringComparer.Ordinal);

    /// <summary>A cell of any table; its name is the controller action that runs it.</summary>
    private abstract record Cell
    {
        public abstract string Name { get; }
    }

    /// <summary>A cell of the first table: a form and its two operands.</summary>
    /// <param name="Form">The combining or removing form.</param>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand.</param>
    private sealed record RunCell(Form Form, Operand Left, Operand Right) : Cell
    {
        public override string Name => $"T1_{Form}_{Left}_{Right}";
    }

    /// <summary>A cell of the origins table: a form, the side its unknown operand stands on and where that operand comes from.</summary>
    /// <param name="Form">The combining or removing form.</param>
    /// <param name="Side">The side the unknown operand stands on.</param>
    /// <param name="Origin">Where that operand comes from.</param>
    private sealed record OriginCell(Form Form, Side Side, Origin Origin) : Cell
    {
        public override string Name => $"T2_{Form}_{Side}_{Origin}";
    }

    /// <summary>A cell of the channels table.</summary>
    /// <param name="Channel">The channel the cell checks.</param>
    private sealed record ChannelCell(Channel Channel) : Cell
    {
        public override string Name => $"T3_{Channel}";
    }

    /// <summary>What a cell is expected to do: the <c>Tally</c> fields it writes, whether its invocation is marked as possibly
    /// reaching a delegate the heap does not follow, and whether it is an opaque call whose writes are not asserted.</summary>
    /// <param name="Writes">The <c>Tally</c> fields the cell writes.</param>
    /// <param name="Marked">Whether its invocation is marked.</param>
    /// <param name="Opaque">Whether it is an opaque call whose writes are not asserted.</param>
    private sealed record Expectation(IReadOnlySet<string> Writes, bool Marked, bool Opaque = false);

    /// <summary>What a cell or a twin did.</summary>
    /// <param name="Writes">The <c>Tally</c> fields it writes.</param>
    /// <param name="Marked">Whether its marked invocation is in <c>HeapSolution.UnresolvedCallTargets</c>.</param>
    /// <param name="Invocations">How many marked invocations its class has.</param>
    /// <param name="OpaqueCombine">Whether its class makes an opaque <c>Combine</c> call.</param>
    private sealed record Observed(IReadOnlySet<string> Writes, bool Marked, int Invocations, bool OpaqueCombine);

    /// <summary>The run of the first two tables, collected without pairing, the lines of its source the marked invocations stand on,
    /// and the engine run of the third table.</summary>
    /// <param name="Execution">The executions of the first two tables' cells and twins over their solved heap.</param>
    /// <param name="Collection">Their interprocedural accesses.</param>
    /// <param name="InvokedLines">The source lines the marked invocations stand on.</param>
    /// <param name="Channels">The engine run of the channels table, pairs included.</param>
    private sealed record Matrix(ExecutionRun Execution, InterproceduralCollection Collection, IReadOnlySet<int> InvokedLines,
                                 EngineRun Channels);

    // ---- the cells and their twins ----

    private static IEnumerable<RunCell> RunCells() =>
        from form in Enum.GetValues<Form>()
        from left in Enum.GetValues<Operand>()
        from right in Enum.GetValues<Operand>()
        select new RunCell(form, left, right);

    private static IEnumerable<OriginCell> OriginCells() =>
        from form in OriginForms
        from side in Enum.GetValues<Side>()
        from origin in Enum.GetValues<Origin>()
        select new OriginCell(form, side, origin);

    private static IEnumerable<ChannelCell> ChannelCells() => Enum.GetValues<Channel>().Select(channel => new ChannelCell(channel));

    private static IEnumerable<Cell> Cells() => RunCells().Cast<Cell>().Concat(OriginCells()).Concat(ChannelCells()).Where(Allowed);

    /// <summary>Every combination of the three tables' axes: C# accepts every form over every operand, and every origin on either side.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Allowed(Cell cell) => true;

    /// <summary>The twin that puts an operand of the first table alone where a form puts it.</summary>
    /// <param name="form">The form.</param>
    /// <param name="side">The side the operand stands on.</param>
    /// <param name="operand">What the operand is.</param>
    private static string Twin(Form form, Side side, Operand operand) => $"TW_{form}_{side}_{operand}";

    /// <summary>The twin that puts an unknown operand of an origin alone where a form puts it.</summary>
    /// <param name="form">The form.</param>
    /// <param name="side">The side the unknown operand stands on.</param>
    /// <param name="origin">Where the unknown operand comes from.</param>
    private static string OriginTwin(Form form, Side side, Origin origin) => $"TWO_{form}_{side}_{origin}";

    private static IEnumerable<(Form Form, Side Side, Operand Operand)> Twins() =>
        from form in Enum.GetValues<Form>()
        where form != Form.CombineSpread
        from side in Enum.GetValues<Side>()
        from operand in Enum.GetValues<Operand>()
        select (form, side, operand);

    /// <summary>The twins of a cell's left and right operand.</summary>
    /// <param name="cell">A run cell or an origin cell.</param>
    private static (string Left, string Right) TwinsOf(Cell cell) => cell switch
    {
        RunCell run => (Twin(run.Form, Side.Left, run.Left), Twin(run.Form, Side.Right, run.Right)),
        OriginCell { Side: Side.Left } origin => (OriginTwin(origin.Form, Side.Left, origin.Origin), Twin(origin.Form, Side.Right, Operand.Known)),
        OriginCell origin => (Twin(origin.Form, Side.Left, Operand.Known), OriginTwin(origin.Form, Side.Right, origin.Origin)),
        _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
    };

    private static Form FormOf(Cell cell) => cell switch
    {
        RunCell run => run.Form,
        OriginCell origin => origin.Form,
        _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
    };

    // ---- the table ----

    private static IReadOnlySet<string> W(Operand operand, string field) =>
        operand == Operand.Known ? new HashSet<string>(StringComparer.Ordinal) { field } : new HashSet<string>(StringComparer.Ordinal);

    private static bool U(Operand operand) => operand == Operand.Unknown;

    /// <summary>The expectation of a run cell or an origin cell, from R3 over what its operands' twins do.</summary>
    /// <param name="cell">The cell.</param>
    private static Expectation Expected(Cell cell)
    {
        var form = FormOf(cell);
        // R3: Delegate.Combine of a spread is no combination form; it stays an opaque call, whose result the heap does not follow.
        if (form == Form.CombineSpread)
            return new Expectation(new HashSet<string>(StringComparer.Ordinal), Marked: true, Opaque: true);
        var (left, right) = TwinsOf(cell);
        var (a, b) = (Observe(left), Observe(right));
        // R3: a combination may run every delegate any operand may run, and keeps the origins of all its operands.
        if (Combining.Contains(form))
            return new Expectation(a.Writes.Union(b.Writes).ToHashSet(StringComparer.Ordinal), a.Marked || b.Marked);
        // R3: a removal may run every delegate its left operand may run, and keeps the origins of its left operand only.
        if (Removing.Contains(form))
            return new Expectation(a.Writes, a.Marked);
        // R3: a removal removes nothing the analysis tracks, so a removal after a combination answers as the combination.
        return new Expectation(a.Writes.Union(b.Writes).ToHashSet(StringComparer.Ordinal), a.Marked || b.Marked);
    }

    /// <summary>The expectation of a run cell of a form whose place is exact, from R3 over its operands themselves.</summary>
    /// <param name="cell">The cell.</param>
    private static Expectation ExpectedExactly(RunCell cell)
    {
        var (a, b) = (W(cell.Left, "A"), W(cell.Right, "B"));
        // R3: a combination may run every delegate any operand may run, and keeps the origins of all its operands.
        if (Combining.Contains(cell.Form))
            return new Expectation(a.Union(b).ToHashSet(StringComparer.Ordinal), U(cell.Left) || U(cell.Right));
        // R3: a removal may run every delegate its left operand may run, and keeps the origins of its left operand only.
        return new Expectation(a, U(cell.Left));
    }

    // ---- the tests ----

    [Fact]
    public void Every_cell_runs_the_expected_delegates()
    {
        var failures = Failures(RunCells());
        foreach (var cell in RunCells().Where(cell => Exact.Contains(cell.Form)))
            failures.AddRange(Compare(cell.Name, ExpectedExactly(cell), Observe(cell.Name)).Select(failure => failure + " (exact place)"));
        Assert.True(failures.Count == 0, $"{failures.Count} cell failure(s):\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Every_unknown_origin_follows_the_combination_rule()
    {
        var failures = Failures(OriginCells());
        Assert.True(failures.Count == 0, $"{failures.Count} cell failure(s):\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Combination_reads_every_array_cell()
    {
        var run = Results.Value.Channels;
        var cells = run.Collection.Accesses.Where(access => !access.IsConstructionLocal && access.Resource.Selector is not null &&
                                                            access.Resource.Member.Name == "_arr").ToArray();
        // R3: Delegate.Combine of an existing array reads its cells, which a write of another execution may change.
        var read = Assert.Single(cells, access => IsOf(access, "T3_ArrayCells") && access.Operation == AccessOperation.Read);
        var write = Assert.Single(cells, access => IsOf(access, "T3_ArrayCellsWriter") && access.Operation == AccessOperation.Write);
        Assert.Contains(run.Pairs.Pairs, pair => pair.First == read && pair.Second == write || pair.First == write && pair.Second == read);
    }

    [Fact]
    public void Every_combination_depends_on_all_its_operands()
    {
        var run = Results.Value.Channels;
        var failures = new List<string>();
        foreach (var channel in new[] { Channel.FieldCompound, Channel.FieldRemove, Channel.SelfRightOfCombine, Channel.SelfRightOfRemove })
        {
            var name = new ChannelCell(channel).Name;
            var accesses = run.Of("_c").Where(access => IsOf(access, name)).Select(access => access.Operation).ToArray();
            // R3: the result is computed from every operand, the right operand of a removal included, so the stored value depends on
            // the load of _c whichever operand carries it, and the store is a read-modify-write of _c.
            if (!accesses.Contains(AccessOperation.ReadModifyWrite) || accesses.Contains(AccessOperation.Write))
                failures.Add($"{name}: {string.Join(", ", accesses)}");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} form(s) whose store of _c is not a read-modify-write:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void The_matrix_has_exactly_the_allowed_cells()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var form in Enum.GetValues<Form>())
        foreach (var left in Enum.GetValues<Operand>())
        foreach (var right in Enum.GetValues<Operand>())
            expected.Add($"T1_{form}_{left}_{right}");
        foreach (var form in OriginForms)
        foreach (var side in Enum.GetValues<Side>())
        foreach (var origin in Enum.GetValues<Origin>())
            expected.Add($"T2_{form}_{side}_{origin}");
        foreach (var channel in Enum.GetValues<Channel>())
            expected.Add($"T3_{channel}");

        Assert.Equal(33, Enum.GetValues<Form>().Length);
        Assert.Equal(Combining.Length + Removing.Length + 2, Enum.GetValues<Form>().Length);
        Assert.Empty(Combining.Intersect(Removing));
        Assert.Empty(Exact.Except(Combining.Concat(Removing)));
        Assert.Equal(expected.Order(StringComparer.Ordinal), Cells().Select(cell => cell.Name).Order(StringComparer.Ordinal));
        // Every cell is a root of one of the two runs, and of only one; the other roots are the twins and the writer of the array cells.
        var twins = Twins().Select(twin => Twin(twin.Form, twin.Side, twin.Operand))
                           .Concat(OriginCells().Select(cell => OriginTwin(cell.Form, cell.Side, cell.Origin)));
        var roots = Results.Value.Execution.Heap.Program.Input.Roots
                           .Concat(Results.Value.Channels.Execution.Heap.Program.Input.Roots)
                           .Select(root => root.Entry.Symbol)
                           .Where(symbol => symbol.StartsWith("MatrixController.", StringComparison.Ordinal))
                           .Select(symbol => symbol["MatrixController.".Length..^"()".Length])
                           .ToArray();
        Assert.Equal(expected.Concat(twins).Append("T3_ArrayCellsWriter").Order(StringComparer.Ordinal), roots.Order(StringComparer.Ordinal));
    }

    // ---- observing ----

    /// <summary>The failures of cells against their expectation over their twins, and the list of cells a <c>Known</c> twin of which
    /// writes nothing checked against <see cref="Unobservable"/>.</summary>
    /// <param name="cells">The cells.</param>
    private static List<string> Failures(IEnumerable<Cell> cells)
    {
        var failures = new List<string>();
        var unobservable = new List<string>();
        foreach (var cell in cells)
        {
            failures.AddRange(Compare(cell.Name, Expected(cell), Observe(cell.Name)));
            if (FormOf(cell) == Form.CombineSpread)
                continue;
            var (left, right) = TwinsOf(cell);
            (Operand Operand, string Twin)[] known = cell switch
            {
                RunCell run => [(run.Left, left), (run.Right, right)],
                OriginCell origin => [(Operand.Known, origin.Side == Side.Left ? right : left)],
                _ => []
            };
            foreach (var twin in new[] { left, right }.Distinct(StringComparer.Ordinal))
            {
                if (Observe(twin).Invocations != 1)
                    failures.Add($"{twin}: {Observe(twin).Invocations} marked invocation(s) found");
            }

            if (known.Any(pair => pair.Operand == Operand.Known && Observe(pair.Twin).Writes.Count == 0))
                unobservable.Add(cell.Name);
        }

        var names = cells.Select(cell => cell.Name).ToHashSet(StringComparer.Ordinal);
        var listed = Unobservable.Keys.Where(names.Contains).ToArray();
        failures.AddRange(unobservable.Except(listed).Select(name => $"{name}: a Known operand's twin writes nothing, and the cell is not listed"));
        failures.AddRange(listed.Except(unobservable).Select(name => $"{name}: listed as unobservable ({Unobservable[name]}), but every Known twin writes"));
        return failures;
    }

    private static IEnumerable<string> Compare(string name, Expectation expected, Observed observed)
    {
        if (observed.Invocations != 1)
        {
            yield return $"{name}: {observed.Invocations} marked invocation(s) found";
            yield break;
        }

        if (expected.Opaque)
        {
            if (!observed.OpaqueCombine)
                yield return $"{name}: Delegate.Combine is not counted as an opaque call";
        }
        else if (!observed.Writes.SetEquals(expected.Writes))
        {
            yield return $"{name}: writes [{string.Join(",", observed.Writes.Order(StringComparer.Ordinal))}], " +
                         $"expected [{string.Join(",", expected.Writes.Order(StringComparer.Ordinal))}]";
        }

        if (observed.Marked != expected.Marked)
            yield return $"{name}: invocation {(observed.Marked ? "" : "not ")}marked as reaching an unfollowed origin";
    }

    /// <summary>What an action did: the <c>Tally</c> fields it writes, whether its marked invocation is in
    /// <c>HeapSolution.UnresolvedCallTargets</c>, how many marked invocations its class has, and whether its class makes an opaque
    /// <c>Combine</c> call.</summary>
    /// <param name="name">The action, which runs the class <c>Cell_</c> of the same name.</param>
    private static Observed Observe(string name) => Observations.GetOrAdd(name, ObserveOnce);

    private static Observed ObserveOnce(string name)
    {
        var (matrix, lines) = (Results.Value, Results.Value.InvokedLines);
        // The accesses of a field that can pair, construction-local accesses aside, as EngineRun.Of gives them.
        IEnumerable<Access> Of(string field) => matrix.Collection.Accesses.Where(access => access.Resource.Member.Name == field && !access.IsConstructionLocal);
        var writes = new[] { "A", "B" }.Where(field => Of(field).Any(access => IsOf(access, name) && access.Operation != AccessOperation.Read))
                                       .ToHashSet(StringComparer.Ordinal);
        var heap = matrix.Execution.Heap;
        var bodies = heap.Program.Result.Bodies.Values.Where(body => body.BodyId.Contains($"Cell_{name}.", StringComparison.Ordinal)).ToArray();
        var invocations = bodies.SelectMany(body => body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>()
                                                        .Where(call => call.CallKind == IrCallKind.Delegate && lines.Contains(call.Provenance.Span.StartLine))
                                                        .Select(call => (body.BodyId, call.Id)))
                                .ToArray();
        var marked = invocations.Any(invocation => heap.Instances(invocation.BodyId)
                                                       .Any(instance => heap.Heap.UnresolvedCallTargets.Contains((instance.Id, invocation.Id))));
        var opaqueCombine = bodies.SelectMany(body => heap.Instances(body.BodyId))
                                  .Any(instance => instance.Summary.OpaqueCalls.Any(call => !call.IsKnown && call.Callee.Contains("Combine", StringComparison.Ordinal)));
        return new Observed(writes, marked, invocations.Length, opaqueCombine);
    }

    private static Dictionary<string, string> Listed(params (string Reason, string[] Cells)[] groups) =>
        groups.SelectMany(group => group.Cells.Select(cell => (cell, group.Reason))).ToDictionary(pair => pair.cell, pair => pair.Reason, StringComparer.Ordinal);

    private static bool IsOf(Access access, string action) => access.Root.Symbol.EndsWith($".{action}()", StringComparison.Ordinal);

    // ---- the fixture ----

    private const string MODELS = """
        {"schemaVersion":1,"assemblies":["Keep"],"models":[
          {"member":"M:Keep.Keeper.Put(System.Object)","effects":{},"keeps":{"this":["arg:value"]}},
          {"member":"M:Keep.Keeper.Take","effects":{},"result":"[kept:this]"}
        ]}
        """;

    private static Matrix Run()
    {
        var (solution, lines) = Compile(Source());
        var execution = Execute(Solve(ReachScope(solution, "scope:Fixture", ModelCellFixture.Resolve(solution, MODELS))));
        var (channels, _) = Compile(ChannelSource());
        return new Matrix(execution, Collect(execution), lines, AnalyzeScope(channels, "scope:Fixture", ModelCellFixture.Resolve(channels, MODELS)));
    }

    /// <summary>A fixture program over the keeper's library, which compiles without an error, and the lines of its source the marked
    /// invocations stand on.</summary>
    /// <param name="text">The program, without the usings every fixture gets.</param>
    private static (Solution Solution, IReadOnlySet<int> InvokedLines) Compile(string text)
    {
        var solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = [Library()] }, ("Case.cs", Usings + text));
        var compilation = solution.Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var lines = (Usings + text).Split('\n').Select((line, index) => (line, index))
                                   .Where(pair => pair.line.Contains(INVOKED, StringComparison.Ordinal))
                                   .Select(pair => pair.index + 1).ToHashSet();
        return (solution, lines);
    }

    /// <summary>The library whose keeper keeps what it is handed and returns it: its members have no body in the run.</summary>
    private static MetadataReference Library()
    {
        var compilation = CSharpCompilation.Create("Keep", [CSharpSyntaxTree.ParseText("""
            namespace Keep;
            public sealed class Keeper
            {
                public void Put(object value) { }
                public object Take() => null!;
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        var emit = compilation.Emit(bytes);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return MetadataReference.CreateFromImage(bytes.ToArray());
    }

    /// <summary>The tally both known delegates write.</summary>
    private const string TALLY = """
        public static class Tally
        {
            public static int A;
            public static int B;
        }

        """;

    /// <summary>The program of the first two tables: the tally, the opaque source of unknown delegates, one class per cell and twin
    /// and the controller whose actions are the cells and the twins.</summary>
    private static string Source()
    {
        var text = new StringBuilder("using System.Collections.Generic;\n\n" + TALLY + """
            public static class Opaque
            {
                public static extern Action Unknown();
            }

            """);
        var actions = new List<string>();
        void Add(string name, string members, string run)
        {
            text.Append($"public sealed class Cell_{name}\n{{\n{members}\n{run}\n}}\n\n");
            actions.Add(name);
        }

        foreach (var cell in RunCells())
        {
            var (members, body) = Code(cell.Form);
            var prelude = $"Action a = {OperandCode(cell.Left, "A")};\nAction b = {OperandCode(cell.Right, "B")};\n";
            Add(cell.Name, members, $"public void Run()\n{{\n{prelude}{body}\n}}");
        }

        foreach (var cell in OriginCells())
        {
            var (members, body) = Code(cell.Form);
            var operands = cell.Side == Side.Left ? "Action a = u;\nAction b = () => Tally.B = 1;\n" : "Action a = () => Tally.A = 1;\nAction b = u;\n";
            var (originMembers, run) = OriginCode(cell.Origin, operands + body);
            Add(cell.Name, members + "\n" + originMembers, run);
        }

        foreach (var (form, side, operand) in Twins())
        {
            var (members, body) = TwinCode(form, side);
            Add(Twin(form, side, operand), members, $"public void Run()\n{{\nAction x = {OperandCode(operand, side == Side.Left ? "A" : "B")};\n{body}\n}}");
        }

        foreach (var cell in OriginCells())
        {
            var (members, body) = TwinCode(cell.Form, cell.Side);
            var (originMembers, run) = OriginCode(cell.Origin, "Action x = u;\n" + body);
            Add(OriginTwin(cell.Form, cell.Side, cell.Origin), members + "\n" + originMembers, run);
        }

        text.Append("public sealed class MatrixController : ControllerBase\n{\n");
        foreach (var action in actions)
            text.Append($"    public void {action}() => new Cell_{action}().Run();\n");
        text.Append("}\n");
        return text + Startup();
    }

    /// <summary>The program of the channels table: the tally, the singleton whose members are the cells and the writer of its array
    /// cells, and the controller whose actions call them.</summary>
    private static string ChannelSource()
    {
        var text = new StringBuilder(TALLY + """
            public sealed class Shared
            {
                private readonly Delegate[] _arr = new Delegate[1];
                private Action _c;

                public void T3_ArrayCells()
                {
                    var c = (Action)Delegate.Combine(_arr);
                    c?.Invoke();
                }

                public void T3_ArrayCellsWriter()
                {
                    Action a = () => Tally.A = 1;
                    _arr[0] = a;
                }

                public void T3_FieldCompound()
                {
                    Action b = () => Tally.B = 1;
                    _c += b;
                }

                public void T3_FieldRemove()
                {
                    Action b = () => Tally.B = 1;
                    _c -= b;
                }

                public void T3_SelfRightOfCombine()
                {
                    Action a = () => Tally.A = 1;
                    _c = a + _c;
                }

                public void T3_SelfRightOfRemove()
                {
                    Action a = () => Tally.A = 1;
                    _c = a - _c;
                }
            }

            """);
        text.Append("public sealed class MatrixController(Shared shared) : ControllerBase\n{\n    private readonly Shared _shared = shared;\n");
        foreach (var cell in ChannelCells())
            text.Append($"    public void {cell.Name}() => _shared.{cell.Name}();\n");
        text.Append("    public void T3_ArrayCellsWriter() => _shared.T3_ArrayCellsWriter();\n}\n");
        return text + Startup("services.AddSingleton<Shared>();");
    }

    private static string OperandCode(Operand operand, string field) => operand switch
    {
        Operand.Known => $"() => Tally.{field} = 1",
        Operand.Null => "null",
        Operand.Unknown => "Opaque.Unknown()",
        _ => throw new ArgumentOutOfRangeException(nameof(operand), operand, null)
    };

    /// <summary>Where an origin cell's unknown operand <c>u</c> comes from: the members it needs and the <c>Run</c> method that
    /// defines <c>u</c> before <paramref name="form"/>.</summary>
    /// <param name="origin">The origin.</param>
    /// <param name="form">The operands and the form, over <c>u</c>.</param>
    private static (string Members, string Run) OriginCode(Origin origin, string form)
    {
        string Run(string prelude) => $"public void Run()\n{{\n{prelude}\n{form}\n}}";
        return origin switch
        {
            Origin.SourceCall => ("private Action Source() => Opaque.Unknown();", Run("Action u = Source();")),
            Origin.Parameter => ($"private void Go(Action u)\n{{\n{form}\n}}", "public void Run() => Go(Opaque.Unknown());"),
            Origin.Field => ("private static Action s_u;\nprivate void Fill() => s_u = Opaque.Unknown();", Run("Fill();\nAction u = s_u;")),
            Origin.Captured => ("", Run("Action u = null;\nAction fill = () => u = Opaque.Unknown();\nfill();")),
            Origin.Element => ("", Run("var held = new Action[1];\nheld[0] = Opaque.Unknown();\nAction u = held[0];")),
            Origin.DictionaryKey => ("", Run("var keys = new Dictionary<Action, int>();\nkeys.Add(Opaque.Unknown(), 0);\nAction u = null;\nforeach (var k in keys.Keys)\n    u = k;")),
            Origin.Kept => ("", Run("var keeper = new Keep.Keeper();\nkeeper.Put(Opaque.Unknown());\nAction u = (Action)keeper.Take();")),
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null)
        };
    }

    private const string FIELD = "private Action _c;";
    private const string AUTO_PROPERTY = "private Action C { get; set; }";
    private const string PROPERTY = "private Action _p;\nprivate Action P { get => _p; set => _p = value; }";
    private const string INDEXER = "private Action _i;\nprivate Action this[int k] { get => _i; set => _i = value; }";
    private const string FLAG = "private readonly bool _flag = Environment.ProcessorCount > 1;";

    private static string Invoke(string target) => $"{target}?.Invoke(); {INVOKED}";

    /// <summary>The members a form needs and its statements over the locals <c>a</c> and <c>b</c>, the invocation of its result last
    /// and marked.</summary>
    /// <param name="form">The form.</param>
    private static (string Members, string Body) Code(Form form)
    {
        var op = Removing.Contains(form) ? "-" : "+";
        static string Call(string expression) => $"var c = (Action){expression};\n{Invoke("c")}";
        return form switch
        {
            Form.Plus => ("", $"var c = a + b;\n{Invoke("c")}"),
            Form.Minus => ("", $"var c = a - b;\n{Invoke("c")}"),
            Form.LocalCompound or Form.LocalRemove => ("", $"Action c = a;\nc {op}= b;\n{Invoke("c")}"),
            Form.ParameterCompound or Form.ParameterRemove =>
                ($"private void Use(Action c, Action b)\n{{\nc {op}= b;\n{Invoke("c")}\n}}", "Use(a, b);"),
            Form.RefLocalCompound or Form.RefLocalRemove => (FIELD, $"ref Action r = ref _c;\n_c = a;\nr {op}= b;\n{Invoke("r")}"),
            Form.FieldCompound or Form.FieldRemove => (FIELD, $"_c = a;\n_c {op}= b;\n{Invoke("_c")}"),
            Form.AutoPropertyCompound or Form.AutoPropertyRemove => (AUTO_PROPERTY, $"C = a;\nC {op}= b;\n{Invoke("C")}"),
            Form.AccessorPropertyCompound or Form.AccessorPropertyRemove => (PROPERTY, $"P = a;\nP {op}= b;\n{Invoke("P")}"),
            Form.IndexerCompound or Form.IndexerRemove => (INDEXER, $"this[0] = a;\nthis[0] {op}= b;\n{Invoke("this[0]")}"),
            Form.CapturedLongFormCompound or Form.CapturedLongFormRemove =>
                (PROPERTY + "\n" + FLAG, $"P = a;\nP {op}= _flag ? b : b;\n{Invoke("P")}"),
            Form.ElementCompound or Form.ElementRemove => ("", $"var cells = new Action[1];\ncells[0] = a;\ncells[0] {op}= b;\n{Invoke("cells[0]")}"),
            Form.CapturedLocalCompound or Form.CapturedLocalRemove => ("", $"Action c = a;\nAction f = () => c {op}= b;\nf();\n{Invoke("c")}"),
            Form.CombinePair => ("", Call("Delegate.Combine(a, b)")),
            Form.CombineExpandedParams => ("", Call("Delegate.Combine(a, b, null)")),
            Form.CombineCollection => ("", Call("Delegate.Combine([a, b])")),
            Form.CombineArrayCreation => ("", Call("Delegate.Combine(new Delegate[] { a, b })")),
            Form.CombineArray => ("", "var arr = new Delegate[] { a, b };\n" + Call("Delegate.Combine(arr)")),
            Form.Remove => ("", Call("Delegate.Remove(a, b)")),
            Form.RemoveAll => ("", Call("Delegate.RemoveAll(a, b)")),
            Form.RemoveNamed => ("", Call("Delegate.Remove(value: b, source: a)")),
            Form.RemoveAllNamed => ("", Call("Delegate.RemoveAll(value: b, source: a)")),
            Form.RemoveAfterCombine => (FIELD, $"_c = a + b;\n_c -= b;\n{Invoke("_c")}"),
            Form.CombineSpread => ("", "var arr = new Delegate[] { a, b };\n" + Call("Delegate.Combine([.. arr])")),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
        };
    }

    /// <summary>The twin of an operand of a form: the members it needs and its statements over the local <c>x</c>, which they put alone,
    /// by plain assignment, where the form puts that operand, and invoke as the form invokes its result, marked.</summary>
    /// <param name="form">The form.</param>
    /// <param name="side">The side the operand stands on.</param>
    private static (string Members, string Body) TwinCode(Form form, Side side) => form switch
    {
        Form.ParameterCompound or Form.ParameterRemove => ($"private void Use(Action c)\n{{\n{Invoke("c")}\n}}", "Use(x);"),
        // The form stores its result through the ref local and reads it through the ref local.
        Form.RefLocalCompound or Form.RefLocalRemove => (FIELD, $"ref Action r = ref _c;\nr = x;\n{Invoke("r")}"),
        Form.FieldCompound or Form.FieldRemove or Form.RemoveAfterCombine => (FIELD, $"_c = x;\n{Invoke("_c")}"),
        Form.AutoPropertyCompound or Form.AutoPropertyRemove => (AUTO_PROPERTY, $"C = x;\n{Invoke("C")}"),
        Form.AccessorPropertyCompound or Form.AccessorPropertyRemove => (PROPERTY, $"P = x;\n{Invoke("P")}"),
        Form.IndexerCompound or Form.IndexerRemove => (INDEXER, $"this[0] = x;\n{Invoke("this[0]")}"),
        Form.CapturedLongFormCompound or Form.CapturedLongFormRemove =>
            (PROPERTY + "\n" + FLAG, (side == Side.Left ? "P = x;\n" : "P = _flag ? x : x;\n") + Invoke("P")),
        Form.ElementCompound or Form.ElementRemove => ("", $"var cells = new Action[1];\ncells[0] = x;\n{Invoke("cells[0]")}"),
        // The left operand is the captured local's value before the lambda runs; the right one reaches it inside the lambda.
        Form.CapturedLocalCompound or Form.CapturedLocalRemove => side == Side.Left
            ? ("", $"Action c = x;\nAction f = () => {{ var held = c; c = held; }};\nf();\n{Invoke("c")}")
            : ("", $"Action c = null;\nAction f = () => c = x;\nf();\n{Invoke("c")}"),
        // The array's cells are what the combination reads its operands from.
        Form.CombineArray => ("", $"var arr = new Delegate[] {{ x }};\nvar c = (Action)arr[0];\n{Invoke("c")}"),
        Form.CombineSpread => throw new ArgumentOutOfRangeException(nameof(form), form, "an opaque call has no twins"),
        _ => ("", $"Action c = x;\n{Invoke("c")}")
    };
}
