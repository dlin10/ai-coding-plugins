using System.Diagnostics;
using System.Collections;
using System.Reflection;
using ConcurrencyHunter.Accesses;
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
            public class Box { public int Value; public Box() { } public void Touch() { Value = 1; } }
            public class SampleController : ControllerBase { public void Post() { var box = new Box(); box.Touch(); } }
            """ + EngineFixture.Startup());
        var heap = creates.Heap;
        // The walk resolves receivers while it discovers the entries it takes, before it walks any node of them, and what an access
        // touches once the walk is done: the cut at the first resolution for the store in Touch reports the walk's visits.
        var observed = new HeapSolution(heap.Regions, heap.Instances, heap.Edges, heap.TypeInitializers, heap.Constructions, heap.Counters,
                                       heap.ReachableBodies, heap.LoweredNotReached, heap.NoReceiverObjects,
                                       (instance, value) =>
                                       {
                                           if (instance.Contains("Box.Touch", StringComparison.Ordinal))
                                               cancellation.Cancel();
                                           return heap.Resolve(instance, value);
                                       },
                                       heap.PointsTo, heap.RootInstances, heap.StaticRegionOf, heap.FieldsOf, heap.DelegateCaptures)
        {
            ExecutionEdges = heap.ExecutionEdges,
            StartupRegions = heap.StartupRegions
        };
        var partial = Assert.Throws<EngineStageCancelledException>(() =>
            ExecutionModel.Build(Scope(creates.Program, creates.Summaries), observed, cancellation.Token));
        Assert.True(partial.Counters["walkVisits"] > 0);
    }

    [Fact]
    public void Cancelled_access_collection_reports_its_counters()
    {
        var solved = EngineFixture.Solve("public class SampleController : ControllerBase { public int Count; public void Post() { Count++; } }" +
                                         EngineFixture.Startup());
        var scope = Scope(solved.Program, solved.Summaries);
        var input = new InterproceduralInput(scope, solved.Heap, ExecutionModel.Build(scope, solved.Heap, CancellationToken.None));
        var error = Assert.Throws<EngineStageCancelledException>(() => InterproceduralAccesses.Collect(input, new CancellationToken(true)));
        Assert.Equal("accesses", error.Stage);
        Assert.Equal(0, error.Counters["executions"]);
        Assert.Equal(0, error.Counters["accesses"]);
        Assert.NotEmpty(InterproceduralAccesses.Collect(input, CancellationToken.None).Accesses);
    }

    [Theory]
    [InlineData("entry", false)]
    [InlineData("entry", true)]
    [InlineData("preparation", false)]
    [InlineData("preparation", true)]
    [InlineData("coverage", false)]
    [InlineData("coverage", true)]
    public void Every_part_of_access_collection_attributes_cancellation(string part, bool enclosing)
    {
        var solved = EngineFixture.Solve("public class SampleController : ControllerBase { public int Count; public void Post() { Count++; } }" +
                                         EngineFixture.Startup());
        var scope = Scope(solved.Program, solved.Summaries);
        var executions = ExecutionModel.Build(scope, solved.Heap, CancellationToken.None);
        var input = new InterproceduralInput(scope, solved.Heap, executions);
        var complete = InterproceduralAccesses.Collect(input, CancellationToken.None);
        Assert.NotEmpty(complete.Accesses);
        using var cancellation = new CancellationTokenSource();
        var cancelled = false;
        using var outer = WorkCancellation.Enter(enclosing ? cancellation.Token : CancellationToken.None);
        using var work = WorkCancellation.Enter(enclosing ? CancellationToken.None : cancellation.Token, () =>
        {
            var method = part == "preparation" ? "Discoveries" : "Coverage";
            if (!cancelled && (part == "entry" || new StackTrace().GetFrames().Any(frame =>
                    frame.GetMethod() is { } called && called.DeclaringType == typeof(InterproceduralAccesses) && called.Name == method)))
            {
                cancelled = true;
                cancellation.Cancel();
            }
        });

        var error = Assert.Throws<EngineStageCancelledException>(() =>
            InterproceduralAccesses.Collect(input, enclosing ? CancellationToken.None : cancellation.Token));

        Assert.True(cancelled);
        Assert.Equal("accesses", error.Stage);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.Equal(part == "coverage" ? executions.Executions.Count : 0, error.Counters["executions"]);
        Assert.Equal(part == "coverage" ? complete.Accesses.Count : 0, error.Counters["accesses"]);
    }

    private const string STANDING = """
        public class SampleController : ControllerBase
        {
            private static readonly System.Collections.Generic.Dictionary<string, int> Cache = new();
            private static System.Collections.Generic.Dictionary<string, int> Store() => Cache;

            [HttpPost("/cache")]
            public void Post(string key)
            {
                if (!Store().ContainsKey(key))
                    Store().Add(key, 1);
            }
        }
        """;

    [Fact]
    public void Cancellation_inside_standing_member_accesses_stops_the_collection()
    {
        var solved = EngineFixture.Solve(STANDING + EngineFixture.Startup());
        var scope = Scope(solved.Program, solved.Summaries);
        var executions = ExecutionModel.Build(scope, solved.Heap, CancellationToken.None);
        var cache = solved.Heap.Regions.Values.Single(region => region.TypeKey?.Contains("Dictionary", StringComparison.Ordinal) == true).Identity;
        // The dictionary is reached through a call, so no field of the action's body names it: only the members of the table the action
        // calls on it, and the check one of them depends on, ask the heap about it.
        Assert.NotEmpty(InterproceduralAccesses.Collect(new InterproceduralInput(scope, solved.Heap, executions), CancellationToken.None).Accesses);

        using var cancellation = new CancellationTokenSource();
        var asked = 0;
        var observed = Observed(solved.Heap, regions =>
        {
            if (!regions.Contains(cache))
                return;
            asked++;
            cancellation.Cancel();
        });
        var error = Assert.Throws<EngineStageCancelledException>(() =>
            InterproceduralAccesses.Collect(new InterproceduralInput(scope, observed, executions), cancellation.Token));

        Assert.Equal("accesses", error.Stage);
        // The loops under the standing members check as they go: the question that cancelled is followed by at most the one it asks
        // beside it, where without the checks the collection asks every question of the emission before it reaches a check.
        Assert.InRange(asked, 1, 3);
    }

    [Fact]
    public void Cancelled_return_summaries_stop_before_they_summarize_anything()
    {
        var solved = EngineFixture.Solve(STANDING + EngineFixture.Startup());

        Assert.NotNull(Summarize(solved.Heap, CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() => Summarize(solved.Heap, new CancellationToken(true)));
    }

    [Fact]
    public void Cancellation_inside_the_return_summaries_fixpoint_stops_it()
    {
        var solved = EngineFixture.Solve(STANDING + EngineFixture.Startup());
        using var cancellation = new CancellationTokenSource();
        var rounds = 0;
        // The constructor reads the instances' keys while it finds the components and sets its items, and their values only in the
        // fixpoint: the cancellation comes as the fixpoint starts, and the constructor ends at its next check instead of at its fixpoint.
        var observed = WithInstances(solved.Heap, new ObservedInstances(solved.Heap.Instances, () =>
        {
            rounds++;
            cancellation.Cancel();
        }));

        Assert.Throws<OperationCanceledException>(() => Summarize(observed, cancellation.Token));
        Assert.Equal(1, rounds);
    }

    [Fact]
    public void The_return_summaries_check_as_they_materialize_the_call_edges()
    {
        var solved = EngineFixture.Solve(STANDING + EngineFixture.Startup());
        Assert.True(solved.Heap.Edges.Count > 1);
        using var cancellation = new CancellationTokenSource();
        var taken = 0;
        // The first edge cancels the token as it is handed over: the constructor ends at the check after that edge, where without it
        // the edges are all read before the first check of the components or the fixpoint.
        IEnumerable<CallEdge> Cancelling()
        {
            foreach (var edge in solved.Heap.Edges)
            {
                taken++;
                cancellation.Cancel();
                yield return edge;
            }
        }

        Assert.Throws<OperationCanceledException>(() => Summarize(solved.Heap, Cancelling(), cancellation.Token));
        Assert.Equal(1, taken);
    }

    [Fact]
    public void Cancellation_inside_the_lock_solving_of_the_collection_stops_it()
    {
        var solved = EngineFixture.Solve("""
            public class SampleController : ControllerBase
            {
                private readonly object _gate = new();
                public int Count;

                [HttpPost("/count")]
                public void Post() { lock (_gate) { Count++; } }
            }
            """ + EngineFixture.Startup());
        var scope = Scope(solved.Program, solved.Summaries);
        var executions = ExecutionModel.Build(scope, solved.Heap, CancellationToken.None);
        var gate = Assert.Single(solved.Heap.Regions.Values, region => region.Kind == HeapRegionKind.Allocation).Identity;
        var baseline = 0;
        Assert.NotEmpty(InterproceduralAccesses.Collect(
            new InterproceduralInput(scope, Observed(solved.Heap, regions => baseline += regions.Contains(gate) ? 1 : 0), executions),
            CancellationToken.None).Accesses);
        Assert.True(baseline > 2);

        using var cancellation = new CancellationTokenSource();
        var asked = 0;
        var observed = Observed(solved.Heap, regions =>
        {
            if (!regions.Contains(gate))
                return;
            asked++;
            cancellation.Cancel();
        });
        var error = Assert.Throws<EngineStageCancelledException>(() =>
            InterproceduralAccesses.Collect(new InterproceduralInput(scope, observed, executions), cancellation.Token));

        Assert.Equal("accesses", error.Stage);
        // The solvers of locks ask the heap about the lock again for every operation they walk: the one that cancelled is followed by
        // at most the one it asks beside it, not by the questions of the rest of the solving.
        Assert.InRange(asked, 1, 2);
    }

    [Fact]
    public void Cancelled_lock_solving_stops_in_each_of_its_walks()
    {
        var reached = EngineFixture.Reach("""
            public class SampleController : ControllerBase
            {
                private readonly object _gate = new();
                public int Count;

                [HttpPost("/count")]
                public void Post() { lock (_gate) { Count++; } }
            }
            """ + EngineFixture.Startup());
        var body = reached.Result.Bodies.Single(pair => pair.Key.EndsWith(":M:SampleController.Post", StringComparison.Ordinal)).Value;
        var operations = body.Blocks.SelectMany(block => block.Operations).Select(operation => operation.Id).ToArray();
        Func<IrOperation, LockEffect> none = _ => LockEffect.None;
        var cancelled = new CancellationToken(true);

        Assert.NotNull(MustHeldLocks.Compute(body, none));
        Assert.Throws<OperationCanceledException>(() => MustHeldLocks.Compute(body, none, null, cancelled));
        Assert.Throws<OperationCanceledException>(() => MustHeldLocks.ReleasesOnEveryPath(body, none, "key", cancelled));
        Assert.Throws<OperationCanceledException>(() => MustHeldLocks.ReachesWithoutEntry(body, none, "key", operations[^1], cancelled));
        Assert.Throws<OperationCanceledException>(() => MustHeldLocks.Reaches(body, operations[0], _ => false, cancelled));
        Assert.Throws<OperationCanceledException>(() => MustHeldLocks.HoldsBetween(body, none, "key", operations[0], operations[^1], cancelled));
        // Without a token the same walks run as they always ran.
        Assert.False(MustHeldLocks.Reaches(body, operations[0], _ => false));
        Assert.True(MustHeldLocks.ReachesWithoutEntry(body, none, "key", operations[^1]));
    }

    /// <summary>The returns of a heap's instances, as the execution collector summarizes them: the private type is built with its
    /// constructor, over the heap's own call edges.</summary>
    /// <param name="heap">The heap.</param>
    /// <param name="cancellationToken">The token the summaries observe.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    private static object Summarize(HeapSolution heap, CancellationToken cancellationToken) => Summarize(heap, heap.Edges, cancellationToken);

    /// <summary>The returns of a heap's instances over the call edges given, as the execution collector summarizes them.</summary>
    /// <param name="heap">The heap.</param>
    /// <param name="edges">The call edges the summaries are built over.</param>
    /// <param name="cancellationToken">The token the summaries observe.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    private static object Summarize(HeapSolution heap, IEnumerable<CallEdge> edges, CancellationToken cancellationToken)
    {
        var summaries = typeof(InterproceduralAccesses).GetNestedType("ExecutionCollector", BindingFlags.NonPublic)!
                                                       .GetNestedType("ReturnSummaries", BindingFlags.NonPublic)!;
        var constructor = summaries.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
        try
        {
            return constructor.Invoke([heap, edges, cancellationToken]);
        }
        catch (TargetInvocationException error) when (error.InnerException is OperationCanceledException cancelled)
        {
            throw new OperationCanceledException(cancelled.Message, cancelled, cancellationToken);
        }
    }

    /// <summary>A solved heap that answers with other instances than its own, and is the heap in every other property.</summary>
    /// <param name="heap">The solved heap.</param>
    /// <param name="instances">The instances it names.</param>
    private static HeapSolution WithInstances(HeapSolution heap, IReadOnlyDictionary<string, MethodInstance> instances)
    {
        var replaced = new HeapSolution(heap.Regions, instances, heap.Edges, heap.TypeInitializers, heap.Constructions, heap.Counters,
                                        heap.ReachableBodies, heap.LoweredNotReached, heap.NoReceiverObjects, heap.Resolve, heap.PointsTo,
                                        heap.RootInstances, heap.StaticRegionOf, heap.FieldsOf, heap.DelegateCaptures);
        foreach (var property in typeof(HeapSolution).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                                     .Where(property => property.SetMethod is not null))
            property.SetValue(replaced, property.GetValue(heap));
        return replaced;
    }

    /// <summary>A solved heap whose answers are the heap's, each reported to <paramref name="asked"/> before it is given: the regions
    /// an object value resolves to, and the region a slot is read from.</summary>
    /// <param name="heap">The solved heap.</param>
    /// <param name="asked">Told the regions of each question.</param>
    private static HeapSolution Observed(HeapSolution heap, Action<IReadOnlyCollection<string>> asked)
    {
        var observed = new HeapSolution(heap.Regions, heap.Instances, heap.Edges, heap.TypeInitializers, heap.Constructions, heap.Counters,
                                        heap.ReachableBodies, heap.LoweredNotReached, heap.NoReceiverObjects,
                                        (instance, value) =>
                                        {
                                            var regions = heap.Resolve(instance, value);
                                            asked(regions);
                                            return regions;
                                        },
                                        (region, slot) =>
                                        {
                                            asked([region]);
                                            return heap.PointsTo(region, slot);
                                        },
                                        heap.RootInstances, heap.StaticRegionOf, heap.FieldsOf, heap.DelegateCaptures);
        // Every stage after the solve reads the properties the solve set: the observed heap carries them all.
        foreach (var property in typeof(HeapSolution).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                                     .Where(property => property.SetMethod is not null))
            property.SetValue(observed, property.GetValue(heap));
        return observed;
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
        Assert.Equal("notRun", CoreLibBenchmarkTests.Stages(error, null)["accesses"]!["status"]!.GetValue<string>());
        var memory = CoreLibBenchmarkTests.Stages(error, "memory")["accesses"]!;
        Assert.Equal("cut", memory["status"]!.GetValue<string>());
        Assert.Equal(0, memory["counters"]!["executions"]!.GetValue<int>());
    }

    [Fact]
    public void Completed_pipeline_reports_every_stage_finished()
    {
        var run = Pipeline(CancellationToken.None, _ => { });
        var stages = CoreLibBenchmarkTests.Stages(run);
        Assert.Equal(Enum.GetValues<ScopeStep>().Length, stages.Count);
        Assert.All(stages, stage => Assert.Equal("finished", stage.Value!["status"]!.GetValue<string>()));
        Assert.True(stages["executions"]!["counters"]!["walkVisits"]!.GetValue<int>() > 0);
        Assert.Equal(run.Executions!.Executions.Count, stages["accesses"]!["counters"]!["executions"]!.GetValue<int>());
        Assert.Equal(run.Collection!.Accesses.Count, stages["accesses"]!["counters"]!["accesses"]!.GetValue<int>());
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

    /// <summary>A heap's instances that report each request for their values, which only a pass over every instance makes.</summary>
    /// <param name="instances">The instances.</param>
    /// <param name="valuesRead">Told when the values are asked for, before they are given.</param>
    private sealed class ObservedInstances(IReadOnlyDictionary<string, MethodInstance> instances, Action valuesRead)
        : IReadOnlyDictionary<string, MethodInstance>
    {
        public MethodInstance this[string key] => instances[key];
        public IEnumerable<string> Keys => instances.Keys;

        public IEnumerable<MethodInstance> Values
        {
            get
            {
                valuesRead();
                return instances.Values;
            }
        }

        public int Count => instances.Count;
        public bool ContainsKey(string key) => instances.ContainsKey(key);
        public bool TryGetValue(string key, out MethodInstance value) => instances.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<string, MethodInstance>> GetEnumerator() => instances.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
