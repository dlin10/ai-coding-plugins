using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Which cell of a collection an access touches (TD-043), and what the candidate index does with it (TD-075): a proven
/// cell is a resource of its own, a cell nothing proves apart meets every cell it may be, and constants, ranges and slices are
/// compared here without a solver.</summary>
public sealed class ElementSelectorTests
{
    [Fact]
    public void Two_constants_are_two_cells()
    {
        Assert.False(ElementSelector.Exact(0).MayOverlap(ElementSelector.Exact(1)));
        Assert.True(ElementSelector.Exact(3).MayOverlap(ElementSelector.Exact(3)));
    }

    [Fact]
    public void A_constant_and_a_symbol_are_not_two_cells()
    {
        Assert.True(ElementSelector.Exact(0).MayOverlap(ElementSelector.Symbol("body:one:%4")));
        Assert.True(ElementSelector.Symbol("body:one:%4").MayOverlap(ElementSelector.Exact(0)));
    }

    /// <summary>Two names are two values of two executions; that they are named apart proves nothing about what they hold.</summary>
    [Fact]
    public void Two_symbols_are_not_two_cells() =>
        Assert.True(ElementSelector.Symbol("body:one:%4").MayOverlap(ElementSelector.Symbol("body:two:%7")));

    [Fact]
    public void The_unknown_cell_meets_everything()
    {
        Assert.True(ElementSelector.Unknown.MayOverlap(ElementSelector.Exact(7)));
        Assert.True(ElementSelector.Exact(7).MayOverlap(ElementSelector.Unknown));
        Assert.True(ElementSelector.Unknown.MayOverlap(ElementSelector.Range(0, 4)));
        Assert.True(ElementSelector.Unknown.MayOverlap(ElementSelector.Unknown));
    }

    [Fact]
    public void Ranges_that_do_not_meet_are_two_cells()
    {
        Assert.False(ElementSelector.Range(0, 8).MayOverlap(ElementSelector.Range(8, 16)));
        Assert.False(ElementSelector.Range(0, 8).MayOverlap(ElementSelector.Exact(8)));
        Assert.False(ElementSelector.Exact(8).MayOverlap(ElementSelector.Range(0, 8)));
    }

    [Fact]
    public void Ranges_that_meet_are_one_cell()
    {
        Assert.True(ElementSelector.Range(0, 8).MayOverlap(ElementSelector.Range(7, 16)));
        Assert.True(ElementSelector.Range(0, 8).MayOverlap(ElementSelector.Range(2, 4)));
        Assert.True(ElementSelector.Range(0, 8).MayOverlap(ElementSelector.Exact(7)));
    }

    /// <summary>A range that holds no cell proves nothing rather than proving emptiness, which no caller could act on safely.</summary>
    [Fact]
    public void A_range_that_holds_nothing_is_the_unknown_cell()
    {
        Assert.Equal(ElementSelector.Unknown, ElementSelector.Range(4, 4));
        Assert.Equal(ElementSelector.Unknown, ElementSelector.Range(8, 4));
    }

    [Fact]
    public void Each_kind_of_selector_prints_its_own_form()
    {
        Assert.Equal("[0]", ElementSelector.Exact(0).Text);
        Assert.Equal("[0..8]", ElementSelector.Range(0, 8).Text);
        Assert.Equal("[?]", ElementSelector.Symbol("body:one:%4").Text);
        Assert.Equal("[?]", ElementSelector.Unknown.Text);
    }

    [Fact]
    public void A_proven_index_names_the_cell_it_reads_and_writes() =>
        Assert.Equal(["Store Cells[3]", "Load Cells[5]"],
                     Selectors([new IrStoreElementOperation(1, ARRAY, [Constant(3)], Constant(9), Provenance),
                                new IrLoadElementOperation(2, SPAN, ARRAY, [Constant(5)], Provenance)]));

    /// <summary>A slice is compared by the collection it cuts and the offset it starts at, never by the slice object.</summary>
    [Fact]
    public void Slices_with_offsets_that_do_not_meet_are_two_cells()
    {
        var cells = Selectors([Slice(1, ARRAY, 0, 4), new IrStoreElementOperation(2, SPAN, [Constant(0)], Constant(9), Provenance),
                               Slice(3, ARRAY, 4, 4, SPAN + 1), new IrStoreElementOperation(4, SPAN + 1, [Constant(0)], Constant(9), Provenance)]);

        Assert.Equal(["Store Cells[0]", "Store Cells[4]"], cells);
        Assert.False(ElementSelector.Exact(0).MayOverlap(ElementSelector.Exact(4)));
    }

    [Fact]
    public void Slices_with_offsets_that_meet_are_one_cell() =>
        Assert.Equal(["Store Cells[1]", "Store Cells[1]"],
                     Selectors([Slice(1, ARRAY, 0, 4), new IrStoreElementOperation(2, SPAN, [Constant(1)], Constant(9), Provenance),
                                Slice(3, ARRAY, 1, 4, SPAN + 1), new IrStoreElementOperation(4, SPAN + 1, [Constant(0)], Constant(9), Provenance)]));

    [Fact]
    public void A_slice_and_a_direct_index_name_one_cell() =>
        Assert.Equal(["Store Cells[5]", "Store Cells[5]"],
                     Selectors([Slice(1, ARRAY, 4, 4), new IrStoreElementOperation(2, SPAN, [Constant(1)], Constant(9), Provenance),
                                new IrStoreElementOperation(3, ARRAY, [Constant(5)], Constant(9), Provenance)]));

    /// <summary>An index nothing proves, inside a slice of a known length, is still no further than that slice.</summary>
    [Fact]
    public void An_unproven_index_of_a_slice_widens_to_the_slice() =>
        Assert.Equal(["Store Cells[4..8]"],
                     Selectors([Slice(1, ARRAY, 4, 4), new IrStoreElementOperation(2, SPAN, [INDEX], Constant(9), Provenance)]));

    [Fact]
    public void A_slice_whose_offset_is_not_proven_is_the_unknown_cell() =>
        Assert.Equal(["Store Cells[?]"],
                     Selectors([new IrCallOperation(1, SPAN, IrCallKind.Static, "System.MemoryExtensions.AsSpan(int[], int, int)", null,
                                                    [ARRAY, INDEX, Constant(4)], Provenance) { TargetContainingTypeKey = MEMORY_EXTENSIONS },
                                new IrStoreElementOperation(2, SPAN, [Constant(1)], Constant(9), Provenance)]));

    /// <summary>Past the budget the chain is still unwound to the collection it cuts, so the write is widened to the whole
    /// collection rather than dropped.</summary>
    [Fact]
    public void A_chain_of_slices_past_the_budget_is_the_unknown_cell() =>
        Assert.Equal(["Store Cells[?]"],
                     Selectors([Slice(1, ARRAY, 0, 8),
                                Slice(2, SPAN, 1, null, SPAN + 1), Slice(3, SPAN + 1, 1, null, SPAN + 2),
                                Slice(4, SPAN + 2, 1, null, SPAN + 3), Slice(5, SPAN + 3, 1, null, SPAN + 4),
                                new IrStoreElementOperation(6, SPAN + 4, [Constant(0)], Constant(9), Provenance)]));

    /// <summary>Two proven cells of one array are two resources, under one collection.</summary>
    [Fact]
    public void The_cell_is_part_of_the_resource_identity()
    {
        var cells = Cells(Analyze(Holder + Worker("First", "_holder.Cells[0] = 1; _holder.Cells[1] = 2;") +
                                  Startup(Registrations("First"))));

        Assert.Equal(["[0]", "[1]"], cells.Select(access => access.Resource.Selector!.Text).Order(StringComparer.Ordinal));
        Assert.Equal(["Cells|[0]", "Cells|[1]"], cells.Select(access => string.Join("|", access.Resource.AccessPath)).Order(StringComparer.Ordinal));
        Assert.Equal(2, cells.Select(access => access.Resource.Identity).Distinct(StringComparer.Ordinal).Count());
        Assert.Single(cells.Select(access => access.Resource.StructuralIdentity).Distinct(StringComparer.Ordinal));
    }

    /// <summary>The index buckets by the cell, so two proven cells never meet, while a cell nothing proves meets both (TD-075).</summary>
    [Fact]
    public void The_cell_is_part_of_the_bucket()
    {
        var run = Analyze(Holder + Worker("First", "_holder.Cells[0] = 1; _holder.Cells[1] = 2;") +
                          Worker("Second", "_holder.Cells[_holder.Index] = 3;") + Startup(Registrations("First", "Second")));

        var pairs = run.Pairs.Pairs.Where(pair => pair.Resource.Selector is not null).ToArray();
        Assert.All(pairs, pair => Assert.Contains("[?]", Texts(pair)));
        Assert.Equal(["[0]", "[1]"], pairs.Select(pair => Texts(pair).First(text => text != "[?]")).Order(StringComparer.Ordinal));
    }

    /// <summary>Proven-distinct cells are never compared: over two workers writing four cells of one array, the index compares
    /// strictly fewer pairs than the Cartesian bound, and the pairs it does report are exactly the reference's.</summary>
    [Fact]
    public void Proven_cells_are_not_compared_pairwise()
    {
        var run = Analyze(Holder + Worker("First", "_holder.Cells[0] = 1; _holder.Cells[1] = 2;") +
                          Worker("Second", "_holder.Cells[2] = 3; _holder.Cells[3] = 4;") + Startup(Registrations("First", "Second")));
        var reference = ReferencePair(run.Collection.Accesses, run.Execution.Analysis, run.Execution.Heap.Heap);

        Assert.True(run.Pairs.Comparisons < run.Pairs.CartesianBound,
                    $"{run.Pairs.Comparisons} comparisons is not strictly fewer than the Cartesian bound {run.Pairs.CartesianBound}.");
        Assert.Equal(Sites(reference), Sites(run.Pairs));
        Assert.DoesNotContain(run.Pairs.Pairs, pair => pair.Resource.Selector is not null);
    }

    /// <summary>A span is a window on the array it was cut from, and an index of that window is a cell of the array: two windows
    /// that do not meet name no cell in common, so the two writes never pair (TD-043).</summary>
    [Fact]
    public void Writes_through_two_slices_that_do_not_meet_are_two_cells()
    {
        var run = Analyze(Holder + Worker("First", "_holder.Cells.AsSpan(0, 8)[0] = 1;") +
                          Worker("Second", "_holder.Cells.AsSpan(8, 8)[0] = 2;") + Startup(Registrations("First", "Second")));

        Assert.Equal(["[0]", "[8]"], Cells(run).Select(access => access.Resource.Selector!.Text).Order(StringComparer.Ordinal));
        Assert.Empty(run.Pairs.Pairs);
    }

    /// <summary>Windows that overlap share the cells they overlap on, and the offset is what says which cell each index is.</summary>
    [Fact]
    public void Writes_through_two_slices_that_meet_are_one_cell()
    {
        var run = Analyze(Holder + Worker("First", "_holder.Cells.AsSpan(4, 8)[0] = 1;") +
                          Worker("Second", "_holder.Cells.AsSpan(0, 8)[4] = 2;") + Startup(Registrations("First", "Second")));

        Assert.Equal(["[4]", "[4]"], Cells(run).Select(access => access.Resource.Selector!.Text).ToArray());
        Assert.Single(run.Pairs.Pairs);
    }

    /// <summary>The window and the array are one storage: a cell reached through a slice meets the same cell reached directly.</summary>
    [Fact]
    public void A_write_through_a_slice_meets_the_same_cell_written_directly()
    {
        var run = Analyze(Holder + Worker("First", "_holder.Cells.AsSpan(4, 8)[1] = 1;") +
                          Worker("Second", "_holder.Cells[5] = 2;") + Startup(Registrations("First", "Second")));

        Assert.Equal(["[5]", "[5]"], Cells(run).Select(access => access.Resource.Selector!.Text).ToArray());
        Assert.Single(run.Pairs.Pairs);
    }

    [Fact]
    public void A_write_through_a_slice_does_not_meet_another_cell_written_directly()
    {
        var run = Analyze(Holder + Worker("First", "_holder.Cells.AsSpan(4, 8)[1] = 1;") +
                          Worker("Second", "_holder.Cells[9] = 2;") + Startup(Registrations("First", "Second")));

        // Both writes are cells, and they are two: an empty run of accesses would pass this on nothing at all.
        Assert.Equal(["[5]", "[9]"], Cells(run).Select(access => access.Resource.Selector!.Text).Order(StringComparer.Ordinal));
        Assert.Empty(run.Pairs.Pairs);
    }

    /// <summary>The expression handed to the solver is in the array's coordinates, which is where the selector already is: an
    /// index of a window names the cell that many places along, and passing the window's own index would prove two cells that
    /// are one to be two and take a real pair away (TD-092). The solver runs only in the whole pipeline, so this is asked of
    /// the findings and never of the cheap filters.</summary>
    [Fact]
    public async Task A_slice_index_reaches_the_solver_in_the_arrays_own_coordinates()
    {
        var findings = await Findings(Worker("First", "_holder.Cells.AsSpan(4, 8)[1] = _holder.Index;") +
                                      Worker("Second", "_holder.Cells[5] = 2;"));

        Assert.Contains("Cells.[5]", findings);
    }

    [Fact]
    public async Task A_slice_index_the_solver_proves_apart_from_a_direct_one_takes_the_pair_away()
    {
        var findings = await Findings(Worker("First", "_holder.Cells.AsSpan(4, 8)[1] = _holder.Index;") +
                                      Worker("Second", "_holder.Cells[9] = 2;"));

        Assert.DoesNotContain(findings, finding => finding.StartsWith("Cells.[", StringComparison.Ordinal));
    }

    /// <summary>A conversion widens the bits it is given, so it extends by the sign of what it converts: a `byte` holding 200
    /// reaches an `int` as 200, and reading the sign off the type the conversion produces would make it -56 and prove the cell
    /// it names to be another one (TD-094).</summary>
    [Fact]
    public async Task A_widened_unsigned_index_can_still_be_the_cell_it_names()
    {
        var findings = await Findings(Worker("First", "_holder.Wide[(int)_holder.Small] = 1;") +
                                      Worker("Second", "_holder.Wide[200] = 2;"));

        Assert.Contains("Wide.[200]", findings);
    }

    /// <summary>A ref-returning indexer of a type the analysis does not know proves nothing about which cell it hands back — it
    /// may hand back one cell for every index it is given — so two indices of it are one unknown cell and still pair, while the
    /// same two indices of a <c>Span</c> stay two cells (TD-043).</summary>
    [Fact]
    public void Two_indices_of_an_indexer_outside_the_framework_are_not_two_cells()
    {
        const string rack = """
            public sealed class Latch
            {
                private int _cell;
                public ref int this[int index] => ref _cell;
            }

            public sealed class Rack { public readonly Latch Slots = new(); }

            """;
        var run = Analyze(rack + RackWorker("First", "_rack.Slots[0] = 1;") + RackWorker("Second", "_rack.Slots[1] = 2;") +
                          Startup("services.AddSingleton<Rack>(); services.AddHostedService<FirstWorker>(); " +
                                  "services.AddHostedService<SecondWorker>();"));

        Assert.Equal(["[?]", "[?]"], Cells(run).Select(access => access.Resource.Selector!.Text).ToArray());
        Assert.Single(run.Pairs.Pairs);
    }

    private static string RackWorker(string name, string body) => $$"""
        public sealed class {{name}}Worker : BackgroundService
        {
            private readonly Rack _rack;
            public {{name}}Worker(Rack rack) => _rack = rack;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{body}}
                return Task.CompletedTask;
            }
        }

        """;

    /// <summary>The findings of a fixture as the resource each names, through the whole pipeline: the solver is the last filter
    /// and no fixture that stops before it can say anything about what the solver decides.</summary>
    private static async Task<IReadOnlyList<string>> Findings(string workers)
    {
        var result = await PhaseOneAnalyzer.AnalyzeAsync(
            FixtureSolution.Create(("Case.cs", Usings + Holder + workers + Startup(Registrations("First", "Second")))),
            ROOT_DIRECTORY, ProviderRegistry.BuiltIn, CancellationToken.None);
        return result.Findings.Select(finding => string.Join(".", finding.Resource.AccessPath)).Order(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> Texts(AccessPair pair) =>
        [pair.First.Resource.Selector!.Text, pair.Second.Resource.Selector!.Text];

    private static IReadOnlyList<string> Sites(PairAnalysis pairs) =>
        pairs.Pairs.Select(pair => $"{pair.Resource.Identity}|{pair.First.Symbol}|{pair.First.OperationId}|{pair.Second.Symbol}|{pair.Second.OperationId}")
             .Order(StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<Access> Cells(EngineRun run) =>
        run.Collection.Accesses.Where(access => access.Resource.Selector is not null).ToArray();

    private const string Holder = """
        public sealed class Holder
        {
            public readonly int[] Cells = new int[16];
            public readonly int[] Wide = new int[256];
            public int Index;
            public byte Small;
        }

        """;

    private static string Registrations(params string[] workers) =>
        "services.AddSingleton<Holder>(); " + string.Join(" ", workers.Select(name => $"services.AddHostedService<{name}Worker>();"));

    private static string Worker(string name, string body) => $$"""
        public sealed class {{name}}Worker : BackgroundService
        {
            private readonly Holder _holder;
            public {{name}}Worker(Holder holder) => _holder = holder;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{body}}
                return Task.CompletedTask;
            }
        }

        """;

    // Roslyn models an index of a span as a ref-returning indexer, which the IR does not lower to an element operation, so the
    // slices below are written as IR directly: a field load of the array, the calls that cut it, and the element operations.
    private const int RECEIVER = 0;
    private const int ARRAY = 1;
    private const int INDEX = 2;
    private const int SPAN = 20;

    private static readonly IrProvenance Provenance = new(new SourceSpan("Fixture.cs", 10, 2, 10, 8), "Fixture.Rack.Method()",
                                                          "InvocationExpression", "direct");

    private static readonly IrFieldRef Field = new("Fixture", "Fixture.Rack", "Cells", IrFieldKind.Field, false, false, "System.Int32[]");

    /// <summary>The value of a constant: the values 3..12 of the body below hold 0..9.</summary>
    private static int Constant(int number) => 3 + number;

    /// <summary><c>MemoryExtensions.AsSpan(array, start, length)</c> over a value, or <c>Span&lt;T&gt;.Slice(start)</c> over a span.</summary>
    private static IrCallOperation Slice(int id, int array, int start, int? length, int result = SPAN) =>
        array == ARRAY
            ? new IrCallOperation(id, result, IrCallKind.Static, "System.MemoryExtensions.AsSpan(int[], int, int)", null,
                                  length is { } size ? [array, Constant(start), Constant(size)] : [array, Constant(start)], Provenance)
              {
                  TargetContainingTypeKey = MEMORY_EXTENSIONS
              }
            : new IrCallOperation(id, result, IrCallKind.Instance, "System.Span<int>.Slice(int)", array, [Constant(start)], Provenance)
              {
                  TargetContainingTypeKey = SPAN_TYPE
              };

    /// <summary>The declaring types the lowering records for these calls, which is what says their offsets mean what they say.</summary>
    private const string MEMORY_EXTENSIONS = "System.Memory:System.MemoryExtensions";
    private const string SPAN_TYPE = "System.Runtime:System.Span<System.Private.CoreLib:System.Int32>";

    /// <summary>The element accesses a body of the operations below summarizes to, in operation order.</summary>
    private static IReadOnlyList<string> Selectors(IReadOnlyList<IrOperation> operations)
    {
        var values = new List<IrValue>
        {
            new(RECEIVER, IrValueKind.Receiver, "Fixture.Rack", "this", 0),
            new(ARRAY, IrValueKind.Temporary, "System.Int32[]", "cells", 0),
            new(INDEX, IrValueKind.Local, "System.Int32", "index", 0)
        };
        values.AddRange(Enumerable.Range(0, 10).Select(number => new IrValue(Constant(number), IrValueKind.Constant, "System.Int32",
                                                                            number.ToString(), 0)));
        values.AddRange(Enumerable.Range(SPAN, 8).Select(id => new IrValue(id, IrValueKind.Temporary, "System.Span<int>", $"span{id}", 0)));

        var body = new IrBody("body:fixture", IrBodyKind.Method, "Fixture.Rack", "Fixture.Rack.Method()", values,
        [
            new IrBlock(0, IrBlockKind.Entry, 0, [], [], [], null, new IrBranch(IrBranchKind.Regular, 1, null, null, [], [], [])),
            new IrBlock(1, IrBlockKind.Block, 0, [0], [new IrFlowPredecessor(0, IrEdgeKind.Explicit)],
                        [new IrLoadFieldOperation(0, ARRAY, RECEIVER, Field, Provenance), .. operations], null,
                        new IrBranch(IrBranchKind.Regular, 2, null, null, [], [], [])),
            new IrBlock(2, IrBlockKind.Exit, 0, [1], [new IrFlowPredecessor(1, IrEdgeKind.Explicit)], [], null, null)
        ], [new IrRegion(0, IrRegionKind.Root, null, 0, 2, null)]);

        var summary = MethodSummaryBuilder.Build(body, new ProgramIndex("scope:Fixture", [], [], [], []), AnalysisLimits.Default);
        return summary.Accesses.Where(access => access.Selector is not null)
                      .Select(access => $"{access.Kind} {access.Field.Name}{access.Selector!.Text}").ToArray();
    }
}
