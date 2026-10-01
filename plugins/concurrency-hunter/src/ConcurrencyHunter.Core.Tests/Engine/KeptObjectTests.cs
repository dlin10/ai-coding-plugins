using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class KeptObjectTests
{
    [Fact]
    public void Kept_value_is_returned_by_a_later_call_on_the_same_keeper()
    {
        var run = Run("var cache = new Cache(); Lib.Set(cache, new Item()); Lib.Get<Item>(cache).Hits = 1;");
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.Write);
    }

    [Fact]
    public void Kept_value_of_a_shared_keeper_is_escaped()
    {
        var run = Run("Lib.Set(_state.Cache, new Item()); Lib.Get<Item>(_state.Cache).Hits++;");
        Assert.Equal(OwnershipKind.Escaped, ItemOwnership(run).Kind);
    }

    [Fact]
    public void Keepers_without_an_object_share_one_store_per_type()
    {
        var run = Run("Lib.Set(_state.Empty, new Item()); Lib.OtherSet(_state.Empty, new Other()); Lib.Get<Item>(_state.Empty).Hits = 1;");
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.Write);
        Assert.Single(TypeStores(run, "Kept:Kept.ICache"));
        Assert.Equal(2, TypeStores(run, "Kept:Kept.ICache").Single().Held.Count);
    }

    [Fact]
    public void Stores_of_two_types_stay_apart()
    {
        var run = Run("Lib.Set(_state.Empty, new Item()); Lib.SetOther(_state.OtherEmpty, new Other()); Lib.Get<Item>(_state.Empty).Hits = 1; Lib.GetOther<Other>(_state.OtherEmpty).OtherHits = 1;");
        Assert.Single(TypeStores(run, "Kept:Kept.ICache").Single().Held);
        Assert.Single(TypeStores(run, "Kept:Kept.IOther").Single().Held);
        Assert.Contains(run.Of("OtherHits"), access => access.Operation == AccessOperation.Write);
    }

    [Fact]
    public async Task Kept_value_of_a_keeper_of_one_execution_stays_confined()
    {
        var result = await Full("var cache = new Cache(); Lib.Set(cache, new Item()); Lib.Get<Item>(cache).Hits++;");
        Assert.DoesNotContain(result.Findings, finding => finding.Resource.Member.Name == "Hits");
        Assert.Equal(0, Assert.Single(result.Coverage).Skips[CoverageCounters.MODEL_ENTRY_REJECTED]);
    }

    [Fact]
    public void Keeper_whose_object_arrives_during_propagation_keeps_into_it_and_not_into_the_type_store()
    {
        var run = Run("var cache = MakeCache(); Lib.Set(cache, new Item()); Lib.Get<Item>(cache).Hits = 1;");
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.Write);
        Assert.Empty(TypeStores(run, "Kept:Kept.ICache").SelectMany(store => store.Held));
    }

    [Fact]
    public void Empty_keepers_of_one_wave_all_go_to_the_type_store()
    {
        var run = Run("Lib.Set(_state.Empty, new Cache()); var second = Lib.Get<ICache>(_state.Empty); Lib.Set(second, new Item());");
        Assert.Equal(2, TypeStores(run, "Kept:Kept.ICache").Single().Held.Count);
    }

    [Fact]
    public void Type_store_fallback_stays_when_the_keepers_object_arrives_later()
    {
        var run = Run("Lib.Set(_state.Empty, new Cache()); var second = Lib.Get<ICache>(_state.Empty); Lib.Set(second, new Item()); Lib.Get<Item>(second).Hits = 1;");
        Assert.Equal(2, TypeStores(run, "Kept:Kept.ICache").Single().Held.Count);
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.Write);
        Assert.Contains(run.Execution.Heap.Heap.Regions.Values, region => region.TypeKey == "Kept:Kept.Cache" &&
            run.Execution.Heap.Heap.PointsTo(region.Identity, "[kept]").Any(id => run.Execution.Heap.Heap.Regions[id].TypeKey == "Fixture:Item"));
    }

    [Fact]
    public void Keeps_act_at_the_call_even_with_a_sequence_result()
    {
        var run = Run("_ = Lib.Lazy(_state.Cache, new Item()); Lib.Get<Item>(_state.Cache).Hits = 1;");
        Assert.Equal(OwnershipKind.Escaped, ItemOwnership(run).Kind);
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.Write);
    }

    [Fact]
    public void Unknown_effect_through_a_keeper_reaches_kept_objects()
    {
        foreach (var work in new[] { "Lib.Set(_state.Cache, new Item()); Lib.Touch(_state.Cache);",
                                     "_state.Any = Lib.MakeString(new Item()); Lib.Touch(_state.Any);" })
        {
            var run = Run(work);
            Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.UnknownEffect);
            Assert.DoesNotContain(run.Collection.Accesses, access => access.Resource.AccessPath.Contains("[kept]"));
        }
    }

    [Fact]
    public void Deep_read_of_a_keeper_reads_kept_objects()
    {
        var run = Run("Lib.Set(_state.Cache, new Item()); Lib.Read(_state.Cache);");
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.Read);
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Resource.AccessPath.Contains("[kept]"));
    }

    [Fact]
    public void Kept_iterator_is_enumerated_where_the_elements_of_kept_are_read()
    {
        foreach (var sequence in new[] { "Produce()", "new [] { new Item() }.Select(item => { _state.Count = 2; return item; })" })
        {
            var run = Run($"Lib.Set(_state.Cache, {sequence}); lock (_state.Gate) {{ Lib.Elements<Item>(_state.Cache); }}");
            Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.Write && access.HeldProtectionIds.Count != 0);
            Assert.Equal(1,
                run.Of("Count").Count(access => access.Operation == AccessOperation.Write && access.HeldProtectionIds.Count != 0));
        }
    }

    [Fact]
    public void Result_keeper_keeps_into_the_new_object()
    {
        var run = Run("var cache = Lib.Make(new Item()); Lib.Get<Item>(cache).Hits = 1;");
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.Write);
        Assert.Single(run.Execution.Heap.Heap.Regions.Values, region => region.ModelCreationKey is not null &&
            run.Execution.Heap.Heap.PointsTo(region.Identity, "[kept]").Count != 0);
    }

    [Fact]
    public void Escape_through_a_keeper_names_the_keeping_call()
    {
        var work = "Lib.Set(_state.Cache, new Item()); Lib.Get<Item>(_state.Cache).Hits = 1;";
        var run = Run(work);
        Assert.Contains(ItemOwnership(run).Evidence, hop => hop.Contains("[kept]", StringComparison.Ordinal) && hop.Contains("Case.cs:", StringComparison.Ordinal));
    }

    [Fact]
    public void Keeper_is_not_a_delegate_holder()
    {
        var run = Run("Lib.Set(_state.Cache, new Item()); Lib.Touch(_state.Cache);");
        Assert.Empty(run.Execution.Heap.Heap.Holders);
        Assert.Empty(run.Execution.Heap.Heap.DelegateHandoffs);
    }

    [Fact]
    public void Holder_kept_by_a_keeper_handed_to_a_call_without_a_body_adds_an_unknown_execution()
    {
        var run = Run("Lib.Set(_state.Cache, Lib.Hold(() => _state.Count = 1)); Lib.Touch(_state.Cache);");
        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.Write && run.Execution.Analysis.Execution(access.ExecutionId).Kind == ExecutionKind.UnknownDelegateCall);
    }

    [Fact]
    public async Task Opaque_result_kept_by_a_shared_keeper_feeds_it()
    {
        var result = await Full("Lib.Set(_state.Cache, Lib.Unknown());");
        Assert.Contains(Assert.Single(result.Coverage).Gaps, gap => gap.Callee.Contains("Unknown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Opaque_result_kept_in_a_type_store_feeds_it()
    {
        var result = await Full("Lib.Set(_state.Empty, Lib.Unknown());");
        Assert.Contains(Assert.Single(result.Coverage).Gaps, gap => gap.Callee.Contains("Unknown", StringComparison.Ordinal));
    }

    private static RegionOwnership ItemOwnership(EngineRun run) => run.Execution.Ownership("alloc:Worker.ExecuteAsync(CancellationToken)#Item");

    private static IEnumerable<(HeapRegion Region, IReadOnlySet<string> Held)> TypeStores(EngineRun run, string type) =>
        run.Execution.Heap.Heap.Regions.Values.Where(region => region.Kind == HeapRegionKind.Static && region.TypeKey == type)
           .Select(region => (region, run.Execution.Heap.Heap.PointsTo(region.Identity, "[kept]")));

    private static EngineRun Run(string work)
    {
        var solution = Solution(work);
        return AnalyzeScope(solution, "scope:Fixture", ModelCellFixture.Resolve(solution, KeptFixture.Models));
    }

    private static async Task<AnalysisResult> Full(string work)
    {
        using var repo = new CellModelRepository(KeptFixture.Models);
        return await PhaseOneAnalyzer.AnalyzeAsync(Solution(work, controller: true), ROOT_DIRECTORY, repo.Root, CancellationToken.None);
    }

    private static Solution Solution(string work, bool controller = false) => FixtureSolution.Create(new FixtureOptions { MetadataReferences = [KeptFixture.Library] },
        ("Case.cs", Usings + $$"""
        using Kept;
        using System.Collections.Generic;
        using System.Linq;
        public class Item { public int Hits; }
        public class Other { public int OtherHits; }
        public sealed class State
        {
            public readonly Cache Cache = new Cache();
            public ICache Empty;
            public IOther OtherEmpty;
            public readonly object Gate = new object();
            public int Count;
            public object Any;
        }
        {{(controller ? "[ApiController]" : "")}}
        public sealed class Worker(State state) : {{(controller ? "ControllerBase" : "BackgroundService")}}
        {
            private readonly State _state = state;
            private ICache MakeCache() => new Cache();
            private IEnumerable<Item> Produce() { _state.Count = 1; yield return new Item(); }
            {{(controller ? "[HttpPost] public Task Post()" : "protected override Task ExecuteAsync(CancellationToken stoppingToken)")}} { {{work}} return Task.CompletedTask; }
        }
        """ + Startup("services.AddSingleton<State>();" + (controller ? "" : " services.AddHostedService<Worker>();"))));
}

internal static class KeptFixture
{
    internal const string Models = """
        {"schemaVersion":1,"assemblies":["Kept"],"models":[
          {"member":"M:Kept.Lib.Put``1(Kept.ICache{``0},``0)","effects":{},"keeps":{"cache":["arg:value"]}},
          {"member":"M:Kept.Lib.GetValue``1(Kept.ICache{``0})","effects":{},"result":"[kept:cache]"},
          {"member":"M:Kept.Lib.Set``1(Kept.ICache,``0)","effects":{},"keeps":{"cache":["arg:value"]}},
          {"member":"M:Kept.Lib.OtherSet``1(Kept.ICache,``0)","effects":{},"keeps":{"cache":["arg:value"]}},
          {"member":"M:Kept.Lib.Get``1(Kept.ICache)","effects":{},"result":"[kept:cache]"},
          {"member":"M:Kept.Lib.SetOther``1(Kept.IOther,``0)","effects":{},"keeps":{"cache":["arg:value"]}},
          {"member":"M:Kept.Lib.GetOther``1(Kept.IOther)","effects":{},"result":"[kept:cache]"},
          {"member":"M:Kept.Lib.Lazy``1(Kept.ICache,``0)","effects":{},"keeps":{"cache":["arg:value"]},"result":"sequence(arg:value)"},
          {"member":"M:Kept.Lib.MakeString``1(``0)","effects":{},"keeps":{"result":["arg:value"]},"result":"new"},
          {"member":"M:Kept.Lib.Make``1(``0)","effects":{},"keeps":{"result":["arg:value"]},"result":"new"},
          {"member":"M:Kept.Lib.Read(Kept.ICache)","effects":{"cache":["reads-deep"]}},
          {"member":"M:Kept.Lib.Elements``1(Kept.ICache)","effects":{},"result":"collection(elements(kept:cache))"},
          {"member":"M:Kept.Lib.Hold(System.Action)","effects":{},"fates":{"action":{"fate":"holder","holder":"result","inputs":[]}}}
        ]}
        """;

    internal static MetadataReference Library { get; } = CreateLibrary();

    private static MetadataReference CreateLibrary()
    {
        var compilation = CSharpCompilation.Create("Kept", [CSharpSyntaxTree.ParseText("""
            using System;
            using System.Collections.Generic;
            namespace Kept;
            public interface ICache { }
            public interface ICache<T> : ICache { }
            public class Cache<T> : Cache, ICache<T> { }
            public interface IOther { }
            public class Cache : ICache, IOther { }
            public class Holder { }
            public static class Lib
            {
                public static void Put<T>(ICache<T> cache, T value) { }
                public static T GetValue<T>(ICache<T> cache) => default!;
                public static void Set<T>(ICache cache, T value) { }
                public static void OtherSet<T>(ICache cache, T value) { }
                public static T Get<T>(ICache cache) => default!;
                public static void SetOther<T>(IOther cache, T value) { }
                public static T GetOther<T>(IOther cache) => default!;
                public static IEnumerable<T> Lazy<T>(ICache cache, T value) => null!;
                public static string MakeString<T>(T value) => null!;
                public static Cache Make<T>(T value) => null!;
                public static void Read(ICache cache) { }
                public static List<T> Elements<T>(ICache cache) => null!;
                public static Holder Hold(Action action) => null!;
                public static void Touch(object value) { }
                public static object Unknown() => null!;
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        var emit = compilation.Emit(bytes);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return MetadataReference.CreateFromImage(bytes.ToArray());
    }
}
