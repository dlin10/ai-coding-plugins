using System.Collections.Concurrent;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Ir;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The placement of first and later runs across every run relation, delegate target and record layer.</summary>
public sealed class LaterRunsPlacementMatrixTests
{
    public enum Relation { Concurrent, Sequential, SequentialAwaited }
    public enum Target { AsyncLambda, AsyncMethod, Forwarder, TaskRun }
    public enum Layer { BuiltIn, Project, Generated }

    /// <summary>One combination of the placement axes.</summary>
    /// <param name="Relation">The record's run relation.</param>
    /// <param name="Target">The delegate target.</param>
    /// <param name="Layer">The record layer.</param>
    public sealed record Cell(Relation Relation, Target Target, Layer Layer)
    {
        public override string ToString() => $"{Relation}/{Target}/{Layer}";
    }

    private static readonly Dictionary<Cell, string> KnownMisses = AllCells()
        .Where(cell => cell.Relation == Relation.SequentialAwaited && cell.Target is Target.Forwarder or Target.TaskRun)
        .ToDictionary(cell => cell, _ => "a non-async target keeps every run in the caller's execution (#413)");

    private static readonly ConcurrentDictionary<Cell, Lazy<Observed>> Runs = new();

    public static IEnumerable<Cell> AllCells() =>
        from relation in Enum.GetValues<Relation>()
        from target in Enum.GetValues<Target>()
        from layer in Enum.GetValues<Layer>()
        select new Cell(relation, target, layer);

    public static TheoryData<Relation, Target, Layer> Cells()
    {
        var cells = new TheoryData<Relation, Target, Layer>();
        foreach (var cell in AllCells())
            cells.Add(cell.Relation, cell.Target, cell.Layer);
        return cells;
    }

    [Fact]
    public void There_are_36_cells_covering_every_value_of_every_axis()
    {
        var cells = AllCells().ToArray();
        Assert.Equal(36, cells.Length);
        Assert.Equal(cells.Length, cells.Distinct().Count());
        Assert.All(Enum.GetValues<Relation>(), value => Assert.Equal(12, cells.Count(cell => cell.Relation == value)));
        Assert.All(Enum.GetValues<Target>(), value => Assert.Equal(9, cells.Count(cell => cell.Target == value)));
        Assert.All(Enum.GetValues<Layer>(), value => Assert.Equal(12, cells.Count(cell => cell.Layer == value)));
        Assert.Equal(6, KnownMisses.Count);
        Assert.All(KnownMisses, miss =>
        {
            Assert.Contains(miss.Key, cells);
            Assert.Equal(Relation.SequentialAwaited, miss.Key.Relation);
            Assert.Contains(miss.Key.Target, new[] { Target.Forwarder, Target.TaskRun });
            Assert.False(string.IsNullOrWhiteSpace(miss.Value));
        });
    }

    /// <summary>The first run's synchronous write stays in the calling execution in every cell.</summary>
    /// <param name="relation">The record's run relation.</param>
    /// <param name="target">The delegate target.</param>
    /// <param name="layer">The record layer.</param>
    [Theory]
    [MemberData(nameof(Cells))]
    public void Every_cell_has_the_first_run_in_the_caller(Relation relation, Target target, Layer layer)
    {
        var observed = Observe(new Cell(relation, target, layer));
        Assert.Contains(observed.Sync, access => access.ExecutionId == observed.After.ExecutionId);
    }

    /// <summary>Only sequential-awaited async targets also enter their synchronous write in the call's continuation;
    /// the six non-async targets keep their listed #413 miss.</summary>
    /// <param name="relation">The record's run relation.</param>
    /// <param name="target">The delegate target.</param>
    /// <param name="layer">The record layer.</param>
    [Theory]
    [MemberData(nameof(Cells))]
    public void Every_cell_has_exactly_its_continuation_occurrence(Relation relation, Target target, Layer layer)
    {
        var cell = new Cell(relation, target, layer);
        var observed = Observe(cell);
        var inContinuation = observed.Sync.Any(access => access.ExecutionId == observed.Continuation);
        AssertLaterRun(cell, inContinuation);
        var expected = inContinuation ? new[] { observed.After.ExecutionId, observed.Continuation } : new[] { observed.After.ExecutionId };
        Assert.Equal(expected.Order(StringComparer.Ordinal), observed.Sync.Select(access => access.ExecutionId).Distinct().Order(StringComparer.Ordinal));
    }

    /// <summary>The later synchronous write pairs with the write after the dropped call exactly where it enters the continuation,
    /// except for the six listed #413 misses.</summary>
    /// <param name="relation">The record's run relation.</param>
    /// <param name="target">The delegate target.</param>
    /// <param name="layer">The record layer.</param>
    [Theory]
    [MemberData(nameof(Cells))]
    public void Every_cell_has_exactly_its_pair_after_a_dropped_call(Relation relation, Target target, Layer layer)
    {
        var cell = new Cell(relation, target, layer);
        var observed = Observe(cell);
        var pair = observed.Run.PairsOn("C").Any(pair =>
            observed.Sync.Contains(pair.First) && pair.Second == observed.After ||
            observed.Sync.Contains(pair.Second) && pair.First == observed.After);
        AssertLaterRun(cell, pair);
    }

    /// <summary>Checks a later-run observation, making a newly found known miss fail explicitly.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="actual">Whether the occurrence or pair was found.</param>
    private static void AssertLaterRun(Cell cell, bool actual)
    {
        if (KnownMisses.TryGetValue(cell, out var reason))
            Assert.False(actual, $"{cell}: the known miss is found now; take it off the list: {reason}");
        else
            Assert.Equal(cell.Relation == Relation.SequentialAwaited, actual);
    }

    /// <summary>The run and the accesses used to read its placement.</summary>
    /// <param name="Run">The analyzed program.</param>
    /// <param name="Sync">The occurrences of the synchronous write of 3.</param>
    /// <param name="After">The caller's write of 2 after the dropped call.</param>
    /// <param name="Continuation">The call's continuation, or the forwarded async call's for a forwarder.</param>
    private sealed record Observed(EngineRun Run, Access[] Sync, Access After, string Continuation);

    /// <summary>Analyzes each program once for all three observations.</summary>
    /// <param name="cell">The cell.</param>
    private static Observed Observe(Cell cell) => Runs.GetOrAdd(cell, key => new Lazy<Observed>(() => Analyze(key))).Value;

    /// <summary>Runs the hosted root with one dropped library call and locates the writes by their IR constants.</summary>
    /// <param name="cell">The cell.</param>
    private static Observed Analyze(Cell cell)
    {
        var target = cell.Target switch
        {
            Target.AsyncLambda => "async () => { _state.C = 3; await Task.Yield(); _state.C = 1; }",
            Target.AsyncMethod => "NextAsync",
            Target.Forwarder => "() => NextAsync()",
            _ => "() => { _state.C = 3; return Task.Run(() => { _state.C = 1; }); }"
        };
        var relation = cell.Relation switch
        {
            Relation.Concurrent => "concurrent",
            Relation.Sequential => "sequential",
            _ => "sequential-awaited"
        };
        var members = cell.Target is Target.AsyncMethod or Target.Forwarder
            ? "private async Task NextAsync() { _state.C = 3; await Task.Yield(); _state.C = 1; }" : "";
        var run = AwaitedRunsFixture.Run($"Runs.Lib.Run({target});\n_state.C = 2;", relation, members: members,
                                         builtIn: cell.Layer == Layer.BuiltIn, generated: cell.Layer == Layer.Generated);
        var bodies = run.Execution.Heap.Program.Result.Bodies.Values;
        Access[] Writes(string constant)
        {
            var sites = bodies.SelectMany(body => body.Blocks.SelectMany(block => block.Operations).OfType<IrStoreFieldOperation>()
                .Where(store => store.Field.Name == "C" && body.Values.Any(value => value.Id == store.Value && value.Kind == IrValueKind.Constant && value.Name == constant))
                .Select(store => (body.BodyId, store.Id))).ToHashSet();
            return run.Of("C").Where(access => access.Operation == AccessOperation.Write && sites.Contains((access.BodyId, access.OperationId))).ToArray();
        }
        var sync = Writes("3");
        Assert.NotEmpty(sync);
        var after = Assert.Single(Writes("2"));
        Assert.Equal(ExecutionKind.Root, Assert.Single(run.Execution.Analysis.Executions, execution => execution.Id == after.ExecutionId).Kind);
        var calls = bodies.SelectMany(body => body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>()
                                                .Select(call => (body.BodyId, Call: call)));
        var site = Assert.Single(calls, site => cell.Target == Target.Forwarder
            ? site.Call.TargetMethodId == sync[0].BodyId
            : site.Call.Library?.MemberId.StartsWith("M:Runs.Lib.Run(", StringComparison.Ordinal) == true);
        var continuation = $"{after.ExecutionId}>async:{site.BodyId}#{site.Call.Id}";
        return new Observed(run, sync, after, continuation);
    }
}
