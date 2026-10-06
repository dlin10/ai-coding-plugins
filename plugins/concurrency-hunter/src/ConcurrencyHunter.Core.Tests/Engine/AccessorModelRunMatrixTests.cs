using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The accessor-model matrix in the run (R1, R4, R5): every form that calls an accessor of a library whose members have no body
/// in the run, crossed with the forms of a project model of the accessors it calls. Each cell writes one project model file for the
/// accessors it calls and runs the analyzer over a program whose controller action <c>Act</c> runs the cell's shape on a singleton's
/// objects. A second table checks the library-effect binder's other branches against method twins given the same model.</summary>
public sealed class AccessorModelRunMatrixTests
{
    /// <summary>The root symbol of the calling action.</summary>
    private const string ACT = "CellController.Act()";

    /// <summary>The root symbol of the second action, which calls <c>bag.Poke()</c>.</summary>
    private const string POKE = "CellController.Poke()";

    /// <summary>The root kind of an unknown execution.</summary>
    private const string UNKNOWN = "unknown-delegate-call";

    /// <summary>How the accessors are called.</summary>
    public enum Shape
    {
        Assign,
        Initializer,
        Coalesce,
        CapturedCoalesce,
        Deconstruct,
        IndexerSet,
        IndexerGet,
        IndexerCoalesce,
        PostIncrement,
        PreIncrement,
        CompoundAdd,
        StaticAssign,
        Subscribe,
        Unsubscribe,
        StaticSubscribe,
        StaticUnsubscribe
    }

    /// <summary>What the project model of the called accessors says.</summary>
    public enum Form
    {
        Effect,
        GetterOnly,
        SetterOnly,
        Keep,
        NotRun,
        None
    }

    /// <summary>A row of the second table: a form of the binder against its method twin.</summary>
    public enum Branch
    {
        WritesArg,
        WritesCells,
        AssignedArray,
        ParamsIndexer,
        ImmutableValue
    }

    /// <summary>The <c>Item</c> objects of the singleton, by the subclass whose allocation they are.</summary>
    private enum Arg
    {
        Item,
        Other,
        Key
    }

    /// <summary>A called accessor: its library type, metadata name, and the <c>Item</c> objects the shape passes it.</summary>
    /// <param name="Type">The library type's name.</param>
    /// <param name="Name">The accessor's metadata name.</param>
    /// <param name="Args">The objects it receives.</param>
    private sealed record Accessor(string Type, string Name, IReadOnlyList<Arg> Args)
    {
        public bool IsGetter => Name.StartsWith("get_", StringComparison.Ordinal);

        public bool IsDelegate => Name.StartsWith("add_", StringComparison.Ordinal) || Name.StartsWith("remove_", StringComparison.Ordinal);
    }

    /// <summary>A cell: a shape and a model form.</summary>
    /// <param name="Shape">How the accessors are called.</param>
    /// <param name="Form">What the model says.</param>
    private sealed record Cell(Shape Shape, Form Form)
    {
        public string Name => $"{Shape}_{Form}";
    }

    /// <summary>What a cell observes in the calling action and the analyzer's coverage.</summary>
    /// <param name="Reads">The objects whose <c>Mark</c> the action reads.</param>
    /// <param name="Unknown">The objects the action makes an unknown-effect access on.</param>
    /// <param name="Kept">The objects whose <c>Mark</c> the action writes through the getter called after the shape.</param>
    /// <param name="HitsRoots">The root kinds and symbols of the handler's writes.</param>
    /// <param name="Opaque">Whether an opaque callee names a called accessor.</param>
    /// <param name="Gap">Whether a semantic gap names a called accessor.</param>
    /// <param name="KnownProject">The count of calls known by a project model.</param>
    private sealed record Observed(IReadOnlySet<Arg> Reads, IReadOnlySet<Arg> Unknown, IReadOnlySet<Arg> Kept, IReadOnlySet<string> HitsRoots,
                                  bool Opaque, bool Gap, int KnownProject);

    private static readonly Shape[] BothAccessors = [Shape.Coalesce, Shape.CapturedCoalesce, Shape.IndexerCoalesce, Shape.PostIncrement,
                                                     Shape.PreIncrement, Shape.CompoundAdd];

    private static readonly Shape[] Delegates = [Shape.Subscribe, Shape.Unsubscribe, Shape.StaticSubscribe, Shape.StaticUnsubscribe];

    private static readonly Shape[] Keepable = [Shape.Assign, Shape.Initializer, Shape.Coalesce, Shape.CapturedCoalesce, Shape.Deconstruct,
                                                Shape.IndexerSet, Shape.IndexerCoalesce, Shape.Subscribe, Shape.Unsubscribe];

    // ---- the cells ----

    private static IEnumerable<Cell> Cells() =>
        from shape in Enum.GetValues<Shape>()
        from form in Enum.GetValues<Form>()
        where Allowed(shape, form)
        select new Cell(shape, form);

    /// <summary>Which forms a shape is crossed with.</summary>
    /// <param name="shape">The shape.</param>
    /// <param name="form">The form.</param>
    private static bool Allowed(Shape shape, Form form) => form switch
    {
        // Every shape calls a modelled and an unmodelled accessor alike.
        Form.Effect or Form.None => true,
        // Only a shape that calls both a getter and a setter can leave one of them without a model.
        Form.GetterOnly or Form.SetterOnly => BothAccessors.Contains(shape),
        // Only a delegate accessor has a delegate to leave not run.
        Form.NotRun => Delegates.Contains(shape),
        // Keeping needs an instance to keep in and an object to keep: not IndexerGet (no value), not the Counter shapes (an int
        // value), not the static shapes (no this).
        Form.Keep => Keepable.Contains(shape),
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
    };

    /// <summary>The accessors a shape calls, with the objects each receives.</summary>
    /// <param name="shape">The shape.</param>
    private static IReadOnlyList<Accessor> Calls(Shape shape) => shape switch
    {
        Shape.Assign or Shape.Deconstruct => [new("Bag", "set_Value", [Arg.Item])],
        Shape.Initializer => [new("Bag", "set_Init", [Arg.Item])],
        Shape.Coalesce => [new("Bag", "get_Value", []), new("Bag", "set_Value", [Arg.Item])],
        Shape.CapturedCoalesce => [new("Bag", "get_Value", []), new("Bag", "set_Value", [Arg.Item, Arg.Other])],
        Shape.IndexerSet => [new("Bag", "set_Item", [Arg.Key, Arg.Item])],
        Shape.IndexerGet => [new("Bag", "get_Item", [Arg.Key])],
        Shape.IndexerCoalesce => [new("Bag", "get_Item", [Arg.Key]), new("Bag", "set_Item", [Arg.Key, Arg.Item])],
        Shape.PostIncrement or Shape.PreIncrement or Shape.CompoundAdd => [new("Counter", "get_Item", [Arg.Key]), new("Counter", "set_Item", [Arg.Key])],
        Shape.StaticAssign => [new("Bag", "set_Shared", [Arg.Item])],
        Shape.Subscribe => [new("Bag", "add_Changed", [])],
        Shape.Unsubscribe => [new("Bag", "remove_Changed", [])],
        Shape.StaticSubscribe => [new("Bag", "add_SharedChanged", [])],
        Shape.StaticUnsubscribe => [new("Bag", "remove_SharedChanged", [])],
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
    };

    /// <summary>The statements of a shape inside <c>Act</c>, over the locals <c>item</c>, <c>other</c>, <c>key</c>, <c>flag</c> and
    /// <c>h</c>.</summary>
    /// <param name="shape">The shape.</param>
    private static string Code(Shape shape) => shape switch
    {
        Shape.Assign => "var bag = _state.Bag; bag.Value = item;",
        Shape.Initializer => "var bag = new Acc.Bag { Init = item };",
        Shape.Coalesce => "var bag = _state.Bag; bag.Value ??= item;",
        Shape.CapturedCoalesce => "var bag = _state.Bag; bag.Value ??= flag ? item : other;",
        Shape.Deconstruct => "var bag = _state.Bag; (bag.Value, _) = (item, 0);",
        Shape.IndexerSet => "var bag = _state.Bag; bag[key] = item;",
        Shape.IndexerGet => "var bag = _state.Bag; _ = bag[key];",
        Shape.IndexerCoalesce => "var bag = _state.Bag; bag[key] ??= item;",
        Shape.PostIncrement => "var counter = _state.Counter; counter[key]++;",
        Shape.PreIncrement => "var counter = _state.Counter; ++counter[key];",
        Shape.CompoundAdd => "var counter = _state.Counter; counter[key] += 1;",
        Shape.StaticAssign => "Acc.Bag.Shared = item;",
        Shape.Subscribe => "var bag = _state.Bag; bag.Changed += h;",
        Shape.Unsubscribe => "var bag = _state.Bag; bag.Changed -= h;",
        Shape.StaticSubscribe => "Acc.Bag.SharedChanged += h;",
        Shape.StaticUnsubscribe => "Acc.Bag.SharedChanged -= h;",
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
    };

    /// <summary>The getter a <c>Keep</c> cell calls after its shape, writing the <c>Mark</c> of what it returns; none for a delegate.</summary>
    /// <param name="shape">The shape.</param>
    private static Accessor? KeptGetter(Shape shape) => shape switch
    {
        Shape.Initializer => new("Bag", "get_Init", []),
        Shape.Assign or Shape.Coalesce or Shape.CapturedCoalesce or Shape.Deconstruct => new("Bag", "get_Value", []),
        Shape.IndexerSet or Shape.IndexerCoalesce => new("Bag", "get_Item", [Arg.Key]),
        _ => null
    };

    /// <summary>The statement that calls <see cref="KeptGetter"/>.</summary>
    /// <param name="shape">The shape.</param>
    private static string KeptCode(Shape shape) => shape switch
    {
        Shape.Initializer => "bag.Init.Mark = 1;",
        Shape.IndexerSet or Shape.IndexerCoalesce => "bag[key].Mark = 1;",
        _ => "bag.Value.Mark = 1;"
    };

    /// <summary>Whether <paramref name="form"/> gives <paramref name="accessor"/> a model, and which.</summary>
    /// <param name="form">The form.</param>
    /// <param name="accessor">The accessor.</param>
    private static Form ModelOf(Form form, Accessor accessor) => form switch
    {
        Form.GetterOnly => accessor.IsGetter ? Form.Effect : Form.None,
        Form.SetterOnly => accessor.IsGetter ? Form.None : Form.Effect,
        _ => form
    };

    // ---- the table ----

    /// <summary>What a cell is expected to observe.</summary>
    /// <param name="cell">The cell.</param>
    private static Observed Expected(Cell cell)
    {
        var (reads, unknown, kept, hits) = (new HashSet<Arg>(), new HashSet<Arg>(), new HashSet<Arg>(), new HashSet<string>(StringComparer.Ordinal));
        var (opaque, gap, known) = (false, false, 0);
        foreach (var accessor in Calls(cell.Shape))
        {
            switch (ModelOf(cell.Form, accessor))
            {
                // R4: a known accessor call binds the model's effects to the arguments it receives, by parameter, in the caller's execution.
                case Form.Effect:
                    reads.UnionWith(accessor.Args);
                    known++;
                    // R1: a delegate the model's fate runs now runs in the calling action.
                    if (accessor.IsDelegate)
                        hits.Add($"root:{ACT}");
                    break;
                // R4, R5: a setter keeping its value hands it back through the getter of kept:this, and reads nothing.
                case Form.Keep when !accessor.IsDelegate:
                    known++;
                    kept.UnionWith(accessor.Args.Where(arg => arg != Arg.Key));
                    break;
                // R1, R5: a holder this runs the delegate it keeps at every call of the holder's members without a body: Poke() in a
                // second action, and the accessor call itself.
                case Form.Keep:
                    known++;
                    hits.UnionWith([$"root:{ACT}", $"root:{POKE}"]);
                    break;
                // R5: not-run neither runs nor keeps the delegate; the call is known and no gap.
                case Form.NotRun:
                    known++;
                    break;
                // R1, R4: without a model the call stays opaque: an unknown-effect access on every object it receives, the handler handed
                // to an unknown execution, and a semantic gap where it is handed a delegate or a shared object (TD-034) — every Item
                // argument is a singleton's, so a parameterless getter is the one opaque accessor that is no gap.
                case Form.None:
                    unknown.UnionWith(accessor.Args);
                    opaque = true;
                    gap |= accessor.IsDelegate || accessor.Args.Count > 0;
                    if (accessor.IsDelegate)
                        hits.Add($"{UNKNOWN}:");
                    break;
            }
        }

        // R4: a Keep cell also calls its getter after the shape, a known call whose result is kept:this.
        if (cell.Form == Form.Keep && KeptGetter(cell.Shape) is not null)
            known++;
        return new Observed(reads, unknown, kept, hits, opaque, gap, known);
    }

    // ---- the tests ----

    [Fact]
    public async Task Every_cell_applies_its_model()
    {
        var cells = Cells().ToArray();
        var observed = await Task.WhenAll(cells.Select(cell => Task.Run(() => Observe(cell))));
        var failures = new List<string>();
        for (var i = 0; i < cells.Length; i++)
        {
            var (expected, actual) = (Expected(cells[i]), observed[i]);
            if (!expected.Reads.SetEquals(actual.Reads))
                failures.Add($"{cells[i].Name}: reads of Mark on [{Show(actual.Reads)}], expected [{Show(expected.Reads)}]");
            if (!expected.Unknown.SetEquals(actual.Unknown))
                failures.Add($"{cells[i].Name}: unknown-effect accesses on [{Show(actual.Unknown)}], expected [{Show(expected.Unknown)}]");
            if (!expected.Kept.SetEquals(actual.Kept))
                failures.Add($"{cells[i].Name}: the getter after the shape returns [{Show(actual.Kept)}], expected [{Show(expected.Kept)}]");
            if (!expected.HitsRoots.SetEquals(actual.HitsRoots))
                failures.Add($"{cells[i].Name}: the handler writes in [{Show(actual.HitsRoots)}], expected [{Show(expected.HitsRoots)}]");
            if (expected.Opaque != actual.Opaque)
                failures.Add($"{cells[i].Name}: {(actual.Opaque ? "an" : "no")} opaque call of a called accessor");
            if (expected.Gap != actual.Gap)
                failures.Add($"{cells[i].Name}: {(actual.Gap ? "a" : "no")} semantic gap names a called accessor");
            if (expected.KnownProject != actual.KnownProject)
                failures.Add($"{cells[i].Name}: {actual.KnownProject} call(s) known by a project model, expected {expected.KnownProject}");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} cell failure(s):\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task Every_binding_branch_matches_its_method_twin()
    {
        var branches = Enum.GetValues<Branch>();
        var runs = await Task.WhenAll(branches.Select(branch => Task.Run(async () =>
        {
            using var repo = new CellModelRepository(BranchModels(branch));
            var accessor = await PhaseOneAnalyzer.AnalyzeAsync(Solution(BranchCode(branch, twin: false)), ROOT_DIRECTORY, repo.Root, CancellationToken.None);
            var twin = await PhaseOneAnalyzer.AnalyzeAsync(Solution(BranchCode(branch, twin: true)), ROOT_DIRECTORY, repo.Root, CancellationToken.None);
            return (accessor, twin);
        })));

        var failures = new List<string>();
        for (var i = 0; i < branches.Length; i++)
        {
            var (branch, (accessor, twin)) = (branches[i], runs[i]);
            foreach (var result in new[] { accessor, twin })
                Assert.Equal(0, Assert.Single(result.Coverage).Skips.GetValueOrDefault(CoverageCounters.MODEL_ENTRY_REJECTED));
            var (ours, theirs) = (Projection(accessor), Projection(twin));
            if (!ours.SetEquals(theirs))
                failures.Add($"{branch}: accessor [{Show(ours)}], method twin [{Show(theirs)}]");
            var acts = Act(accessor);
            // R4: each branch binds the model's effect to the value the accessor is given, as a method's argument.
            var holds = branch switch
            {
                Branch.WritesArg => acts.Any(access => access.Operation == AccessOperation.Write && Is(access, Arg.Item)),
                Branch.WritesCells => acts.Any(access => access.Operation == AccessOperation.Write && access.Resource.Region == "di:State@Singleton" &&
                                                         access.Resource.AccessPath[0] == "Cells" && access.Resource.Selector is not null),
                // No field holds the created array, so its cells are no resource an access could name: the write is the cell write
                // effect bound to the array the call is handed, in the accessor's call and in its twin's.
                Branch.AssignedArray => WritesCreatedArray(BranchCode(branch, twin: false), "set_Cells") &&
                                        WritesCreatedArray(BranchCode(branch, twin: true), "SetCells"),
                Branch.ParamsIndexer => new[] { Arg.Item, Arg.Other }.All(arg => acts.Any(access => access.Operation == AccessOperation.Read && access.Resource.Member.Name == "Mark" && Is(access, arg))),
                Branch.ImmutableValue => !acts.Any(access => access.Resource.Region.Contains("String", StringComparison.OrdinalIgnoreCase)),
                _ => throw new ArgumentOutOfRangeException(nameof(branch), branch, null)
            };
            if (!holds)
                failures.Add($"{branch}: the binding does not hold; accesses [{Show(Projection(accessor))}]");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} row failure(s):\n" + string.Join("\n", failures));
    }

    [Fact]
    public void The_matrix_has_exactly_the_allowed_cells()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shape in Enum.GetValues<Shape>())
        {
            expected.Add($"{shape}_{Form.Effect}");
            expected.Add($"{shape}_{Form.None}");
        }

        foreach (var shape in new[] { "Coalesce", "CapturedCoalesce", "IndexerCoalesce", "PostIncrement", "PreIncrement", "CompoundAdd" })
        {
            expected.Add($"{shape}_GetterOnly");
            expected.Add($"{shape}_SetterOnly");
        }

        foreach (var shape in new[] { "Subscribe", "Unsubscribe", "StaticSubscribe", "StaticUnsubscribe" })
            expected.Add($"{shape}_NotRun");
        foreach (var shape in new[] { "Assign", "Initializer", "Coalesce", "CapturedCoalesce", "Deconstruct", "IndexerSet", "IndexerCoalesce", "Subscribe", "Unsubscribe" })
            expected.Add($"{shape}_Keep");

        Assert.Equal((16, 6, 5), (Enum.GetValues<Shape>().Length, Enum.GetValues<Form>().Length, Enum.GetValues<Branch>().Length));
        Assert.Equal(16 * 2 + 6 * 2 + 4 + 9, expected.Count);
        Assert.Equal(expected.Order(StringComparer.Ordinal), Cells().Select(cell => cell.Name).Order(StringComparer.Ordinal));
    }

    // ---- observing ----

    /// <summary>Runs the analyzer over one cell with its model file in a repository and observes it.</summary>
    /// <param name="cell">The cell.</param>
    private static async Task<Observed> Observe(Cell cell)
    {
        using var repo = new CellModelRepository(CellModels(cell));
        var result = await PhaseOneAnalyzer.AnalyzeAsync(Solution(CellCode(cell)), ROOT_DIRECTORY, repo.Root, CancellationToken.None);
        var coverage = Assert.Single(result.Coverage);
        Assert.Equal(0, coverage.Skips.GetValueOrDefault(CoverageCounters.MODEL_ENTRY_REJECTED));
        var acts = Act(result);
        var marks =acts.Where(access => access.Resource.Member.Name == "Mark").ToArray();
        var accessors = Calls(cell.Shape).Select(call => call.Name).ToArray();
        bool Names(string callee) => accessors.Any(name => callee.Contains(name, StringComparison.Ordinal));
        return new Observed(
            Args(marks.Where(access => access.Operation == AccessOperation.Read)),
            Args(acts.Where(access => access.Operation is AccessOperation.UnknownEffect or AccessOperation.AtomicUnknownEffect)),
            Args(marks.Where(access => access.Operation == AccessOperation.Write)),
            result.Accesses.Where(access => !access.IsConstructionLocal && access.Resource.Member.Name == "Hits" && access.Operation != AccessOperation.Read)
                  .Select(access => access.Root.RootKind == UNKNOWN ? $"{UNKNOWN}:" : $"root:{access.Root.Symbol}")
                  .ToHashSet(StringComparer.Ordinal),
            coverage.TopOpaqueCallees.Any(callee => Names(callee.Callee)),
            coverage.Gaps.Any(gap => Names(gap.Callee)),
            coverage.Skips.GetValueOrDefault(CoverageCounters.KNOWN_CALL_PROJECT));
    }

    /// <summary>Whether the engine's one call of <paramref name="member"/> in <c>Act</c> binds its cell-write effect to exactly the array
    /// <c>Act</c> creates for it, the call's own argument, and the heap has that array's allocation.</summary>
    /// <param name="act">The statements of <c>Act</c>.</param>
    /// <param name="member">The called member's metadata name.</param>
    private static bool WritesCreatedArray(string act, string member)
    {
        var solution = Solution(act);
        var run = AnalyzeScope(solution, "scope:Fixture", ModelCellFixture.Resolve(solution, BranchModels(Branch.AssignedArray)));
        var heap = run.Execution.Heap;
        var body = Assert.Single(heap.Program.Result.Bodies.Values, body => body.MethodSymbol == ACT);
        var operations = body.Blocks.SelectMany(block => block.Operations).ToArray();
        var call = Assert.Single(operations.OfType<IrCallOperation>(), call => call.Library?.MemberId.Contains($".{member}(", StringComparison.Ordinal) == true);
        var effect = Assert.Single(call.Library!.Effects, effect => effect.Kind == IrLibraryEffectKind.WriteCells);
        var value = Assert.Single(effect.Arguments).Value;
        var allocation = operations.OfType<IrAllocateOperation>().SingleOrDefault(operation => operation.ResultValue == value);
        return allocation is not null && value == call.ArgumentAt(0) &&
               heap.Heap.Regions.Values.Any(region => region.Display.StartsWith($"alloc:{ACT}#", StringComparison.Ordinal));
    }

    /// <summary>The accesses that can pair whose root is the calling action and whose execution is the action's own.</summary>
    /// <param name="result">The analyzer's result.</param>
    private static Access[] Act(AnalysisResult result) =>
        result.Accesses.Where(access => !access.IsConstructionLocal && access.Root.Symbol == ACT && access.Root.RootKind != UNKNOWN).ToArray();

    private static IReadOnlySet<Arg> Args(IEnumerable<Access> accesses) =>
        accesses.SelectMany(access => Enum.GetValues<Arg>().Where(arg => Is(access, arg))).ToHashSet();

    /// <summary>Whether an access is on the allocation of <paramref name="arg"/>: the singleton's field of that subclass of <c>Item</c>.</summary>
    /// <param name="access">The access.</param>
    /// <param name="arg">The object.</param>
    private static bool Is(Access access, Arg arg) => access.Resource.Region.EndsWith($"#{arg}Item", StringComparison.Ordinal);

    /// <summary>The accesses of <c>Act</c> by kind, object, member and execution.</summary>
    /// <param name="result">The analyzer's result.</param>
    private static IReadOnlySet<string> Projection(AnalysisResult result) =>
        result.Accesses.Where(access => !access.IsConstructionLocal && access.Root.Symbol == ACT)
              .Select(access => $"{access.Operation}|{access.Resource.Region}|{string.Join('.', access.Resource.AccessPath)}|{access.Resource.Selector?.Text}|{access.Root.RootKind}")
              .ToHashSet(StringComparer.Ordinal);

    private static string Show<T>(IEnumerable<T> values) => string.Join(", ", values.Select(value => value!.ToString()).Order(StringComparer.Ordinal));

    // ---- the models ----

    /// <summary>The project model file of a cell: one entry for every accessor it calls that its form gives a model.</summary>
    /// <param name="cell">The cell.</param>
    private static string CellModels(Cell cell)
    {
        var calls = Calls(cell.Shape).ToList();
        if (cell.Form == Form.Keep && KeptGetter(cell.Shape) is { } getter && !calls.Any(call => call.Name == getter.Name))
            calls.Add(getter);
        var entries = calls.Select(call => (call, model: ModelOf(cell.Form, call)))
                           .Where(pair => pair.model != Form.None)
                           .Select(pair => Entry(pair.call.Type, pair.call.Name, Decision(pair.model, pair.call)));
        return File(entries);
    }

    /// <summary>The clauses a form gives an accessor.</summary>
    /// <param name="form">The accessor's own form.</param>
    /// <param name="accessor">The accessor.</param>
    private static string Decision(Form form, Accessor accessor)
    {
        var parameters = Member(accessor.Type, accessor.Name).Parameters;
        return form switch
        {
            Form.Effect when accessor.IsDelegate => "\"effects\":{},\"fates\":{\"value\":{\"fate\":\"invoke-now\"}}",
            Form.Effect => "\"effects\":{" + string.Join(",", parameters.Where(parameter => parameter.Type.Name == "Item")
                                                                        .Select(parameter => $"\"{parameter.Name}\":[\"reads-deep\"]")) + "}",
            Form.Keep when accessor.IsDelegate => "\"effects\":{},\"fates\":{\"value\":{\"fate\":\"holder\",\"holder\":\"this\"}}",
            Form.Keep when accessor.IsGetter => "\"effects\":{},\"result\":\"[kept:this]\"",
            Form.Keep => "\"effects\":{},\"keeps\":{\"this\":[\"arg:value\"]}",
            Form.NotRun => "\"effects\":{},\"fates\":{\"value\":{\"fate\":\"not-run\"}}",
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
        };
    }

    /// <summary>The project model file of a second-table row: the accessor and its method twin, given the same model.</summary>
    /// <param name="branch">The row.</param>
    private static string BranchModels(Branch branch)
    {
        var (type, accessor, method, decision) = branch switch
        {
            Branch.WritesArg => ("Bag", "set_Value", "SetValue", "\"effects\":{\"value\":[\"writes-arg\"]}"),
            Branch.WritesCells or Branch.AssignedArray => ("Bag", "set_Cells", "SetCells", "\"effects\":{\"value\":[\"writes-cells\"]}"),
            Branch.ParamsIndexer => ("Multi", "get_Item", "Get", "\"effects\":{\"keys\":[\"reads-deep\"]}"),
            Branch.ImmutableValue => ("Bag", "set_Obj", "SetObj", "\"effects\":{\"value\":[\"reads-deep\"]}"),
            _ => throw new ArgumentOutOfRangeException(nameof(branch), branch, null)
        };
        return File([Entry(type, accessor, decision), Entry(type, method, decision)]);
    }

    private static string Entry(string type, string member, string decision) => "{\"member\":\"" + Id(type, member) + "\"," + decision + "}";

    private static string File(IEnumerable<string> entries) =>
        "{\"schemaVersion\":1,\"assemblies\":[\"Acc\"],\"models\":[" + string.Join(",", entries) + "]}";

    /// <summary>The declaration id of the one member of <c>Acc.<paramref name="type"/></c> with that metadata name.</summary>
    /// <param name="type">The library type's name.</param>
    /// <param name="member">The member's metadata name.</param>
    private static string Id(string type, string member) => DocumentationCommentId.CreateDeclarationId(Member(type, member))!;

    private static IMethodSymbol Member(string type, string member) =>
        LibrarySource.Value.GetTypeByMetadataName("Acc." + type)!.GetMembers(member).OfType<IMethodSymbol>().Single();

    // ---- the fixture ----

    /// <summary>The statements of <c>Act</c> for a cell: its shape, and for a <c>Keep</c> cell the getter call after it.</summary>
    /// <param name="cell">The cell.</param>
    private static string CellCode(Cell cell) =>
        Code(cell.Shape) + (cell.Form == Form.Keep && KeptGetter(cell.Shape) is not null ? " " + KeptCode(cell.Shape) : "");

    /// <summary>The statements of <c>Act</c> for a second-table row or its method twin.</summary>
    /// <param name="branch">The row.</param>
    /// <param name="twin">Whether to call the method twin instead of the accessor.</param>
    private static string BranchCode(Branch branch, bool twin) => (branch, twin) switch
    {
        (Branch.WritesArg, false) => "var bag = _state.Bag; bag.Value = item;",
        (Branch.WritesArg, true) => "var bag = _state.Bag; bag.SetValue(item);",
        (Branch.WritesCells, false) => "var bag = _state.Bag; var cells = _state.Cells; bag.Cells = cells;",
        (Branch.WritesCells, true) => "var bag = _state.Bag; var cells = _state.Cells; bag.SetCells(cells);",
        (Branch.AssignedArray, false) => "var bag = _state.Bag; bag.Cells = new[] { 1, 2 };",
        (Branch.AssignedArray, true) => "var bag = _state.Bag; bag.SetCells(new[] { 1, 2 });",
        (Branch.ParamsIndexer, false) => "var multi = _state.Multi; _ = multi[item, other];",
        (Branch.ParamsIndexer, true) => "var multi = _state.Multi; _ = multi.Get(item, other);",
        (Branch.ImmutableValue, false) => "var bag = _state.Bag; bag.Obj = \"text\";",
        (Branch.ImmutableValue, true) => "var bag = _state.Bag; bag.SetObj(\"text\");",
        _ => throw new ArgumentOutOfRangeException(nameof(branch), branch, null)
    };

    private static Solution Solution(string act) =>
        FixtureSolution.Create(new FixtureOptions { MetadataReferences = [Library.Value] }, ("Case.cs", Usings + Source(act)));

    /// <summary>A singleton <c>State</c> of library objects and <c>Item</c> objects, one allocation of its own subclass each, a singleton
    /// <c>Tally</c> the handler <c>h</c> writes, and a controller whose action <c>Act</c> runs <paramref name="act"/> and whose action
    /// <c>Poke</c> calls the singleton bag's <c>Poke()</c>.</summary>
    /// <param name="act">The statements of <c>Act</c>.</param>
    private static string Source(string act) => $$"""
        public class ItemItem : Acc.Item { public new int Mark; }
        public class OtherItem : Acc.Item { public new int Mark; }
        public class KeyItem : Acc.Item { public new int Mark; }

        public sealed class Tally { public int Hits; }

        public sealed class State
        {
            public readonly Acc.Bag Bag = new Acc.Bag();
            public readonly Acc.Counter Counter = new Acc.Counter();
            public readonly Acc.Multi Multi = new Acc.Multi();
            public readonly ItemItem Item = new ItemItem();
            public readonly OtherItem Other = new OtherItem();
            public readonly KeyItem Key = new KeyItem();
            public readonly int[] Cells = new int[2];
            public bool Flag;
        }

        public sealed class CellController(State state, Tally tally) : ControllerBase
        {
            private readonly State _state = state;
            private readonly Tally _tally = tally;

            public void Act()
            {
                Acc.Item item = _state.Item, other = _state.Other, key = _state.Key;
                var flag = _state.Flag;
                Action h = () => _tally.Hits++;
                {{act}}
            }

            public void Poke() => _state.Bag.Poke();
        }
        """ + Startup("services.AddSingleton<State>(); services.AddSingleton<Tally>();");

    private static readonly Lazy<CSharpCompilation> LibrarySource = new(() => CSharpCompilation.Create("Acc", [CSharpSyntaxTree.ParseText("""
        using System;

        namespace Acc
        {
            public class Item
            {
                public int Mark;
            }

            public sealed class Bag
            {
                public Item Value { get; set; }
                public Item this[Item key] { get => null; set { } }
                public Item Init { get; init; }
                public static Item Shared { get; set; }
                public event Action Changed;
                public static event Action SharedChanged;
                public void Poke() { }
                public object Obj { get; set; }
                public int[] Cells { get; set; }
                public void SetValue(Item value) { }
                public void SetObj(object value) { }
                public void SetCells(int[] value) { }
            }

            public sealed class Counter
            {
                public int this[Item key] { get => 0; set { } }
            }

            public sealed class Multi
            {
                public Item this[params Item[] keys] => null;
                public Item Get(params Item[] keys) => null;
            }
        }
        """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));

    /// <summary>The library whose members have no body in the run.</summary>
    private static readonly Lazy<MetadataReference> Library = new(() =>
    {
        using var stream = new MemoryStream();
        var emitted = LibrarySource.Value.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });
}
