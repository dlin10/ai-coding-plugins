using System.Runtime.InteropServices;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class StageCancellationTests
{
    private static readonly ScopeStep[] PipelineSteps =
    [
        ScopeStep.RootDiscovery, ScopeStep.ProgramIndex, ScopeStep.ReachableSet,
        ScopeStep.SummariesAndFixpoint, ScopeStep.Executions, ScopeStep.Accesses
    ];

    private static readonly IReadOnlyDictionary<ScopeStep, ScopeStep> NestedSteps = new Dictionary<ScopeStep, ScopeStep>
    {
        [ScopeStep.Lowering] = ScopeStep.ReachableSet
    };

    // The decompilation's workers come from the shared thread pool, which a full test run keeps busy: its queue can hold a
    // worker back for many seconds. The wait bounds a hang, not the workers' speed.
    private static readonly TimeSpan WorkerPatience = TimeSpan.FromMinutes(2);

    public static TheoryData<ScopeStep> Steps() => new(PipelineSteps);

    [Theory]
    [MemberData(nameof(Steps))]
    public void Cancellation_deep_inside_each_pipeline_step_stops_it(ScopeStep step)
    {
        var compilation = CSharpCompilation.Create(DriverSynthesizer.ASSEMBLY,
            [CSharpSyntaxTree.ParseText("public static class ModelDriver { public static void V_Call() { var x = new object(); } }",
                                       path: Path.Combine(Path.GetTempPath(), "CancelDriver.cs"))],
            EmittedAssemblies.RuntimeReferences, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var models = LibraryModels.BuiltIn;
        using var cancellation = new CancellationTokenSource();

        Exception? Run(CheckObservations<ScopeStep> observations)
        {
            using var work = WorkCancellation.Enter(CancellationToken.None, observations.Observe);
            return Record.Exception(() => ScopePipeline.Run("scope:cancellation", [compilation], [], Path.GetTempPath(),
                new ProviderRegistry([new DriverRootProvider()]), models, AnalysisLimits.Default, new(), null, cancellation.Token,
                observations.Start));
        }

        var baseline = new CheckObservations<ScopeStep>(step, cancellation);
        Assert.Null(Run(baseline));
        var count = baseline.Count;
        Assert.True(count >= 3, $"{step} had only {count} checks");

        var cancelled = new CheckObservations<ScopeStep>(step, cancellation, (count + 1) / 2);
        var error = Assert.IsType<ScopeCancelledException>(Run(cancelled));
        Assert.Equal(step, error.Step);
        cancelled.AssertStopped();
    }

    [Fact]
    public void Every_scope_step_is_a_row_or_nested_in_one()
    {
        Assert.Equal(PipelineSteps.Length, PipelineSteps.Distinct().Count());
        Assert.Equal(new[] { ScopeStep.Lowering }, NestedSteps.Keys);
        Assert.Equal(ScopeStep.ReachableSet, NestedSteps[ScopeStep.Lowering]);
        Assert.All(NestedSteps.Values, parent => Assert.Contains(parent, PipelineSteps));
        Assert.All(Enum.GetValues<ScopeStep>(), step =>
            Assert.True(PipelineSteps.Contains(step) ^ NestedSteps.ContainsKey(step), $"{step} must be a row or a named nested step"));
    }

    // GenerationStage is internal; public xUnit theories name its values by their names.
    public static TheoryData<string> Stages() => new(Enum.GetNames<GenerationStage>());

    [Theory]
    [MemberData(nameof(Stages))]
    public async Task Cancellation_deep_inside_each_generation_stage_stops_it(string name)
    {
        var stage = Enum.Parse<GenerationStage>(name);
        using var install = new GenerationInstall();
        install.SharedFramework("8.0.5", runtime: true);
        const string packageName = "Fixture.Cancel";
        var package = install.Package(packageName, "1.0.0", "", $"net8.0/{packageName}");
        EmittedAssemblies.Write(Path.Combine(package, "lib", "net8.0", packageName + ".dll"), packageName, """
            using System;
            namespace Lib
            {
                public sealed class Holder
                {
                    private readonly Action _action;
                    public Holder(Action action) { _action = action; }
                    public void Fire() { _action(); }
                }
            }
            """);
        var request = new GenerationRequest(packageName, "1.0.0", "M:Lib.Holder.#ctor(System.Action)", packageName, "net8.0");
        using var cancellation = new CancellationTokenSource();

        async Task<(Exception? Error, IReadOnlyList<OwnedCompilation> Created)> Run(CheckObservations<GenerationStage> observations)
        {
            var ownership = new CompilationOwnership();
            using var owned = ownership.Open();
            using var work = WorkCancellation.Enter(CancellationToken.None, observations.Observe);
            var error = Record.Exception(() => ModelGenerator.Trace(request, install.Resolver(), cancellation.Token,
                (reported, _) => observations.Start(reported)));
            await FinishCompilations(ownership);
            return (error, ownership.Created);
        }

        var baseline = new CheckObservations<GenerationStage>(stage, cancellation);
        var first = await Run(baseline);
        Assert.Null(first.Error);
        Assert.Single(first.Created);
        var count = baseline.Count;
        Assert.True(count >= 3, $"{stage} had only {count} checks");

        var cancelled = new CheckObservations<GenerationStage>(stage, cancellation, (count + 1) / 2);
        var second = await Run(cancelled);
        Assert.IsAssignableFrom<OperationCanceledException>(second.Error);
        if (stage == GenerationStage.Resolve)
            Assert.Empty(second.Created);
        else
            Assert.Single(second.Created);
        cancelled.AssertStopped();
        Assert.DoesNotContain(cancelled.Started, reported => reported > stage);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task Decompilation_stops_every_worker_after_the_cancellation(int workers, bool enclosing)
    {
        using var install = new GenerationInstall();
        const int typeCount = 8;
        const string assemblyName = "Fixture.Workers";
        var source = string.Join("\n", Enumerable.Range(0, typeCount).Select(index =>
            $"public sealed class Type{index} {{ public int Value() => {index}; }}"));
        var path = EmittedAssemblies.Write(Path.Combine(install.Root, assemblyName + ".dll"), assemblyName, source);
        var runtime = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        var assembly = new ImplementationAssembly(assemblyName, path, null, "1.0.0", $"net{Environment.Version.Major}.0", runtime,
            EmittedAssemblies.RUNTIME_ASSEMBLIES.Select(reference => Path.Combine(runtime, reference + ".dll")).ToArray(),
            [], "0.0.0.0", "", Guid.Empty);
        using var cancellation = new CancellationTokenSource();
        using var ready = new CountdownEvent(workers);
        using var cancelled = new ManualResetEventSlim();
        var gate = new object();
        var firstTypes = new List<(int Thread, string Type)>();
        var printed = new List<(int Thread, string Type, bool AfterCancellation)>();
        // A failed wait is recorded, not thrown: an assertion thrown in a worker would reach the test only wrapped in the
        // parallel loop's AggregateException, under the name of the cancellation check.
        var probeFailures = new List<string>();
        var starting = 0;
        var probe = new LibraryCompilationProbe
        {
            Workers = workers,
            TypeStarting = type =>
            {
                if (Interlocked.Increment(ref starting) <= workers)
                {
                    lock (gate)
                        firstTypes.Add((Environment.CurrentManagedThreadId, type));
                    ready.Signal();
                    if (!ready.Wait(WorkerPatience))
                        lock (gate)
                            probeFailures.Add("рабочие не работали параллельно");
                }
                else if (!cancelled.Wait(WorkerPatience))
                    lock (gate)
                        probeFailures.Add("отмена не сработала после печати первого типа");
            },
            TypePrinted = type =>
            {
                lock (gate)
                {
                    printed.Add((Environment.CurrentManagedThreadId, type, cancellation.IsCancellationRequested));
                    if (!cancellation.IsCancellationRequested)
                    {
                        cancellation.Cancel();
                        cancelled.Set();
                    }
                }
            }
        };
        var ownership = new CompilationOwnership();
        using var owned = ownership.Open();
        using var outer = WorkCancellation.Enter(enclosing ? cancellation.Token : CancellationToken.None);
        var token = enclosing ? WorkCancellation.Token : cancellation.Token;
        var error = Record.Exception(() => LibraryCompilation.Compile(assembly, new HashSet<string>(), new GenerationChecks(token), probe));
        await FinishCompilations(ownership);

        Assert.Empty(probeFailures);
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Single(ownership.Created);
        Assert.True(ready.IsSet, "рабочие не работали параллельно");
        Assert.Equal(workers, firstTypes.Select(item => item.Thread).Distinct().Count());
        Assert.Equal(workers, firstTypes.Select(item => item.Type).Distinct().Count());
        Assert.NotEmpty(printed);
        Assert.All(printed, item => Assert.Contains((item.Thread, item.Type), firstTypes));
        var after = printed.Where(item => item.AfterCancellation).ToArray();
        Assert.InRange(after.Length, 0, workers - 1);
        Assert.Equal(after.Length, after.Select(item => item.Thread).Distinct().Count());
        Assert.True(printed.Count < typeCount);
    }

    private static async Task FinishCompilations(CompilationOwnership ownership)
    {
        try
        {
            foreach (var entry in ownership.Created)
                await Record.ExceptionAsync(async () => { await entry.Entry.Value; });
        }
        finally
        {
            LibraryCompilation.Release(ownership.Created);
        }
    }

    private sealed class CheckObservations<T>(T target, CancellationTokenSource cancellation, int? cancelAt = null) where T : struct, Enum
    {
        private readonly object _gate = new();
        private readonly List<T> _started = [];
        private T? _current;
        private int _total;
        private int _count;
        private int? _firedAt;
        private string? _afterCancellation;

        internal int Count { get { lock (_gate) return _count; } }
        internal IReadOnlyList<T> Started { get { lock (_gate) return _started.ToArray(); } }

        internal void Start(T stage)
        {
            lock (_gate)
            {
                _current = stage;
                _started.Add(stage);
            }
        }

        internal void Observe()
        {
            lock (_gate)
            {
                if (_firedAt.HasValue)
                    _afterCancellation ??= string.Join("\n", Environment.StackTrace.Split('\n').Take(10));
                _total++;
                if (_current is not { } current || !EqualityComparer<T>.Default.Equals(current, target))
                    return;
                _count++;
                if (_count == cancelAt)
                {
                    _firedAt = _total;
                    cancellation.Cancel();
                }
            }
        }

        internal void AssertStopped()
        {
            lock (_gate)
            {
                Assert.True(_firedAt.HasValue, $"{target} did not reach check {cancelAt}");
                Assert.True(_firedAt == _total, $"{target} checked again after cancellation: {_afterCancellation}");
                Assert.Equal(cancelAt, _count);
            }
        }
    }
}
