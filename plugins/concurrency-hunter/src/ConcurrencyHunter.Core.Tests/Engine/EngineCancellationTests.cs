using System.Collections;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class EngineCancellationTests
{
    [Fact]
    public void Cancelled_heap_solve_reports_its_counters()
    {
        var reached = EngineFixture.Reach("public class SampleController : ControllerBase { public void Post() { } }" + EngineFixture.Startup());
        var summaries = new SummaryCache(reached.Result.Bodies, reached.Input.Program, AnalysisLimits.Default);
        var scope = Scope(reached, summaries);
        var error = Assert.Throws<EngineStageCancelledException>(() => WholeProgram.Solve(scope, AnalysisLimits.Default, new CancellationToken(true)));
        Assert.Equal("heap", error.Stage);
        foreach (var counter in new[] { HeapCounters.PROPAGATE_PASSES, HeapCounters.INSTANCE_PROCESSINGS, HeapCounters.REFERENCE_LOOKUPS })
            Assert.Equal(0, error.Counters[counter]);
        var heap = WholeProgram.Solve(scope, AnalysisLimits.Default, CancellationToken.None);
        Assert.True(heap.Counters[HeapCounters.INSTANCE_PROCESSINGS] > 0);

        // Cancelling while a new callee's summary is built lets that bounded unit finish, then stops the solve.
        using var cancellation = new CancellationTokenSource();
        var calls = EngineFixture.Reach("""
            public static class Box { public static void Touch() { } }
            public class SampleController : ControllerBase { public void Post() { Box.Touch(); } }
            """ + EngineFixture.Startup());
        var bodies = new ObservedBodies(calls.Result.Bodies, key =>
        {
            if (key.EndsWith(":M:Box.Touch", StringComparison.Ordinal))
                cancellation.Cancel();
        });
        var partial = Assert.Throws<EngineStageCancelledException>(() => WholeProgram.Solve(
            Scope(calls, new SummaryCache(bodies, calls.Input.Program, AnalysisLimits.Default)), AnalysisLimits.Default, cancellation.Token));
        Assert.True(partial.Counters[HeapCounters.PROPAGATE_PASSES] > 0);
        Assert.True(partial.Counters[HeapCounters.INSTANCE_PROCESSINGS] > 0);
        Assert.Equal(0, partial.Counters[HeapCounters.REFERENCE_LOOKUPS]);
    }

    [Fact]
    public void Cancelled_execution_walk_reports_its_counters()
    {
        var solved = EngineFixture.Solve("public class SampleController : ControllerBase { public void Post() { } }" + EngineFixture.Startup());
        var scope = Scope(solved.Program, solved.Summaries);
        var error = Assert.Throws<EngineStageCancelledException>(() => ExecutionModel.Build(scope, solved.Heap, new CancellationToken(true)));
        Assert.Equal("executions", error.Stage);
        Assert.Equal(0, error.Counters["walkVisits"]);
        Assert.True(ExecutionModel.Build(scope, solved.Heap, CancellationToken.None).WalkVisits > 0);

        using var cancellation = new CancellationTokenSource();
        var creates = EngineFixture.Solve("""
            public class Box { public Box() { } public void Touch() { } }
            public class Crate { public Crate() { } public void Touch() { } }
            public class SampleController : ControllerBase
            {
                public void Post() { var box = new Box(); box.Touch(); }
                public void Put() { var crate = new Crate(); crate.Touch(); }
            }
            """ + EngineFixture.Startup());
        var heap = creates.Heap;
        // The walk resolves receivers while it discovers an entry, before it walks any node of it, so the cut comes at the first
        // resolution for the second action: by then the first one's nodes have been walked.
        string? first = null;
        var observed = new HeapSolution(heap.Regions, heap.Instances, heap.Edges, heap.TypeInitializers, heap.Constructions, heap.Counters,
                                       heap.ReachableBodies, heap.LoweredNotReached, heap.NoReceiverObjects,
                                       (instance, value) =>
                                       {
                                           var action = instance.Contains("Crate", StringComparison.Ordinal) || instance.Contains(".Put", StringComparison.Ordinal)
                                               ? "Put"
                                               : "Post";
                                           first ??= action;
                                           if (action != first)
                                               cancellation.Cancel();
                                           return heap.Resolve(instance, value);
                                       },
                                       heap.PointsTo, heap.Cell, heap.RootInstances, heap.StaticRegionOf, heap.FieldsOf, heap.DelegateCaptures)
        {
            ExecutionEdges = heap.ExecutionEdges,
            StartupRegions = heap.StartupRegions
        };
        var partial = Assert.Throws<EngineStageCancelledException>(() =>
            ExecutionModel.Build(Scope(creates.Program, creates.Summaries), observed, cancellation.Token));
        Assert.True(partial.Counters["walkVisits"] > 0);
    }

    [Fact]
    public void Cancelled_pipeline_reports_its_completed_stages()
    {
        using var cancellation = new CancellationTokenSource();
        var error = Assert.Throws<ScopeCancelledException>(() => Pipeline(cancellation.Token, step =>
        {
            if (step == ScopeStep.Accesses)
                cancellation.Cancel();
        }));
        Assert.Equal(ScopeStep.Accesses, error.Step);
        Assert.False(error.Started);
        Assert.Equal(6, error.CompletedSteps.Count);
        Assert.True(error.ReachableBodies > 0);
        Assert.True(error.Counters[ScopeStep.SummariesAndFixpoint][HeapCounters.INSTANCE_PROCESSINGS] > 0);
        Assert.True(error.Counters[ScopeStep.Executions]["walkVisits"] > 0);
        Assert.DoesNotContain(ScopeStep.Accesses, error.CompletedSteps.Keys);
        Assert.Equal("notRun", CoreLibBenchmarkTests.Stages(error, "memory")["accesses"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public void Cancellation_at_a_stage_start_leaves_that_stage_not_run()
    {
        using var cancellation = new CancellationTokenSource();
        var notified = new List<ScopeStep>();
        var error = Assert.Throws<ScopeCancelledException>(() => Pipeline(cancellation.Token, step =>
        {
            notified.Add(step);
            if (step == ScopeStep.SummariesAndFixpoint)
                cancellation.Cancel();
        }));
        Assert.Equal(ScopeStep.SummariesAndFixpoint, error.Step);
        Assert.False(error.Started);
        Assert.Equal(TimeSpan.Zero, error.Elapsed);
        Assert.Empty(error.Counters);
        Assert.DoesNotContain(ScopeStep.SummariesAndFixpoint, error.CompletedSteps.Keys);
        Assert.Equal(new[] { ScopeStep.RootDiscovery, ScopeStep.ProgramIndex, ScopeStep.ReachableSet, ScopeStep.SummariesAndFixpoint }, notified);
        var planned = CoreLibBenchmarkTests.Stages(error, null);
        Assert.Equal("notRun", planned["summariesAndFixpoint"]!["status"]!.GetValue<string>());
        Assert.Null(planned["summariesAndFixpoint"]!["seconds"]);
        var memory = CoreLibBenchmarkTests.Stages(error, "memory");
        Assert.Equal("cut", memory["summariesAndFixpoint"]!["status"]!.GetValue<string>());
        Assert.Equal("memory", memory["summariesAndFixpoint"]!["reason"]!.GetValue<string>());
        Assert.Equal(0, memory["summariesAndFixpoint"]!["counters"]![HeapCounters.INSTANCE_PROCESSINGS]!.GetValue<int>());
    }

    [Fact]
    public void Cancellation_in_the_reachable_set_reports_that_step_and_its_lowering_time()
    {
        using var cancellation = new CancellationTokenSource();
        var compilation = Driver();
        var comparer = new CancellingComparer(cancellation);
        var cache = new Dictionary<(Compilation Compilation, string BodyId, LibraryModels Models), IrLoweredMethod?>(comparer)
        {
            [(compilation, "unused", LibraryModels.BuiltIn)] = null
        };
        var error = Assert.Throws<ScopeCancelledException>(() => ScopePipeline.Run("scope:cancellation", [compilation], [], Path.GetTempPath(),
            new ProviderRegistry([new DriverRootProvider()]), LibraryModels.BuiltIn, AnalysisLimits.Default, cache, null, cancellation.Token,
            step => comparer.Armed = step == ScopeStep.ReachableSet));
        Assert.Equal(ScopeStep.ReachableSet, error.Step);
        Assert.True(error.Started);
        Assert.True(error.Lowering > TimeSpan.Zero);
        Assert.True(error.Elapsed >= TimeSpan.Zero);
        Assert.Equal(2, error.CompletedSteps.Count);
        Assert.Null(error.ReachableBodies);
        Assert.DoesNotContain(ScopeStep.Lowering, error.CompletedSteps.Keys);
    }

    private static ScopeRun Pipeline(CancellationToken token, Action<ScopeStep> stepStarted)
    {
        var compilation = Driver();
        return ScopePipeline.Run("scope:cancellation", [compilation], [], Path.GetTempPath(), new ProviderRegistry([new DriverRootProvider()]),
                                 LibraryModels.BuiltIn, AnalysisLimits.Default, new(), null, token, stepStarted);
    }

    private static CSharpCompilation Driver() =>
        CSharpCompilation.Create(DriverSynthesizer.ASSEMBLY,
            [CSharpSyntaxTree.ParseText("public static class ModelDriver { public static void V_Call() { var x = new object(); } }",
                                       path: Path.Combine(Path.GetTempPath(), "CancelDriver.cs"))],
            EmittedAssemblies.RuntimeReferences, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static ScopeProgram Scope(WholeProgramRun run, SummaryCache summaries) =>
        new(run.ScopeId, run.Input.Roots, run.Result, summaries, run.Input.Program, run.Input.DiIndex, run.Input.InjectionBindings)
        {
            MetadataSupertypes = run.MetadataSupertypes
        };

    private sealed class CancellingComparer(CancellationTokenSource cancellation) :
        IEqualityComparer<(Compilation Compilation, string BodyId, LibraryModels Models)>
    {
        internal bool Armed { get; set; }

        public bool Equals((Compilation Compilation, string BodyId, LibraryModels Models) x,
                           (Compilation Compilation, string BodyId, LibraryModels Models) y) => x == y;

        public int GetHashCode((Compilation Compilation, string BodyId, LibraryModels Models) obj)
        {
            if (Armed)
                cancellation.Cancel();
            return obj.GetHashCode();
        }
    }

    private sealed class ObservedBodies(IReadOnlyDictionary<string, IrBody> bodies, Action<string> read) : IReadOnlyDictionary<string, IrBody>
    {
        public IrBody this[string key] { get { read(key); return bodies[key]; } }
        public IEnumerable<string> Keys => bodies.Keys;
        public IEnumerable<IrBody> Values => bodies.Values;
        public int Count => bodies.Count;
        public bool ContainsKey(string key) => bodies.ContainsKey(key);
        public bool TryGetValue(string key, out IrBody value)
        {
            read(key);
            return bodies.TryGetValue(key, out value!);
        }

        public IEnumerator<KeyValuePair<string, IrBody>> GetEnumerator() => bodies.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
