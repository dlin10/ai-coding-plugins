using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class CandidateIndexTests
{
    private const string SCOPE = "scope:Fixture";
    private const string EXECUTION = "root:a";
    private const string GROUP = "static|Fixture:Cache";

    [Fact]
    public void Accesses_on_two_regions_are_never_compared()
    {
        var accesses = new[] { Write("a", "Value"), Read("a", "Value"), Write("b", "Value") };

        var pairs = Enumerate(Heap(Static("a"), Static("b")), accesses);

        Assert.Equal([(0, 0), (0, 1), (1, 1), (2, 2)], Keys(pairs, accesses));
        Assert.All(pairs, pair => Assert.Equal(pair.First.Resource.RegionId, pair.Second.Resource.RegionId));
    }

    [Fact]
    public void Accesses_on_two_resources_of_one_region_are_never_compared()
    {
        var accesses = new[] { Write("a", "X"), Write("a", "Y") };

        Assert.Equal([(0, 0), (1, 1)], Keys(Enumerate(Heap(Static("a")), accesses), accesses));
    }

    [Fact]
    public void Two_members_with_one_display_path_are_two_buckets()
    {
        var accesses = new[] { Write("a", "Value", declaring: "Fixture.Base"), Write("a", "Value", declaring: "Fixture.Derived") };

        var index = CandidateIndex.Build(accesses, Heap(Static("a")));

        Assert.Equal(accesses[0].Resource.AccessPath, accesses[1].Resource.AccessPath);
        Assert.Equal(2, index.Buckets);
        Assert.Equal(1, index.LargestBucket);
        Assert.Equal([(0, 0), (1, 1)], Keys(index.Pairs().ToArray(), accesses));
    }

    [Fact]
    public void Wildcard_bucket_meets_every_bucket_of_its_region_and_itself()
    {
        var accesses = new[] { Write("a", "X"), Write("a", "Y"), Wildcard("a"), Wildcard("a", "Other"), Write("b", "X"), Wildcard("b") };

        var index = CandidateIndex.Build(accesses, Heap(Static("a"), Static("b")));
        var wildcard = index.Pairs().Where(pair => pair.Enumeration == PairEnumeration.Wildcard).ToArray();

        Assert.Equal(5, index.Buckets);
        Assert.Equal([(0, 2), (0, 3), (1, 2), (1, 3), (2, 2), (2, 3), (3, 3), (4, 5), (5, 5)], Keys(wildcard, accesses));
        Assert.All(wildcard, pair => Assert.True(pair.First.Resource.IsWildcard));
        Assert.All(wildcard, pair => Assert.Equal(pair.First.Resource, pair.Reported));
    }

    [Fact]
    public void Open_region_bucket_meets_each_closed_region_bucket_of_its_group()
    {
        var open = Write("open", "X") with { Uncertainties = ["merged"] };
        var accesses = new[] { open, Write("first", "X"), Write("second", "X"), Write("second", "Y"), Write("other", "X") };
        var heap = Heap(Static("open", GROUP, isOpen: true), Static("first", GROUP), Static("second", GROUP), Static("other"));

        var pairs = Enumerate(heap, accesses).Where(pair => pair.Enumeration == PairEnumeration.OpenRegion).ToArray();

        Assert.Equal([(0, 1), (0, 2)], Keys(pairs, accesses));
        Assert.All(pairs, pair => Assert.Same(open, pair.First));
        Assert.All(pairs, pair => Assert.Equal(pair.Second.Resource, pair.Reported));
        Assert.All(pairs, pair => Assert.Equal(["merged"], pair.Uncertainties));
    }

    [Fact]
    public void Open_region_wildcard_meets_closed_region_buckets_once()
    {
        var accesses = new[] { Wildcard("open"), Write("open", "X"), Write("first", "X"), Wildcard("first"), Write("second", "X"), Write("second", "Y") };
        var heap = Heap(Static("open", GROUP, isOpen: true), Static("first", GROUP), Static("second", GROUP));

        var pairs = Enumerate(heap, accesses);
        var openWildcard = pairs.Where(pair => pair.Enumeration == PairEnumeration.OpenRegion && ReferenceEquals(pair.First, accesses[0])).ToArray();

        Assert.Equal([(0, 2), (0, 3), (0, 4), (0, 5)], Keys(openWildcard, accesses));
        Assert.Equal(Keys(pairs, accesses), ReferenceKeys(heap, accesses));
    }

    [Fact]
    public void No_unordered_pair_is_produced_twice()
    {
        var accesses = new[] { Wildcard("open"), Write("open", "X"), Read("open", "X"), Write("closed", "X"), Wildcard("closed") };
        var heap = Heap(Static("open", GROUP, isOpen: true), Static("closed", GROUP));

        var keys = Keys(Enumerate(heap, accesses), accesses);

        Assert.Equal(keys.Distinct().Count(), keys.Count);
        Assert.Equal(accesses.Length * (accesses.Length + 1) / 2, keys.Count);
    }

    [Fact]
    public void Comparisons_equal_the_sum_of_the_three_enumerations()
    {
        var accesses = new[] { Wildcard("open"), Write("open", "X"), Write("closed", "X"), Read("closed", "X"), Write("closed", "Y"), Write("other", "X") };
        var heap = Heap(Static("open", GROUP, isOpen: true), Static("closed", GROUP), Static("other"));

        var analysis = InterproceduralPairing.Pair(accesses, Executions(), heap);
        var byEnumeration = Enumerate(heap, accesses).GroupBy(pair => pair.Enumeration).ToDictionary(group => group.Key, group => group.Count());

        Assert.Equal(3, byEnumeration.Count);
        Assert.Equal(byEnumeration.Values.Sum(), analysis.Comparisons);
        Assert.Equal(analysis.Comparisons, analysis.Skips.Values.Sum() + analysis.Suppressed + analysis.Candidates);
        Assert.Equal(analysis.Pairs.Count, analysis.Candidates);
    }

    [Fact]
    public async Task Cartesian_bound_is_n_times_n_plus_one_over_two_per_scope_and_summed()
    {
        Assert.Equal(6, InterproceduralPairing.Pair([Write("a", "X"), Write("b", "X"), Write("c", "X")], Executions(), Heap(Static("a"), Static("b"), Static("c"))).CartesianBound);
        Assert.Equal(0, InterproceduralPairing.Pair([], Executions(), Heap()).CartesianBound);
        Assert.Equal(1, InterproceduralPairing.Pair([Write("a", "X"), Write("a", "Y") with { IsConstructionLocal = true }], Executions(), Heap(Static("a"))).CartesianBound);

        var (result, scopes) = await AnalyzeTwoScopes();

        Assert.Equal(2, scopes.Count);
        foreach (var (accesses, analysis) in scopes)
            Assert.Equal(accesses * (accesses + 1) / 2, analysis.CartesianBound);
        var counts = result.Accesses.Where(access => !access.IsConstructionLocal).GroupBy(access => access.Resource.Scope).Select(group => group.Count()).ToArray();
        Assert.Equal(counts.Order(), scopes.Select(scope => scope.Accesses).Order());
        Assert.Equal(counts.Sum(count => count * (count + 1) / 2), result.Pairs.CartesianBound);
    }

    [Fact]
    public void Comparisons_are_below_the_cartesian_bound_with_two_regions()
    {
        var accesses = new[] { Write("a", "X"), Write("a", "X", symbol: "S.Other"), Write("b", "X"), Write("b", "X", symbol: "S.Other") };

        var analysis = InterproceduralPairing.Pair(accesses, Executions(), Heap(Static("a"), Static("b")));

        Assert.Equal(10, analysis.CartesianBound);
        Assert.Equal(6, analysis.Comparisons);
        Assert.True(analysis.Comparisons < analysis.CartesianBound);
    }

    [Fact]
    public async Task Largest_bucket_is_the_maximum_over_scopes_and_buckets_are_counted()
    {
        var (result, scopes) = await AnalyzeTwoScopes();
        var analyses = scopes.Select(scope => scope.Analysis).ToArray();

        Assert.NotEqual(analyses[0].LargestBucket, analyses[1].LargestBucket);
        Assert.Equal(analyses.Max(analysis => analysis.LargestBucket), result.Pairs.LargestBucket);
        Assert.Equal(analyses.Sum(analysis => analysis.Buckets), result.Pairs.Buckets);
        Assert.Equal(analyses.Sum(analysis => analysis.Comparisons), result.Pairs.Comparisons);
        Assert.Equal(analyses.Sum(analysis => analysis.Candidates), result.Pairs.Candidates);
        Assert.Equal(analyses.Sum(analysis => analysis.Suppressed), result.Pairs.Suppressed);
        Assert.True(result.Pairs.Candidates <= result.Pairs.Comparisons);
        Assert.True(result.Pairs.Buckets > 0);
    }

    [Fact]
    public void Pair_set_equals_the_reference_loop()
    {
        var run = Analyze("""
            public sealed class Order { }
            public sealed class Invoice { }
            public sealed class Leaf { public object? Value; }
            public sealed class Chain { public Leaf Child { get; } = new(); public object? Value; }
            public static class Cache<T> { public static object? Last; }
            public static class Access
            {
                public static void Store<T>(object value) => Cache<T>.Last = value;
                public static object? Load<T>() => Cache<T>.Last;
            }
            public class CacheController(Chain chain) : ControllerBase
            {
                public void Post() { Access.Store<Order>(new object()); Access.Store<Invoice>(new object()); GC.KeepAlive(Access.Load<Order>()); }
                public void Deep() => chain.Child.Value = new object();
                public void Shallow() => chain.Value = new object();
            }
            """ + Startup("services.AddSingleton<Chain>();"), new AnalysisLimits(MaxAccessPathDepth: 1, MaxContextsPerMethod: 1));
        var heap = run.Execution.Heap.Heap;

        var reference = ReferencePair(run.Collection.Accesses, run.Execution.Analysis, heap);

        Assert.Contains(run.Pairs.Pairs, pair => pair.Resource.IsWildcard);
        Assert.Contains(run.Pairs.Pairs, pair => heap.Regions[pair.First.Resource.RegionId!].IsOpen);
        Assert.Equal(PairKeys(reference), PairKeys(run.Pairs));
        Assert.Equal(reference.Comparisons, run.Pairs.Comparisons);
        Assert.Equal(reference.Skips, run.Pairs.Skips);
        Assert.Equal(reference.Suppressed, run.Pairs.Suppressed);
    }

    private static CandidatePair[] Enumerate(HeapSolution heap, IReadOnlyList<Access> accesses) => CandidateIndex.Build(accesses, heap).Pairs().ToArray();

    /// <summary>Each pair as the ordered positions of its two accesses, sorted.</summary>
    private static List<(int, int)> Keys(IEnumerable<CandidatePair> pairs, IReadOnlyList<Access> accesses) =>
        pairs.Select(pair => Key(Position(accesses, pair.First), Position(accesses, pair.Second))).Order().ToList();

    private static List<(int, int)> ReferenceKeys(HeapSolution heap, IReadOnlyList<Access> accesses)
    {
        var keys = new List<(int, int)>();
        for (var i = 0; i < accesses.Count; i++)
        {
            for (var j = i; j < accesses.Count; j++)
            {
                if (ReferenceAdmits(accesses[i], accesses[j], heap) is not null)
                    keys.Add((i, j));
            }
        }

        return keys;
    }

    private static (int, int) Key(int first, int second) => first <= second ? (first, second) : (second, first);

    private static int Position(IReadOnlyList<Access> accesses, Access access)
    {
        for (var index = 0; index < accesses.Count; index++)
        {
            if (ReferenceEquals(accesses[index], access))
                return index;
        }

        throw new InvalidOperationException("The access is not in the fixture.");
    }

    private static IReadOnlyList<string> PairKeys(PairAnalysis analysis) =>
        analysis.Pairs.Select(pair => $"{pair.First.InstanceId}#{pair.First.OperationId}|{pair.First.Resource.Identity}|{pair.First.Operation}" +
                                      $" <-> {pair.Second.InstanceId}#{pair.Second.OperationId}|{pair.Second.Resource.Identity}|{pair.Second.Operation}" +
                                      $" @ {pair.Resource.Identity} {pair.Protection} {string.Join(";", pair.Uncertainties)}")
                      .Order(StringComparer.Ordinal)
                      .ToArray();

    private static Access Write(string region, string member, string declaring = "Fixture.State", string symbol = "S.Write") =>
        Make(region, member, AccessOperation.Write, declaring, symbol);

    private static Access Read(string region, string member) => Make(region, member, AccessOperation.Read, "Fixture.State", "S.Read");

    private static Access Wildcard(string region, string symbol = "S.Deep") =>
        new Access(new AccessResource("Fixture", SCOPE, region, [PathValue.WILDCARD], new MemberKey(PathValue.WILDCARD, PathValue.WILDCARD, IrFieldKind.Field),
                                      region, true),
                   AccessOperation.Write, ROOT, symbol, SPAN, [], [], [], [], []) { ExecutionId = EXECUTION };

    private static Access Make(string region, string member, AccessOperation operation, string declaring, string symbol) =>
        new Access(new AccessResource("Fixture", SCOPE, region, [member], new MemberKey(declaring, member, IrFieldKind.Field), region),
                   operation, ROOT, symbol, SPAN, [], [], [], [], []) { ExecutionId = EXECUTION };

    private static readonly InvocationPolicy POLICY = new(Multiplicity.Repeated, SelfOverlap.MayOverlap, SCOPE);
    private static readonly AccessRoot ROOT = new(EXECUTION, "S.Root", "root", "test", "test", POLICY, SCOPE);
    private static readonly Analysis.SourceSpan SPAN = new("Case.cs", 1, 1, 1, 2);

    private static HeapRegion Static(string identity, string? group = null, bool isOpen = false) =>
        new(identity, HeapRegionKind.Static, identity, null, "", group ?? identity, isOpen, false);

    private static HeapSolution Heap(params HeapRegion[] regions) =>
        new(regions.ToDictionary(region => region.Identity, StringComparer.Ordinal), new Dictionary<string, MethodInstance>(), [], [], [],
            new Dictionary<string, int>(), new HashSet<string>(), [], [], (_, _) => new HashSet<string>(), (_, _) => new HashSet<string>(),
            (_, _) => new HashSet<string>(), new Dictionary<string, string>(), (_, _) => "", _ => [], _ => new HashSet<string>());

    private static ExecutionAnalysis Executions() =>
        new([new ExecutionInstance(EXECUTION, ExecutionKind.Root, "root", POLICY, EXECUTION, null)], [], new Dictionary<string, RegionOwnership>(),
            new Dictionary<string, int>(), new HashSet<string>(), new HashSet<string>(), new Dictionary<string, IReadOnlySet<string>>(),
            new Dictionary<string, IReadOnlyList<ExecutionEntry>>());

    /// <summary>Two executable projects, each a scope: the first writes one static from three actions, the second from one; each scope's
    /// pairing is recorded with its non-construction-local access count.</summary>
    private static async Task<(AnalysisResult Result, IReadOnlyList<(int Accesses, PairAnalysis Analysis)> Scopes)> AnalyzeTwoScopes()
    {
        const string startup = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.AspNetCore.Routing;
            using Microsoft.Extensions.DependencyInjection;
            public static class State { public static int Value; public static int Other; }
            public static class Startup
            {
                public static void Configure(IServiceCollection services, IEndpointRouteBuilder app) { services.AddControllers(); app.MapControllers(); }
            }
            """;
        var solution = FixtureSolution.CreateProjects(
            new FixtureOptions
            {
                ProjectOutputKinds = new Dictionary<string, OutputKind> { ["First"] = OutputKind.ConsoleApplication, ["Second"] = OutputKind.ConsoleApplication }
            },
            ("First", "Program.cs", "System.Console.WriteLine();"),
            ("First", "Startup.cs", startup),
            ("First", "Controllers.cs", """
                public class OneController : Microsoft.AspNetCore.Mvc.ControllerBase { public void Post() { State.Value = 1; } }
                public class TwoController : Microsoft.AspNetCore.Mvc.ControllerBase { public void Post() { State.Value = 2; } }
                public class ThreeController : Microsoft.AspNetCore.Mvc.ControllerBase { public void Post() { State.Value = 3; State.Other = 3; } }
                """),
            ("Second", "Program.cs", "System.Console.WriteLine();"),
            ("Second", "Startup.cs", startup),
            ("Second", "Controllers.cs", "public class OneController : Microsoft.AspNetCore.Mvc.ControllerBase { public void Post() { State.Value = 1; } }"));

        var scopes = new List<(int, PairAnalysis)>();
        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, ProviderRegistry.BuiltIn, (accesses, executions, heap) =>
        {
            var analysis = InterproceduralPairing.Pair(accesses, executions, heap);
            scopes.Add((accesses.Count(access => !access.IsConstructionLocal), analysis));
            return analysis;
        }, CancellationToken.None);
        return (result, scopes);
    }
}
