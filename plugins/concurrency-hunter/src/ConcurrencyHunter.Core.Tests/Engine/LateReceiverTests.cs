using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class LateReceiverTests
{
    [Fact]
    public async Task Static_factory_receiver_leaves_no_receiverless_instance() =>
        Clean(await Run("public static class Factory { public static Runner Create() => new DirectRunner(); }",
                        "Factory.Create().Run(() => _tally.Hits++);"));

    [Fact]
    public async Task Instance_factory_receiver_leaves_no_receiverless_instance() =>
        Clean(await Run("public sealed class Factory { public Runner Create() => new DirectRunner(); }",
                        "new Factory().Create().Run(() => _tally.Hits++);"));

    [Fact]
    public async Task Identity_helper_receiver_leaves_no_receiverless_instance() =>
        Clean(await Run("public static class Factory { public static T Pass<T>(T value) => value; }",
                        "Factory.Pass<Runner>(new DirectRunner()).Run(() => _tally.Hits++);"));

    [Fact]
    public async Task Static_readonly_field_receiver_leaves_no_receiverless_instance() =>
        Clean(await Run("public static class Factory { public static readonly Runner Instance = new DirectRunner(); }",
                        "Factory.Instance.Run(() => _tally.Hits++);"));

    [Fact]
    public async Task Two_level_this_chain_leaves_no_receiverless_instance() =>
        Clean(await Run("""
                        public sealed class Factory
                        {
                            public Runner Create() => Next();
                            private Runner Next() => new DirectRunner();
                        }
                        """, "new Factory().Create().Run(() => _tally.Hits++);"));

    [Fact]
    public async Task Generic_base_method_on_a_late_receiver_resolves() =>
        Clean(await Run("public static class Factory { public static Runner Create() => new DirectRunner(); }",
                        "Factory.Create().Run<Action>(() => _tally.Hits++);", generic: true));

    [Fact]
    public async Task Receiver_produced_only_by_another_fallback_keeps_todays_behaviour()
    {
        var source = Source("public sealed class Factory { public Runner Create() => new DirectRunner(); }",
                            "Factory? factory = null; factory!.Create().Run(() => _tally.Hits++);");
        var result = await Analyze(source);
        var heap = Solve(source);

        Assert.Contains(heap.Heap.Instances.Values, instance => instance.BodyId.Contains("Factory.Create", StringComparison.Ordinal) && instance.IsReceiverless);
        Assert.Contains(heap.Heap.Instances.Values, instance => instance.BodyId.Contains("Runner.Run", StringComparison.Ordinal) && instance.IsReceiverless);
        Assert.True(result.Coverage[0].Skips[CoverageCounters.NO_RECEIVER_OBJECT] > 0);
    }

    [Fact]
    public async Task Fallback_waves_terminate()
    {
        var source = Source("public sealed class Factory { public Runner Create() => new DirectRunner(); }",
                            "Factory? factory = null; factory!.Create().Run(() => _tally.Hits++);");
        var result = await Analyze(source);
        var heap = Solve(source);

        Assert.NotEmpty(result.Coverage);
        Assert.True(heap.Heap.Instances.Values.Count(instance => instance.IsReceiverless) <= 2);
    }

    [Fact]
    public async Task Receiver_without_objects_at_the_fixpoint_stays_receiverless()
    {
        var source = Source("", "Runner? runner = null; runner!.Run(() => _tally.Hits++);");
        var result = await Analyze(source);
        var heap = Solve(source);

        Assert.Contains(heap.Heap.Instances.Values, instance => instance.BodyId.Contains("Runner.Run", StringComparison.Ordinal) && instance.IsReceiverless);
        Assert.True(result.Coverage[0].Skips[CoverageCounters.NO_RECEIVER_OBJECT] > 0);
    }

    [Fact]
    public async Task Call_whose_receivers_type_safety_excludes_gets_no_fallback()
    {
        var source = Source("", "((Runner)(object)new System.Collections.Generic.List<int>()).Run(() => _tally.Hits++);");
        var result = await Analyze(source);
        var heap = Solve(source);

        Assert.DoesNotContain(heap.Heap.Instances.Values,
                              instance => instance.BodyId.Contains("Runner.Run", StringComparison.Ordinal) && instance.IsReceiverless);
        Assert.DoesNotContain(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Hits"]));
        Assert.Equal(0, result.Coverage[0].Skips[CoverageCounters.NO_RECEIVER_OBJECT]);
    }

    private static string Source(string extra, string action, bool generic = false) => $$"""
        public sealed class Tally { public int Hits; }
        public abstract class Runner
        {
            public void Run{{(generic ? "<T>" : "")}}(Action work) => Impl(work);
            protected abstract void Impl(Action work);
        }
        public sealed class DirectRunner : Runner { protected override void Impl(Action work) => work(); }
        {{extra}}
        public sealed class CaseController : ControllerBase
        {
            private readonly Tally _tally;
            public CaseController(Tally tally) => _tally = tally;
            public void Post() { {{action}} }
        }
        """ + Startup("services.AddSingleton<Tally>();");

    private static Task<AnalysisResult> Analyze(string source) =>
        PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", Usings + source)), ROOT_DIRECTORY, CancellationToken.None);

    private static Task<AnalysisResult> Run(string extra, string action, bool generic = false) => Analyze(Source(extra, action, generic));

    private static void Clean(AnalysisResult result)
    {
        var hits = result.Accesses.Where(access => access.Resource.AccessPath.SequenceEqual(["Hits"])).ToArray();
        Assert.Single(hits);
        Assert.Equal("CaseController.Post()", hits[0].Symbol);
        Assert.StartsWith("root:", hits[0].ExecutionId, StringComparison.Ordinal);
        Assert.Empty(result.Coverage[0].Gaps);
        Assert.Equal(0, result.Coverage[0].Skips[CoverageCounters.NO_RECEIVER_OBJECT]);
    }
}
