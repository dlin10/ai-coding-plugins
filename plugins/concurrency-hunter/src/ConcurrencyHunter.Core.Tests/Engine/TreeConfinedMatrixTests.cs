using System.Collections.Concurrent;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>
/// The classification of a region the creator makes and the work it starts reaches, and its pairs (R4, R5, ADR 0020). The region is an
/// object's field, a captured <c>int</c> or a captured struct's field; the creator reaches it through the local holding its <c>new</c>, or
/// its own captured variable, so its writes are fresh, and the work reaches it through a capture. Published — the object or a delegate
/// capturing the closure stored into a singleton or a static, or the object's reference copied — it is <c>Escaped</c>; otherwise reached by
/// its creators only <c>ThreadConfined</c>; otherwise, made by two executions, <c>Escaped</c>; otherwise <c>TreeConfined</c>, handed to an
/// unresolved call or its captured value copied included. A <c>TreeConfined</c> region pairs exactly what overlaps in one run, and so does
/// an <c>Escaped</c> one a hosted worker, which runs once, makes; one an HTTP action, a <c>Parallel</c> body or two roots make pairs every
/// accessor pair but the creator's two fresh writes.
/// </summary>
public sealed class TreeConfinedMatrixTests
{
    public enum Region
    {
        ObjectField,
        CapturedInt,
        CapturedStructField
    }

    public enum Accessors
    {
        CreatorOnly,
        CreatorBetweenStartAndAwait,
        CreatorAfterAwait,
        TwoSiblings,
        LoopedWork
    }

    public enum Publication
    {
        None,
        StoredInSingleton,
        StoredInStatic,
        HandedToUnresolvedCall,
        ValueCopiedToSingleton
    }

    public enum Creator
    {
        HttpAction,
        HostedWorker,
        ParallelBody,
        HelperOfTwoRoots
    }

    public sealed record Cell(Region Region, Accessors Accessors, Publication Publication, Creator Creator)
    {
        public override string ToString() => $"{Region}/{Accessors}/{Publication}/{Creator}";
    }

    /// <summary>The marked writes: the creator's, the work's, and the second sibling's.</summary>
    private const int CREATOR = 1;
    private const int WORK = 2;
    private const int SIBLING = 4;

    private static readonly ConcurrentDictionary<Cell, Observed> RUNS = new();

    public static IEnumerable<Cell> Cells() =>
        from region in Enum.GetValues<Region>()
        from accessors in Enum.GetValues<Accessors>()
        from publication in Enum.GetValues<Publication>()
        from creator in Enum.GetValues<Creator>()
        select new Cell(region, accessors, publication, creator);

    private static OwnershipKind ExpectedKind(Cell cell) =>
        cell.Publication is Publication.StoredInSingleton or Publication.StoredInStatic ||
        cell is { Publication: Publication.ValueCopiedToSingleton, Region: Region.ObjectField } ? OwnershipKind.Escaped
        : cell.Accessors == Accessors.CreatorOnly ? OwnershipKind.ThreadConfined
        : cell.Creator == Creator.HelperOfTwoRoots ? OwnershipKind.Escaped
        : OwnershipKind.TreeConfined;

    /// <summary>The pairs of marked writes that overlap in one run of the creator.</summary>
    /// <param name="accessors">Who accesses the region.</param>
    private static (int, int)[] InOneRun(Accessors accessors) => accessors switch
    {
        Accessors.CreatorBetweenStartAndAwait => [(CREATOR, WORK)],
        Accessors.TwoSiblings => [(WORK, SIBLING)],
        Accessors.LoopedWork => [(WORK, WORK)],
        _ => []
    };

    /// <summary>The pairs of marked writes when another run of the creator overlaps this one: every accessor pair but the creator's
    /// fresh write against itself.</summary>
    /// <param name="accessors">Who accesses the region.</param>
    private static (int, int)[] AcrossRuns(Accessors accessors) => accessors switch
    {
        Accessors.CreatorBetweenStartAndAwait or Accessors.CreatorAfterAwait => [(CREATOR, WORK), (WORK, WORK)],
        Accessors.TwoSiblings => [(WORK, SIBLING), (WORK, WORK), (SIBLING, SIBLING)],
        Accessors.LoopedWork => [(WORK, WORK)],
        _ => []
    };

    private static (int, int)[] ExpectedPairs(Cell cell) => ExpectedKind(cell) switch
    {
        OwnershipKind.ThreadConfined => [],
        OwnershipKind.TreeConfined => InOneRun(cell.Accessors),
        _ => cell.Creator == Creator.HostedWorker ? InOneRun(cell.Accessors) : AcrossRuns(cell.Accessors)
    };

    [Fact]
    public void Every_cell_is_classified_as_expected()
    {
        var failures = Cells().Select(cell => (Cell: cell, Ownership: Observe(cell).Ownership))
                              .Where(item => item.Ownership.Kind != ExpectedKind(item.Cell))
                              .Select(item => $"{item.Cell}: {item.Ownership.Kind}, expected {ExpectedKind(item.Cell)} ({string.Join("; ", item.Ownership.Evidence)})")
                              .ToList();

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Every_cell_pairs_as_expected()
    {
        var failures = new List<string>();
        foreach (var cell in Cells())
        {
            var observed = Observe(cell);
            var expected = ExpectedPairs(cell).Select(Key).Order(StringComparer.Ordinal).ToArray();
            if (!observed.Pairs.SequenceEqual(expected))
                failures.Add($"{cell}: [{string.Join(", ", observed.Pairs)}], expected [{string.Join(", ", expected)}]");
            // A region its creators alone reach pairs nothing at all, the writes the cell does not mark included.
            if (ExpectedKind(cell) == OwnershipKind.ThreadConfined && observed.AllPairs != 0)
                failures.Add($"{cell}: {observed.AllPairs} pairs on a ThreadConfined region");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void No_pair_within_a_run_is_lost()
    {
        var failures = new List<string>();
        foreach (var cell in Cells().Where(cell => ExpectedKind(cell) != OwnershipKind.ThreadConfined))
        {
            var observed = Observe(cell);
            failures.AddRange(InOneRun(cell.Accessors).Select(Key).Where(pair => !observed.Pairs.Contains(pair)).Select(pair => $"{cell}: {pair}"));
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Every_value_of_every_axis_occurs()
    {
        var cells = Cells().ToArray();

        Assert.Equal(Enum.GetValues<Region>(), cells.Select(cell => cell.Region).Distinct());
        Assert.Equal(Enum.GetValues<Accessors>(), cells.Select(cell => cell.Accessors).Distinct());
        Assert.Equal(Enum.GetValues<Publication>(), cells.Select(cell => cell.Publication).Distinct());
        Assert.Equal(Enum.GetValues<Creator>(), cells.Select(cell => cell.Creator).Distinct());
        Assert.Equal(3 * 5 * 5 * 4, cells.Length);
        Assert.All(Enum.GetValues<OwnershipKind>().Where(kind => kind is OwnershipKind.ThreadConfined or OwnershipKind.TreeConfined or OwnershipKind.Escaped),
                   kind => Assert.Contains(cells, cell => ExpectedKind(cell) == kind));
    }

    // ---- the fixture of a cell ----

    /// <summary>A cell's region ownership, its pairs of marked writes as ordered keys, and how many pairs its path has in all.</summary>
    /// <param name="Ownership">The ownership of the region.</param>
    /// <param name="Pairs">The pairs of marked writes, each as its ordered key.</param>
    /// <param name="AllPairs">How many pairs the region's path has, marked or not.</param>
    private sealed record Observed(RegionOwnership Ownership, IReadOnlyList<string> Pairs, int AllPairs);

    private static string Key((int, int) pair) => $"{Math.Min(pair.Item1, pair.Item2)}x{Math.Max(pair.Item1, pair.Item2)}";

    private static Observed Observe(Cell cell) => RUNS.GetOrAdd(cell, _ =>
    {
        var source = Source(cell);
        var run = Analyze(source);
        var (path, type) = cell.Region switch
        {
            Region.ObjectField => (new[] { "N" }, (string?)"Fixture:Box"),
            Region.CapturedInt => (new[] { "count" }, (string?)null),
            _ => (new[] { "state", "Count" }, (string?)null)
        };
        var heap = run.Execution.Heap.Heap;
        var region = Assert.Single(heap.Regions.Values, region => type is not null
                                                                  ? region.TypeKey == type
                                                                  : region.Kind == ConcurrencyHunter.Heap.HeapRegionKind.Closure &&
                                                                    run.Collection.Accesses.Any(access => access.Resource.RegionId == region.Identity &&
                                                                                                          access.Resource.AccessPath.SequenceEqual(path)));
        var marks = new Dictionary<int, int>();
        foreach (var mark in new[] { CREATOR, WORK, SIBLING })
        {
            if (Write(cell.Region, mark) is var text && (Usings + source).Contains(text, StringComparison.Ordinal))
                marks[Line(source, text)] = mark;
        }

        var pairs = run.Pairs.Pairs.Where(pair => pair.Resource.RegionId == region.Identity && pair.Resource.AccessPath.SequenceEqual(path)).ToArray();
        var marked = pairs.Where(pair => marks.ContainsKey(pair.First.Source.StartLine) && marks.ContainsKey(pair.Second.Source.StartLine))
                          .Select(pair => Key((marks[pair.First.Source.StartLine], marks[pair.Second.Source.StartLine])))
                          .Distinct()
                          .Order(StringComparer.Ordinal)
                          .ToArray();
        return new Observed(run.Execution.Analysis.Ownership[region.Identity], marked, pairs.Length);
    });

    private static int Line(string source, string text) =>
        Array.FindIndex((Usings + source).Split('\n'), line => line.Contains(text, StringComparison.Ordinal)) + 1 is var found and > 0
            ? found
            : throw new InvalidOperationException($"no line holds '{text}'");

    private static string Write(Region region, int value) => region switch
    {
        Region.ObjectField => $"box.N = {value};",
        Region.CapturedInt => $"count = {value};",
        _ => $"state.Count = {value};"
    };

    /// <summary>The region's declaration. A variable is captured, by a lambda nothing runs, whoever else reaches it: one its creator
    /// alone used would be no capture cell at all.</summary>
    /// <param name="region">The region kind.</param>
    private static string Declaration(Region region) => region switch
    {
        Region.ObjectField => "var box = new Box();",
        Region.CapturedInt => "var count = 0; Func<int> peek = () => count;",
        _ => "State state = default; Func<int> peek = () => state.Count;"
    };

    /// <summary>What publishes the region, or does not: the object, or a delegate capturing the closure, stored; the region handed to an
    /// unresolved call, the object itself or a delegate capturing the closure; the variable's value copied.</summary>
    /// <param name="region">The region kind.</param>
    /// <param name="publication">The publication.</param>
    private static string Published(Region region, Publication publication)
    {
        var read = region switch
        {
            Region.ObjectField => "box",
            Region.CapturedInt => "count",
            _ => "state.Count"
        };
        var stored = region == Region.ObjectField ? "box" : $"() => GC.KeepAlive({read})";
        return publication switch
        {
            Publication.StoredInSingleton => $"keeper.{(region == Region.ObjectField ? "Kept" : "Hook")} = {stored};",
            Publication.StoredInStatic => $"Registry.{(region == Region.ObjectField ? "Kept" : "Hook")} = {stored};",
            Publication.HandedToUnresolvedCall => region == Region.ObjectField ? "Sinks.Consumer!.Use(box);" : $"Sinks.Consumer!.Take({stored});",
            Publication.ValueCopiedToSingleton => region switch
            {
                Region.ObjectField => "keeper.Kept = box;",
                Region.CapturedInt => "keeper.Last = count;",
                _ => "keeper.LastState = state;"
            },
            _ => ""
        };
    }

    /// <summary>The creator's code: the region, its publication, and the accesses, waiting with <c>await</c> or, in a <c>Parallel</c> body,
    /// which cannot, with <c>Wait</c>.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="sync">Whether the creator waits synchronously.</param>
    private static string Body(Cell cell, bool sync)
    {
        string Wait(string task) => sync ? $"{task}.Wait();" : $"await {task};";
        string WaitAll(string tasks) => sync ? $"Task.WaitAll({tasks});" : $"await Task.WhenAll({tasks});";
        string Work(int value) => $"Task.Run(() => {{ {Write(cell.Region, value)} }})";
        string[] accesses = cell.Accessors switch
        {
            Accessors.CreatorOnly => [Write(cell.Region, CREATOR)],
            Accessors.CreatorBetweenStartAndAwait => [$"var t = {Work(WORK)};", Write(cell.Region, CREATOR), Wait("t")],
            Accessors.CreatorAfterAwait => [$"var t = {Work(WORK)};", Wait("t"), Write(cell.Region, CREATOR)],
            Accessors.TwoSiblings => [$"var t1 = {Work(WORK)};", $"var t2 = {Work(SIBLING)};", WaitAll("t1, t2")],
            _ => ["var ts = new Task[n];", $"for (var i = 0; i < n; i++) ts[i] = {Work(WORK)};", WaitAll("ts")]
        };
        return $"""
            var n = Environment.ProcessorCount;
            {Declaration(cell.Region)}
            {Published(cell.Region, cell.Publication)}
            {string.Join("\n", accesses)}
            """;
    }

    private static string Source(Cell cell)
    {
        var (creator, registrations) = cell.Creator switch
        {
            Creator.HttpAction => ($$"""
                public class BoxController(Keeper keeper) : ControllerBase
                {
                    public async Task Post()
                    {
                        {{Body(cell, sync: false)}}
                    }
                }
                """, ""),
            Creator.HostedWorker => ($$"""
                public sealed class Worker(Keeper keeper) : BackgroundService
                {
                    protected override async Task ExecuteAsync(CancellationToken token)
                    {
                        {{Body(cell, sync: false)}}
                    }
                }
                """, "services.AddHostedService<Worker>();"),
            // The body calls a method that declares the region: a variable declared in the lambda itself would be a cell of the
            // action's closure, which the action creates.
            Creator.ParallelBody => ($$"""
                public static class Body
                {
                    public static void Run(Keeper keeper)
                    {
                        {{Body(cell, sync: true)}}
                    }
                }
                public class BoxController(Keeper keeper) : ControllerBase
                {
                    public void Post(int[] items) => Parallel.ForEach(items, item => Body.Run(keeper));
                }
                """, ""),
            _ => ($$"""
                public static class Maker
                {
                    public static async Task Run(Keeper keeper)
                    {
                        {{Body(cell, sync: false)}}
                    }
                }
                public static class Shared { public static Task Run(Keeper keeper) => Maker.Run(keeper); }
                public class FirstController(Keeper keeper) : ControllerBase { public Task Post() => Shared.Run(keeper); }
                public class SecondController(Keeper keeper) : ControllerBase { public Task Post() => Shared.Run(keeper); }
                """, "")
        };
        return $$"""
            public sealed class Box { public int N; }
            public struct State { public int Count; }
            public sealed class Keeper { public Box? Kept; public Action? Hook; public int Last; public State LastState; }
            public static class Registry { public static Box? Kept; public static Action? Hook; }
            public interface IConsumer { void Take(Action work); void Use(Box box); }
            public static class Sinks { public static IConsumer? Consumer; }
            {{creator}}
            """ + Startup($"services.AddSingleton<Keeper>(); {registrations}");
    }
}
