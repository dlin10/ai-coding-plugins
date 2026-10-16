using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class RequiresEngineBenchmarkFactAttribute : FactAttribute
{
    public RequiresEngineBenchmarkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CH_ENGINE_BENCH") != "1")
            Skip = "CH_ENGINE_BENCH is not 1. Run build/bench-corelib.ps1 to measure CoreLib explicitly.";
    }
}

/// <summary>Records independent CoreLib scopes. Compilation bypasses the process cache, and non-inlined scope methods return
/// only JSON observations so neither a completed run nor a cancellation's partial results survive the inter-scope collection.</summary>
public sealed class CoreLibBenchmarkTests
{
    [RequiresEngineBenchmarkFact]
    public void CoreLib_benchmark_records_a_point()
    {
        var label = Environment.GetEnvironmentVariable("CH_ENGINE_BENCH_LABEL");
        Assert.False(string.IsNullOrWhiteSpace(label), "CH_ENGINE_BENCH_LABEL must name the point.");
        var root = RepositoryRoot();
        var assembly = InstalledCoreLib();
        var selections = SelectScopes(assembly);
        CollectScope();
        var scopes = new JsonObject();
        foreach (var (scope, selected) in selections)
        {
            scopes[scope] = MeasureScope(assembly, selected);
            CollectScope();
        }

        var point = new JsonObject
        {
            ["label"] = label,
            ["recordedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["head"] = Git(root, "rev-parse", "HEAD").Trim(),
            ["dirty"] = Git(root, "status", "--porcelain").Length != 0,
            ["machine"] = Environment.MachineName,
            ["logicalCores"] = Environment.ProcessorCount,
            ["coreLibVersion"] = assembly.ImplementationVersion,
            ["scopes"] = scopes
        };
        var path = Path.Combine(root, "skills", "hunt", "evals", "metrics", "corelib-bench.json");
        var points = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsArray() : new JsonArray();
        points.Add(point);
        File.WriteAllText(path, points.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    /// <summary>A memory cancellation during library setup records a cut in the existing first-stage shape and lets both scopes finish.</summary>
    /// <param name="afterCompilation">Whether compilation returned before setup observes cancellation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Watchdog_cancellation_during_setup_records_each_scope(bool afterCompilation)
    {
        var scopes = new JsonObject();
        foreach (var scope in new[] { "four", "all" })
        {
            using var watchdog = new BenchmarkWatchdog(() => DateTimeOffset.UnixEpoch, () => 0, () => 1024, startTimer: false);
            scopes[scope] = RunScope("BenchmarkFixture", 4, watchdog, token =>
            {
                watchdog.Tick();
                if (!afterCompilation)
                    token.ThrowIfCancellationRequested();
                return new LibraryCompilationResult(CSharpCompilation.Create("BenchmarkFixture", options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)), null, 7, 3,
                                                    new HashSet<string>(), new HashSet<string>(), []);
            });
        }
        Assert.Equal(2, scopes.Count);
        foreach (var scope in scopes)
        {
            var point = scope.Value!;
            Assert.Equal(afterCompilation ? 7 : 0, point["bodies"]!.GetValue<int>());
            Assert.Equal(afterCompilation ? 3 : 0, point["externBodies"]!.GetValue<int>());
            Assert.Equal(4, point["selectedMembers"]!.GetValue<int>());
            Assert.Null(point["reachableBodies"]);
            var stages = point["stages"]!.AsObject();
            Assert.Equal("cut", stages["rootDiscovery"]!["status"]!.GetValue<string>());
            Assert.Equal("memory", stages["rootDiscovery"]!["reason"]!.GetValue<string>());
            Assert.True(stages["rootDiscovery"]!["seconds"]!.GetValue<double>() >= 0);
            Assert.All(stages.Where(stage => stage.Key != "rootDiscovery"),
                       stage => Assert.Equal("notRun", stage.Value!["status"]!.GetValue<string>()));
        }
    }

    /// <summary>The recorded point does not retain the selected compilation after a setup cut and the inter-scope collection.</summary>
    [Fact]
    public void A_setup_cut_releases_its_compilation_before_the_next_scope()
    {
        WeakReference<CSharpCompilation>? selected = null;
        using var watchdog = new BenchmarkWatchdog(() => DateTimeOffset.UnixEpoch, () => 0, () => 0, startTimer: false);
        var point = RunScope("BenchmarkFixture", 0, watchdog, _ =>
        {
            var compilation = CSharpCompilation.Create("BenchmarkFixture", options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            selected = new WeakReference<CSharpCompilation>(compilation);
            watchdog.Tick();
            return new LibraryCompilationResult(compilation, null, 0, 0, new HashSet<string>(), new HashSet<string>(), []);
        });
        CollectScope();
        Assert.NotNull(selected);
        Assert.False(selected.TryGetTarget(out _));
        Assert.Equal("memory", point["stages"]!["rootDiscovery"]!["reason"]!.GetValue<string>());
    }

    /// <summary>A cancellation from another source still fails the scope, including when the watchdog also cancelled.</summary>
    /// <param name="watchdogCancelled">Whether the watchdog has also observed low memory.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_unrelated_setup_cancellation_still_throws(bool watchdogCancelled)
    {
        using var watchdog = new BenchmarkWatchdog(() => DateTimeOffset.UnixEpoch, () => 0, () => 0, startTimer: false);
        var error = new OperationCanceledException(new CancellationToken(true));
        Assert.Same(error, Assert.Throws<OperationCanceledException>(() => RunScope("BenchmarkFixture", 0, watchdog, _ =>
        {
            if (watchdogCancelled)
                watchdog.Tick();
            throw error;
        })));
    }

    /// <summary>Selects both scopes without retaining the baseline compilation used to find their members.</summary>
    /// <param name="assembly">The implementation assembly whose members are selected.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IReadOnlyDictionary<string, IReadOnlySet<string>> SelectScopes(ImplementationAssembly assembly)
    {
        var baseline = CompileUncached(assembly, new HashSet<string>(), CancellationToken.None);
        Usable(baseline, CancellationToken.None);
        return new Dictionary<string, IReadOnlySet<string>>
        {
            ["four"] = BenchmarkScopes.Select(baseline.Compilation!, "four"),
            ["all"] = BenchmarkScopes.Select(baseline.Compilation!, "all")
        };
    }

    /// <summary>Creates a scope's sampling window and releases it before the inter-scope collection.</summary>
    /// <param name="assembly">The library implementation to compile and measure.</param>
    /// <param name="selected">The whole members to make extern.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static JsonObject MeasureScope(ImplementationAssembly assembly, IReadOnlySet<string> selected)
    {
        using var process = Process.GetCurrentProcess();
        using var watchdog = new BenchmarkWatchdog(() => DateTimeOffset.UtcNow, BenchmarkWatchdog.AvailableCommit, () =>
        {
            process.Refresh();
            return process.WorkingSet64;
        });
        return RunScope(assembly.AssemblyName, selected.Count, watchdog, token => CompileUncached(assembly, selected, token));
    }

    /// <summary>Uses the library compiler without its process-wide result cache, so the benchmark can release each compilation.</summary>
    /// <param name="assembly">The implementation assembly to decompile.</param>
    /// <param name="selected">The whole members to make extern.</param>
    /// <param name="cancellationToken">Cancels the decompilation and compilation.</param>
    private static LibraryCompilationResult CompileUncached(ImplementationAssembly assembly, IReadOnlySet<string> selected,
                                                            CancellationToken cancellationToken) =>
        LibraryCompilation.CompileModule(assembly.AssemblyName, LibraryCompilation.Decompile(assembly, cancellationToken),
                                         assembly.References.Select(reference => MetadataReference.CreateFromFile(reference)).ToArray(),
                                         cancellationToken, selected);

    /// <summary>Runs one benchmark scope and returns only the point's scalar observations, never its compilation or pipeline results.</summary>
    /// <param name="assemblyName">The library assembly whose built-in models are excluded.</param>
    /// <param name="selectedMembers">The count of members made extern for this scope.</param>
    /// <param name="watchdog">The scope's cancellation and sampling window.</param>
    /// <param name="compile">Compiles the scope's library with the watchdog token.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static JsonObject RunScope(string assemblyName, int selectedMembers, BenchmarkWatchdog watchdog,
                                       Func<CancellationToken, LibraryCompilationResult> compile)
    {
        var setup = Stopwatch.StartNew();
        LibraryCompilationResult? library = null;
        ScopeRun? run = null;
        ScopeCancelledException? stopped = null;
        try
        {
            library = compile(watchdog.Token);
            Usable(library, watchdog.Token);
            var compilation = library.Compilation!;
            var driver = CSharpCompilation.Create(DriverSynthesizer.ASSEMBLY,
                [CSharpSyntaxTree.ParseText("""
                    public sealed class Probe { public override string ToString() => "probe"; }
                    public static class ModelDriver
                    {
                        public static void V_Call() { string.Join(",", new object[] { new Probe() }); }
                    }
                    """, path: Path.Combine(Path.GetTempPath(), "CoreLibBenchmark", "ModelDriver.cs"))],
                [compilation.ToMetadataReference()], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.Empty(driver.GetDiagnostics(watchdog.Token).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
            var models = new LibraryModels(
                LibraryModels.BuiltIn.Members.Where(model => model.Assemblies.All(range => range.AssemblyName != assemblyName)),
                LibraryModels.BuiltIn.ImmutableTypes.Where(type => type.Assemblies.All(range => range.AssemblyName != assemblyName)));
            run = ScopePipeline.Run("scope:corelib-benchmark", [compilation, driver], [], Path.GetTempPath(),
                                    new ProviderRegistry([new DriverRootProvider()]), models, AnalysisLimits.Default, new(), null, watchdog.Token,
                                    watchdog.Start);
        }
        catch (ScopeCancelledException error) when (watchdog.Reason is "timeout" or "memory" && error.CancellationToken == watchdog.Token)
        {
            stopped = error;
        }
        catch (OperationCanceledException error) when (watchdog.Reason is "timeout" or "memory" && error.CancellationToken == watchdog.Token)
        {
            stopped = new ScopeCancelledException(ScopeStep.RootDiscovery, true, setup.Elapsed, TimeSpan.Zero,
                                                  new Dictionary<ScopeStep, TimeSpan>(), null,
                                                  new Dictionary<ScopeStep, IReadOnlyDictionary<string, int>>(), error, watchdog.Token);
        }
        finally
        {
            // End the sampling window before assembling or writing the point, including on unexpected failures.
            watchdog.Dispose();
        }

        return new JsonObject
        {
            ["bodies"] = library?.Bodies ?? 0,
            ["externBodies"] = library?.ExternBodies ?? 0,
            ["selectedMembers"] = selectedMembers,
            ["reachableBodies"] = run is not null ? run.Reachable.ReachedBodies.Count : stopped!.ReachableBodies,
            ["peakWorkingSetMb"] = watchdog.PeakWorkingSetMb,
            ["stages"] = run is not null ? Stages(run) : Stages(stopped!, watchdog.Reason)
        };
    }

    /// <summary>Reclaims a completed scope's managed and finalized state, compacting its large objects before another watchdog starts.</summary>
    private static void CollectScope()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
    }

    internal static JsonObject Stages(ScopeCancelledException stopped, string? reason)
    {
        var stages = new JsonObject();
        foreach (var step in Enum.GetValues<ScopeStep>())
        {
            var stage = new JsonObject { ["status"] = "notRun" };
            if (stopped.CompletedSteps.TryGetValue(step, out var elapsed))
            {
                stage["status"] = "finished";
                stage["seconds"] = elapsed.TotalSeconds;
            }
            else if ((stopped.Started || reason is not null) &&
                     (step == stopped.Step || step == ScopeStep.Lowering && stopped.Step == ScopeStep.ReachableSet && stopped.Started))
            {
                Assert.True(reason is "timeout" or "memory", "An unplanned cancellation has no watchdog reason.");
                stage["status"] = "cut";
                stage["reason"] = reason;
                stage["seconds"] = (step == ScopeStep.Lowering ? stopped.Lowering : stopped.Elapsed).TotalSeconds;
            }

            if (stopped.Counters.TryGetValue(step, out var counters))
                stage["counters"] = JsonSerializer.SerializeToNode(counters);
            else if (stage["status"]!.GetValue<string>() == "cut" && step is ScopeStep.SummariesAndFixpoint or ScopeStep.Executions or ScopeStep.Accesses)
            {
                // The watchdog can cancel between two stages; the named stage stopped before doing any work.
                stage["counters"] = step switch
                {
                    ScopeStep.Executions => new JsonObject { ["walkVisits"] = 0 },
                    ScopeStep.Accesses => new JsonObject { ["executions"] = 0, ["accesses"] = 0 },
                    _ => new JsonObject
                    {
                        [HeapCounters.PROPAGATE_PASSES] = 0,
                        [HeapCounters.INSTANCE_PROCESSINGS] = 0,
                        [HeapCounters.REFERENCE_LOOKUPS] = 0
                    }
                };
            }
            stages[StepName(step)] = stage;
        }

        return stages;
    }

    internal static JsonObject Stages(ScopeRun run)
    {
        var stages = new JsonObject();
        foreach (var step in Enum.GetValues<ScopeStep>())
        {
            var stage = new JsonObject { ["status"] = "finished", ["seconds"] = Elapsed(run.Times, step).TotalSeconds };
            if (run.Counters.TryGetValue(step, out var counters))
                stage["counters"] = JsonSerializer.SerializeToNode(counters);
            stages[StepName(step)] = stage;
        }

        return stages;
    }

    private static TimeSpan Elapsed(ScopeStepTimes times, ScopeStep step) => step switch
    {
        ScopeStep.RootDiscovery => times.RootDiscovery,
        ScopeStep.ProgramIndex => times.ProgramIndex,
        ScopeStep.Lowering => times.Lowering,
        ScopeStep.ReachableSet => times.ReachableSet,
        ScopeStep.SummariesAndFixpoint => times.SummariesAndFixpoint,
        ScopeStep.Executions => times.Executions,
        ScopeStep.Accesses => times.Accesses,
        _ => throw new ArgumentOutOfRangeException(nameof(step))
    };

    private static string StepName(ScopeStep step) => step switch
    {
        ScopeStep.RootDiscovery => "rootDiscovery",
        ScopeStep.ProgramIndex => "programIndex",
        ScopeStep.Lowering => "lowering",
        ScopeStep.ReachableSet => "reachableSet",
        ScopeStep.SummariesAndFixpoint => "summariesAndFixpoint",
        ScopeStep.Executions => "executions",
        ScopeStep.Accesses => "accesses",
        _ => throw new ArgumentOutOfRangeException(nameof(step))
    };

    /// <summary>Validates the library compilation while observing the scope's setup cancellation.</summary>
    /// <param name="result">The compilation and any reason it could not be used.</param>
    /// <param name="cancellationToken">Cancels validation during setup.</param>
    private static void Usable(LibraryCompilationResult result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.True(result.Reason is null, $"{result.Reason}: {string.Join(", ", result.Errors)}");
        Assert.NotNull(result.Compilation);
        // The declaration artefacts the compilation itself tolerates are no error of the selection.
        var errors = result.Compilation.GetDiagnostics(cancellationToken)
                           .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error &&
                                                !LibraryCompilation.BenignDeclarationErrors.Contains(diagnostic.Id))
                           .ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
    }

    private static ImplementationAssembly InstalledCoreLib()
    {
        var shared = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), ".."));
        var directory = Directory.GetDirectories(shared)
                                 .Select(path => (Path: path, Version: Version.TryParse(Path.GetFileName(path), out var version) ? version : null))
                                 .Where(item => item.Version is { Major: 8, Minor: 0 })
                                 .OrderByDescending(item => item.Version).First().Path;
        var path = Path.Combine(directory, "System.Private.CoreLib.dll");
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return new ImplementationAssembly("System.Private.CoreLib", path, null, Path.GetFileName(directory), "net8.0", directory, [], [],
                                          AssemblyName.GetAssemblyName(path).Version!.ToString(), FileVersionInfo.GetVersionInfo(path).FileVersion!, metadata.GetGuid(metadata.GetModuleDefinition().Mvid));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "ConcurrencyHunter.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("The concurrency-hunter repository root was not found.");
    }

    private static string Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return text;
    }
}
