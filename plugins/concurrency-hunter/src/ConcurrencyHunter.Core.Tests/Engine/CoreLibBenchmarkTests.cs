using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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

public sealed class CoreLibBenchmarkTests
{
    [RequiresEngineBenchmarkFact]
    public void CoreLib_benchmark_records_a_point()
    {
        var label = Environment.GetEnvironmentVariable("CH_ENGINE_BENCH_LABEL");
        Assert.False(string.IsNullOrWhiteSpace(label), "CH_ENGINE_BENCH_LABEL must name the point.");
        var root = RepositoryRoot();
        var assembly = InstalledCoreLib();
        var baseline = LibraryCompilation.Compile(assembly, CancellationToken.None);
        Usable(baseline);
        var scopes = new JsonObject();
        foreach (var scope in new[] { "four", "all" })
        {
            var selected = BenchmarkScopes.Select(baseline.Compilation!, scope);
            using var process = Process.GetCurrentProcess();
            using var watchdog = new BenchmarkWatchdog(() => DateTimeOffset.UtcNow, BenchmarkWatchdog.AvailableCommit, () =>
            {
                process.Refresh();
                return process.WorkingSet64;
            });
            var library = LibraryCompilation.Compile(assembly, selected, watchdog.Token);
            Usable(library);
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
                LibraryModels.BuiltIn.Members.Where(model => model.Assemblies.All(range => range.AssemblyName != assembly.AssemblyName)),
                LibraryModels.BuiltIn.ImmutableTypes.Where(type => type.Assemblies.All(range => range.AssemblyName != assembly.AssemblyName)));
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
            ScopeCancelledException stopped;
            try
            {
                ScopePipeline.Run("scope:corelib-benchmark", [compilation, driver], [], Path.GetTempPath(),
                                  new ProviderRegistry([new DriverRootProvider()]), models, AnalysisLimits.Default, new(), null, stop.Token,
                                  step =>
                                  {
                                      watchdog.Start(step);
                                      if (step == ScopeStep.Accesses)
                                          stop.Cancel();
                                  });
                throw new InvalidOperationException("The benchmark ran beyond executions.");
            }
            catch (ScopeCancelledException error)
            {
                stopped = error;
            }

            // End the sampling window before assembling or writing the point.
            watchdog.Dispose();
            scopes[scope] = new JsonObject
            {
                ["bodies"] = library.Bodies,
                ["externBodies"] = library.ExternBodies,
                ["selectedMembers"] = selected.Count,
                ["reachableBodies"] = stopped.ReachableBodies,
                ["peakWorkingSetMb"] = watchdog.PeakWorkingSetMb,
                ["stages"] = Stages(stopped, watchdog.Reason)
            };
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
            else if (step != ScopeStep.Accesses && (stopped.Started || reason is not null) &&
                     (step == stopped.Step || step == ScopeStep.Lowering && stopped.Step == ScopeStep.ReachableSet && stopped.Started))
            {
                Assert.True(reason is "timeout" or "memory", "An unplanned cancellation has no watchdog reason.");
                stage["status"] = "cut";
                stage["reason"] = reason;
                stage["seconds"] = (step == ScopeStep.Lowering ? stopped.Lowering : stopped.Elapsed).TotalSeconds;
            }

            if (stopped.Counters.TryGetValue(step, out var counters))
                stage["counters"] = JsonSerializer.SerializeToNode(counters);
            else if (stage["status"]!.GetValue<string>() == "cut" && step is ScopeStep.SummariesAndFixpoint or ScopeStep.Executions)
            {
                // The watchdog can cancel between two stages; the named stage stopped before doing any work.
                stage["counters"] = step == ScopeStep.Executions
                    ? new JsonObject { ["walkVisits"] = 0 }
                    : new JsonObject
                    {
                        [HeapCounters.PROPAGATE_PASSES] = 0,
                        [HeapCounters.INSTANCE_PROCESSINGS] = 0,
                        [HeapCounters.REFERENCE_LOOKUPS] = 0
                    };
            }
            stages[StepName(step)] = stage;
        }

        return stages;
    }

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

    private static void Usable(LibraryCompilationResult result)
    {
        Assert.True(result.Reason is null, $"{result.Reason}: {string.Join(", ", result.Errors)}");
        Assert.NotNull(result.Compilation);
        // The declaration artefacts the compilation itself tolerates are no error of the selection.
        var errors = result.Compilation.GetDiagnostics()
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
