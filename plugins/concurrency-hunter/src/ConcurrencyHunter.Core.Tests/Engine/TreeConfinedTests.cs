using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A region one execution creates that, besides its creator, only executions one run of the creator starts reach, and no shared
/// root, is <c>TreeConfined</c> (R5, ADR 0020): its evidence names the creator and those executions, it pairs as if that run were the
/// only one, its unknown effects stay writes, and publication and semantic gaps count it shared. The classification keeps its order:
/// <c>Unknown</c>, <c>Shared</c>, <c>Escaped</c> by reach, <c>Owned</c>, <c>ThreadConfined</c>, then <c>TreeConfined</c>.</summary>
public sealed class TreeConfinedTests
{
    private const string BOX = "alloc:BoxController.Post()#Box";

    [Fact]
    public void An_object_an_action_hands_only_to_its_work_is_tree_confined_and_its_evidence_names_the_creator_and_the_work()
    {
        var run = Analyze("""
            public sealed class Box { public int N; }
            public class BoxController : ControllerBase
            {
                public async Task<int> Post()
                {
                    var box = new Box();
                    await Task.Run(() => { box.N++; });
                    return box.N;
                }
            }
            """ + Startup());

        var ownership = run.Execution.Ownership(BOX);
        Assert.Equal(OwnershipKind.TreeConfined, ownership.Kind);
        var creator = run.Execution.Analysis.Execution(ownership.Creator!);
        Assert.Equal(ExecutionKind.Root, creator.Kind);
        var work = Assert.Single(run.Of("N"), access => access.Operation == AccessOperation.ReadModifyWrite);
        Assert.Equal(creator.Id, run.Execution.Analysis.Execution(work.ExecutionId).ParentId);
        var evidence = Assert.Single(ownership.Evidence);
        Assert.Contains(creator.Display, evidence, StringComparison.Ordinal);
        Assert.Contains(run.Execution.Analysis.Execution(work.ExecutionId).Display, evidence, StringComparison.Ordinal);
        // The work runs once per request and the await orders it before the read: one request's work meets nothing of another's.
        Assert.True(run.Execution.Analysis.RunsOncePerRunOf(work.ExecutionId, creator.Id));
        Assert.Empty(run.PairsOn("N"));
    }

    [Fact]
    public void A_region_a_shared_root_reaches_stays_escaped_and_pairs_across_runs()
    {
        var run = Analyze("""
            public sealed class Box { public int N; }
            public static class Registry { public static Box? Last; }
            public class BoxController : ControllerBase
            {
                public async Task<int> Post()
                {
                    var box = new Box();
                    Registry.Last = box;
                    await Task.Run(() => { box.N++; });
                    return box.N;
                }
            }
            """ + Startup());

        var ownership = run.Execution.Ownership(BOX);
        Assert.Equal(OwnershipKind.Escaped, ownership.Kind);
        Assert.Null(ownership.Creator);
        Assert.NotEmpty(run.PairsOn("N"));
    }

    [Fact]
    public void Owned_thread_confined_and_unknown_keep_their_precedence()
    {
        var run = Execute("""
            public sealed class Box { public int N; }
            public static class Registry { public static Box? Last; }
            public static class Factory { public static void Make() => Registry.Last = new Box(); }
            public class BoxController : ControllerBase
            {
                public async Task Post()
                {
                    var unused = new Box();
                    GC.KeepAlive(unused);
                    var mine = new Box();
                    mine.N = 1;
                    await Task.Run(() => { Factory.Make(); Factory.Make(); });
                }
            }
            """ + Startup(), new AnalysisLimits(MaxContextsPerMethod: 1));

        Assert.Equal(OwnershipKind.Owned, run.Ownership("alloc:BoxController.Post()#Box").Kind);
        Assert.Equal(OwnershipKind.ThreadConfined, run.Ownership("alloc:BoxController.Post()#Box#2").Kind);
        // Made in the work, so created by it, and reached through a static after a merged context: never tree-confined.
        var merged = Assert.Single(run.Heap.Heap.Regions.Values, region => region is { Kind: HeapRegionKind.Allocation, IsMerged: true });
        Assert.Equal(OwnershipKind.Unknown, run.Analysis.Ownership[merged.Identity].Kind);
        Assert.DoesNotContain(run.Analysis.Ownership.Values, ownership => ownership.Kind == OwnershipKind.TreeConfined);
    }

    [Fact]
    public void An_unknown_effect_on_a_tree_confined_region_stays_a_write()
    {
        var run = Analyze("""
            public sealed class Box { public int N; }
            public class BoxController : ControllerBase
            {
                public async Task<int> Post()
                {
                    var box = new Box();
                    await Task.Run(() => { box.N++; System.Console.WriteLine(box); });
                    return box.N;
                }
            }
            """ + Startup());

        Assert.Equal(OwnershipKind.TreeConfined, run.Execution.Ownership(BOX).Kind);
        Assert.Contains(run.Of("N"), access => access.Operation.IsUnknownEffect() && access.Ownership == OwnershipKind.TreeConfined);
        Assert.DoesNotContain(run.Of("N"), access => access.Operation == AccessOperation.Read && access.Symbol.Contains("lambda", StringComparison.Ordinal));
    }

    [Fact]
    public void Bodyless_work_an_action_starts_on_its_object_leaves_it_tree_confined_and_its_unknown_effect_a_write()
    {
        // The work resolves to a member with no body, whose unresolved call runs in the work's own execution, started by one run of
        // the action: the box stays the action's tree, and the call's unknown effect on it stays a write (R5).
        var run = Analyze("""
            public sealed class Box { public int N; }
            public class BoxController : ControllerBase
            {
                public async Task<int> Post()
                {
                    var box = new Box();
                    await Task.Factory.StartNew(System.Console.WriteLine, box);
                    box.N = 1;
                    return box.N;
                }
            }
            """ + Startup());

        Assert.Equal(OwnershipKind.TreeConfined, run.Execution.Ownership(BOX).Kind);
        Assert.Contains(run.Of("N"), access => access.Operation.IsUnknownEffect() && access.Ownership == OwnershipKind.TreeConfined &&
                                               access.ExecutionId != run.Execution.Ownership(BOX).Creator);
    }

    [Fact]
    public void A_semantic_gap_counts_a_tree_confined_object_shared()
    {
        var run = Analyze("""
            public sealed class Box { public int N; }
            public class BoxController : ControllerBase
            {
                public async Task<int> Post()
                {
                    var box = new Box();
                    await Task.Run(() => { box.N++; System.Console.WriteLine(box); });
                    return box.N;
                }
            }
            """ + Startup());

        Assert.Equal(OwnershipKind.TreeConfined, run.Execution.Ownership(BOX).Kind);
        Assert.Contains(run.Collection.Coverage.Gaps, gap => gap.Callee == "System.Console.WriteLine(object)");
    }

    [Fact]
    public void Publication_counts_a_tree_confined_object_shared()
    {
        // The draft's constructor hands the tree-confined draft to a call the analysis cannot follow, which may keep it anywhere.
        var run = Execute("""
            public sealed class Draft
            {
                public int Body;
                public Draft() { System.Console.WriteLine(this); Body = 1; }
            }
            public class DraftController : ControllerBase
            {
                public async Task<int> Post()
                {
                    var draft = new Draft();
                    await Task.Run(() => { draft.Body++; });
                    return draft.Body;
                }
            }
            """ + Startup());

        Assert.Equal(OwnershipKind.TreeConfined, run.Ownership("alloc:DraftController.Post()#Draft").Kind);
        Assert.Contains(run.Heap.Region("alloc:DraftController.Post()#Draft").Identity, run.Analysis.PublishedObjects);
        Assert.Contains(run.Accesses("Body", SummaryAccessKind.Store), access => !access.IsConstructionLocal);
    }

    [Fact]
    public void A_captured_value_copied_into_a_singleton_publishes_nothing()
    {
        var run = Analyze("""
            public sealed class Totals { public int Last; }
            public class CountController(Totals totals) : ControllerBase
            {
                public async Task Post()
                {
                    var count = 0;
                    await Task.Run(() => { count++; });
                    totals.Last = count;
                }
            }
            """ + Startup("services.AddSingleton<Totals>();"));

        var closure = run.Execution.Ownership("closure:CountController.Post()");
        Assert.Equal(OwnershipKind.TreeConfined, closure.Kind);
        Assert.NotEmpty(run.Accesses("count"));
        Assert.Empty(run.PairsOn("count"));
    }

    [Theory]
    [InlineData("Sink.Saved = Ping;", OwnershipKind.TreeConfined)]
    [InlineData("Sink.Saved = () => { };", OwnershipKind.TreeConfined)]
    [InlineData("Sink.Saved = () => Ping();", OwnershipKind.TreeConfined)]
    [InlineData("Sink.Saved = () => count++;", OwnershipKind.Escaped)]
    [InlineData("Sink.Saved = () => { Action inner = () => count++; inner(); };", OwnershipKind.Escaped)]
    public void Publishing_a_delegate_publishes_only_the_closures_it_captures(string publish, OwnershipKind kind)
    {
        // A method group or a lambda closing over no variable carries no closure, whatever closures the member creating it has; one that
        // closes over a variable, itself or through a lambda nested in it, carries that variable's closure where it is stored (R4, R5).
        var run = Analyze($$"""
            public static class Sink { public static Action? Saved; }
            public class CountController : ControllerBase
            {
                public async Task<int> Post()
                {
                    int count = 0;
                    {{publish}}
                    await Task.Run(() => count++);
                    return count;
                }
                private static void Ping() { }
            }
            """ + Startup());

        Assert.Equal(kind, run.Execution.Ownership("closure:CountController.Post()").Kind);
        Assert.NotEmpty(run.Accesses("count"));
        Assert.Equal(kind != OwnershipKind.TreeConfined, run.PairsOn("count").Count != 0);
    }

    [Fact]
    public void A_region_two_executions_create_and_others_access_stays_escaped()
    {
        var run = Analyze("""
            public sealed class Box { public int N; }
            public static class Maker
            {
                public static async Task<int> Make()
                {
                    var box = new Box();
                    await Task.Run(() => { box.N++; });
                    return box.N;
                }
            }
            public static class Shared { public static Task<int> Run() => Maker.Make(); }
            public class FirstController : ControllerBase { public Task<int> Post() => Shared.Run(); }
            public class SecondController : ControllerBase { public Task<int> Post() => Shared.Run(); }
            """ + Startup());

        // One call site of the helper is one context, so both actions run the one allocation.
        var region = Assert.Single(run.Execution.Heap.Heap.Regions.Values, region => region.Display == "alloc:Maker.Make()#Box");
        Assert.Equal(OwnershipKind.Escaped, run.Execution.Analysis.Ownership[region.Identity].Kind);
        Assert.NotEmpty(run.PairsOn("N"));
    }

    [Theory]
    [InlineData("public static class Counter { public static async Task<int> Count() { int n = 0; await Task.Delay(1); await Task.Run(() => n++); return n; } }" +
                "public class CountController : ControllerBase { public Task<int> Post() => Counter.Count(); }",
                "closure:Counter.Count()", false)]
    [InlineData("public static class Counter { public static async Task<int> Count() { int n = 0; await Task.Delay(1); await Task.Run(() => n++); return n; } }" +
                "public class CountController : ControllerBase { public int Post() { _ = Counter.Count(); return 0; } }",
                "closure:Counter.Count()", false)]
    [InlineData("public static class Counter { public static async Task<int> Count() { int n = 0; await Task.Delay(1); await Task.Run(() => n++); return n; } }" +
                "public class CountController : ControllerBase { public async Task<int> Post() => await Counter.Count(); }",
                "closure:Counter.Count()", false)]
    [InlineData("public class CountController : ControllerBase { public async Task<int> Post() { int n = 0; await Task.Delay(1); await Task.Run(() => n++); return n; } }",
                "closure:CountController.Post()", false)]
    [InlineData("public class CountController : ControllerBase { public async Task<int> Post() { int n = 0; await Task.Delay(1); " +
                "var a = Task.Run(() => n++); var b = Task.Run(() => n++); await Task.WhenAll(a, b); return n; } }",
                "closure:CountController.Post()", true)]
    public void A_closure_is_created_once_per_run_of_its_member_whatever_segment_reaches_it(string source, string closure, bool pairs)
    {
        // The closure is made at the body's entry, by the execution running its first segment: the tail after an await is the same run
        // and creates nothing, so the closure stays the one run's, and pairs only within it (R4, R5, ADR 0020).
        var run = Analyze(source + Startup());

        var ownership = run.Execution.Ownership(closure);
        Assert.Equal(OwnershipKind.TreeConfined, ownership.Kind);
        Assert.Equal(ExecutionKind.Root, run.Execution.Analysis.Execution(ownership.Creator!).Kind);
        Assert.NotEmpty(run.Accesses("n"));
        Assert.Equal(pairs, run.PairsOn("n").Count != 0);
        Assert.All(run.PairsOn("n"), pair => Assert.NotEqual(pair.First.ExecutionId, pair.Second.ExecutionId));
    }

    [Theory]
    [InlineData("Action outer = () => { S s = default; s.X++; Keeper.Keep = () => s.X++; }; outer();")]
    [InlineData("void Outer() { S s = default; s.X++; Keeper.Keep = () => s.X++; } Outer();")]
    [InlineData("S s = default; s.X++; Keeper.Keep = () => s.X++;")]
    public void A_struct_a_lambda_or_local_function_declares_in_its_closure_is_fresh_to_it(string body)
    {
        // The body declaring a captured variable makes the closure it is in on each run, whatever kind of body it is: its own accesses to
        // the struct kept there are fresh, and two requests' runs of it never meet (R4).
        var run = Analyze($$"""
            public struct S { public int X; }
            public static class Keeper { public static Action? Keep; }
            public class SController : ControllerBase
            {
                public void Post()
                {
                    {{body}}
                }
            }
            """ + Startup());

        // The body is one line: the declaring body's increment is the first on it, the nested delegate's the second.
        var increments = run.Collection.Accesses.Where(access => access.Resource.AccessPath.SequenceEqual(["s", "X"]) &&
                                                                 access.Operation == AccessOperation.ReadModifyWrite)
                            .OrderBy(access => access.Source.StartLine).ThenBy(access => access.Source.StartColumn).ToArray();
        Assert.NotEmpty(increments);
        Assert.True(increments[0].IsFresh, $"{increments[0].Symbol} {increments[0].Resource.Region}");
        Assert.DoesNotContain(run.Pairs.Pairs, pair => pair.Resource.AccessPath.SequenceEqual(["s", "X"]));
    }

    private const string TWO_CREATORS = """
        public sealed class A { public int X; }
        public sealed class B { public int Y; }
        public class TreeController : ControllerBase
        {
            public async Task Post(int n)
            {
                var a = new A();
                var works = new Task[n];
                for (var i = 0; i < n; i++)
                {
                    works[i] = Task.Run(async () =>
                    {
                        var b = new B();
                        var c = Task.Run(() => { a.X = 1; b.Y = 1; });
                        await c;
                        a.X = 2;
                        b.Y = 2;
                    });
                }
                await Task.WhenAll(works);
            }
        }
        """;

    [Fact]
    public void Two_creators_contexts_in_one_analysis_answer_alike_whichever_is_asked_first()
    {
        var aFirst = Execute(TWO_CREATORS + Startup());
        var bFirst = Execute(TWO_CREATORS + Startup());
        var (xs, ys) = Written(aFirst);
        var onA = Timings(aFirst.Analysis, xs);
        var onB = Timings(aFirst.Analysis, ys);
        var (xsAgain, ysAgain) = Written(bFirst);
        var onBAgain = Timings(bFirst.Analysis, ysAgain);
        var onAAgain = Timings(bFirst.Analysis, xsAgain);

        Assert.Equal(OwnershipKind.TreeConfined, aFirst.Ownership("alloc:TreeController.Post(int)#A").Kind);
        var b = Assert.Single(aFirst.Heap.Heap.Regions.Values, region => region.Display.EndsWith("#B", StringComparison.Ordinal));
        Assert.Equal(OwnershipKind.TreeConfined, aFirst.Analysis.Ownership[b.Identity].Kind);
        // On A, the action's: the work repeats in one run of the action and so does what it starts, so W x W, C x C and W x C meet.
        Assert.Equal(4, onA.Length);
        Assert.All(onA, timing => Assert.Equal(PairTiming.Concurrent, timing));
        // On B, the work's: C runs once per run of the work, and the work's await orders it before the write after it.
        Assert.Equal(4, onB.Length);
        Assert.DoesNotContain(PairTiming.Concurrent, onB);
        Assert.Equal(onA, onAAgain);
        Assert.Equal(onB, onBAgain);
    }

    /// <summary>The two writes of <c>X</c> and of <c>Y</c>, C's first, the work's after its await second; none is fresh, both reach
    /// their object through a capture, save the work's own write of the B it made.</summary>
    /// <param name="run">The execution model of the case, whose accesses are collected without pairing them.</param>
    private static (Access[] X, Access[] Y) Written(ExecutionRun run)
    {
        var accesses = Collect(run).Accesses.Where(access => !access.IsConstructionLocal).ToArray();
        Access[] Of(string field) => accesses.Where(access => access.Resource.AccessPath.SequenceEqual([field]) && access.Operation == AccessOperation.Write)
                                             .OrderBy(access => access.Source.StartLine).ToArray();
        return (Of("X"), Of("Y"));
    }

    /// <summary>The timing of every pair of the accesses, each with itself, in order, and the last against the first.</summary>
    /// <param name="analysis">The execution model asked.</param>
    /// <param name="accesses">The accesses.</param>
    private static PairTiming[] Timings(ExecutionAnalysis analysis, Access[] accesses) =>
        accesses.SelectMany((first, index) => accesses.Skip(index).Select(second => analysis.Timing(first, second)))
                .Concat([analysis.Timing(accesses[^1], accesses[0])])
                .ToArray();
}
