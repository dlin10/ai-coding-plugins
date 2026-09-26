using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Common.Mcp;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Serialization;
using Xunit;

namespace ConcurrencyHunter.Cli.Tests;

public sealed class MetricsCommandTests
{
    private static readonly string[] STEPS =
    [
        "load", "scope-discovery", "program-index", "lowering", "reachable-set", "summaries-and-fixpoint", "executions", "accesses", "pairing",
        "solver", "findings", "render"
    ];

    private static readonly Lazy<Task<(Measurement Measurement, AnalysisResult Analysis)>> DEMO = new(MeasureDemoAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    [Fact]
    public async Task Missing_target_is_a_usage_error()
    {
        using var error = new StringWriter();

        Assert.Equal(ExitCode.UsageError, await MetricsCommand.RunAsync(["--out", "metrics.json"], error, FixtureLoad));
        Assert.Contains("--target", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_out_is_a_usage_error()
    {
        using var error = new StringWriter();

        Assert.Equal(ExitCode.UsageError, await MetricsCommand.RunAsync(["--target", "Fixture.slnx"], error, FixtureLoad));
        Assert.Contains("--out", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_limits_is_a_usage_error()
    {
        foreach (var limits in new[] { "4,x,16", "4,8", "0,8,16", "4,8,16,32", "-1,8,16" })
        {
            using var error = new StringWriter();
            Assert.Equal(ExitCode.UsageError,
                         await MetricsCommand.RunAsync(["--target", "Fixture.slnx", "--out", "metrics.json", "--limits", limits], error, FixtureLoad));
            Assert.Contains("--limits", error.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Demo_measurement_has_every_field_and_twelve_steps()
    {
        var (measurement, _) = await DEMO.Value;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(measurement, MetricsJsonContext.Default.Measurement));
        var root = json.RootElement;

        Assert.Equal(["schemaVersion", "target", "revision", "workingTreeClean", "engineVersion", "recordedAt", "machine", "limits", "timings",
                      "peakWorkingSetMb", "counts", "coverage", "targets"],
                     Names(root));
        Assert.Equal("1.2", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(BuildInfo.Version, root.GetProperty("engineVersion").GetString());
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("recordedAt").GetString(), out _));
        Assert.Equal(["logicalProcessors", "memoryGb"], Names(root.GetProperty("machine")));
        Assert.Equal(["depth", "contexts", "scc"], Names(root.GetProperty("limits")));
        Assert.Equal(["steps", "totalSeconds"], Names(root.GetProperty("timings")));
        var steps = root.GetProperty("timings").GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal(STEPS, steps.Select(step => step.GetProperty("step").GetString()));
        Assert.All(steps, step => Assert.True(step.GetProperty("seconds").GetDouble() >= 0));
        Assert.Equal(["projectsExpected", "projectsLoaded", "scopes", "roots", "rootsPerProvider", "reachableBodies", "summaries", "regions",
                      "instances", "accesses", "pairs", "findings", "occurrences", "groups", "fingerprints"],
                     Names(root.GetProperty("counts")));
        Assert.Equal(["comparisons", "cartesianBound", "largestBucket", "buckets", "skips", "suppressed", "candidates"],
                     Names(root.GetProperty("counts").GetProperty("pairs")));
        Assert.All(root.GetProperty("coverage").EnumerateArray(), scope =>
            Assert.Equal(["scopeId", "rootsPerProvider", "accesses", "counters", "ordering", "semanticGaps"], Names(scope)));
        Assert.Equal(["deterministicSeconds", "peakRssGb"], Names(root.GetProperty("targets")));
        Assert.All(root.GetProperty("targets").EnumerateObject(), target => Assert.Equal(["limit", "actual", "met"], Names(target.Value)));
        Assert.True(root.GetProperty("peakWorkingSetMb").GetDouble() > 0);
    }

    [Fact]
    public async Task Demo_measurement_counts_equal_the_analyzer_totals()
    {
        var (measurement, analysis) = await DEMO.Value;
        var counts = measurement.Counts;

        Assert.Equal((5, 5), (counts.ProjectsExpected, counts.ProjectsLoaded));
        Assert.Equal(analysis.Scopes.Count, counts.Scopes);
        Assert.Equal(analysis.Roots.Count, counts.Roots);
        Assert.Equal(analysis.Roots.GroupBy(root => root.ProviderId).ToDictionary(group => group.Key, group => group.Count()),
                     counts.RootsPerProvider.ToDictionary());
        Assert.Equal(analysis.Coverage.Sum(scope => scope.Skips[CoverageCounters.REACHABLE_BODIES]), counts.ReachableBodies);
        Assert.Equal(analysis.ScopeSizes.Sum(size => size.Summaries), counts.Summaries);
        Assert.Equal(analysis.ScopeSizes.Sum(size => size.Regions), counts.Regions);
        Assert.Equal(analysis.ScopeSizes.Sum(size => size.Instances), counts.Instances);
        Assert.Equal(analysis.Accesses.Count, counts.Accesses);
        Assert.Equal(analysis.Findings.Count, counts.Findings);
        Assert.Equal(analysis.Findings.Sum(finding => finding.OccurrenceCount), counts.Occurrences);
        Assert.Equal(analysis.Groups.Count, counts.Groups);
        Assert.Equal(analysis.Findings.Select(finding => finding.Fingerprint).Order(StringComparer.Ordinal), counts.Fingerprints);

        var pairs = counts.Pairs;
        Assert.Equal(analysis.Pairs.Comparisons, pairs.Comparisons);
        Assert.Equal(measurement.Coverage.Sum(scope => (long)scope.Accesses * (scope.Accesses + 1) / 2), pairs.CartesianBound);
        Assert.Equal(analysis.Pairs.LargestBucket, pairs.LargestBucket);
        Assert.Equal(analysis.Pairs.Buckets, pairs.Buckets);
        Assert.Equal(analysis.Pairs.Skips.ToDictionary(), pairs.Skips.ToDictionary());
        Assert.Equal(analysis.Pairs.Suppressed, pairs.Suppressed);
        Assert.Equal(analysis.Pairs.Candidates, pairs.Candidates);
        Assert.Equal(analysis.Scopes.Select(scope => scope.Id), measurement.Coverage.Select(scope => scope.ScopeId));
        Assert.All(measurement.Coverage, scope =>
        {
            Assert.Equal(analysis.Accesses.Count(access => access.Resource.Scope == scope.ScopeId && !access.IsConstructionLocal), scope.Accesses);
            Assert.Equal(analysis.Coverage.Single(item => item.ScopeId == scope.ScopeId).Skips.ToDictionary(), scope.Counters.ToDictionary());
            Assert.Equal(analysis.Coverage.Single(item => item.ScopeId == scope.ScopeId).Ordering.ToDictionary(), scope.Ordering.ToDictionary());
        });
    }

    /// <summary>The ordering counters are the demo's only gate on spawn sites, unproven joins and timer kinds, so an
    /// empty dictionary must not pass the shape checks unnoticed.</summary>
    [Fact]
    public async Task Demo_measurement_carries_the_ordering_counters_of_the_web_scope()
    {
        var (measurement, _) = await DEMO.Value;

        var web = Assert.Single(measurement.Coverage, scope => scope.ScopeId == "Demo.Web");
        Assert.Equal([OrderingCounters.SPAWN_SITES, OrderingCounters.TIMERS_DISABLED, OrderingCounters.TIMERS_ONE_SHOT,
                      OrderingCounters.TIMERS_PERIODIC, OrderingCounters.UNPROVEN_JOINS],
                     web.Ordering.Keys.Order(StringComparer.Ordinal));
        Assert.All(web.Ordering.Values, value => Assert.True(value > 0));
    }

    [Fact]
    public async Task Demo_measurement_matches_the_snapshot()
    {
        var (measurement, _) = await DEMO.Value;
        var fresh = Normalize(JsonSerializer.Serialize(new MeasurementSnapshot(measurement.Counts, measurement.Coverage),
                                                       MetricsJsonContext.Default.MeasurementSnapshot));
        var recorded = Normalize(await File.ReadAllTextAsync(RepositoryFiles.FindRepositoryFile(
            "plugins", "concurrency-hunter", "src", "ConcurrencyHunter.Cli.Tests", "Snapshots", "demo.json")));

        Assert.True(recorded == fresh, "The demo measurement differs from Snapshots/demo.json:\n" + UnifiedDiff(recorded, fresh));
    }

    [Fact]
    public async Task Limits_are_echoed()
    {
        using var directory = new TemporaryDirectory();
        var target = directory.Write("Fixture.slnx", "<Solution />");
        var output = Path.Combine(directory.Path, "out", "metrics.json");
        using var error = new StringWriter();

        Assert.Equal(ExitCode.Ok, await MetricsCommand.RunAsync(["--target", target, "--out", output, "--limits", "4,8,16"], error, FixtureLoad));

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output));
        var limits = json.RootElement.GetProperty("limits");
        Assert.Equal((4, 8, 16), (limits.GetProperty("depth").GetInt32(), limits.GetProperty("contexts").GetInt32(), limits.GetProperty("scc").GetInt32()));
        Assert.Equal(target, json.RootElement.GetProperty("target").GetString());
    }

    [Fact]
    public async Task Revision_is_unknown_outside_a_repository()
    {
        using var directory = new TemporaryDirectory();
        var target = directory.Write("Fixture.slnx", "<Solution />");

        var (measurement, _) = await MetricsCommand.MeasureAsync(target, AnalysisLimits.Default, FixtureLoad, CancellationToken.None);

        Assert.Equal(MetricsCommand.UNKNOWN_REVISION, measurement.Revision);
        Assert.False(measurement.WorkingTreeClean);
    }

    [Fact]
    public async Task Working_tree_clean_is_true_in_a_clean_temporary_repository()
    {
        using var repository = CommittedRepository();

        var (measurement, _) = await MetricsCommand.MeasureAsync(repository.Target, AnalysisLimits.Default, FixtureLoad, CancellationToken.None);

        Assert.Equal(Git(repository.Path, "rev-parse", "HEAD").Trim(), measurement.Revision);
        Assert.True(measurement.WorkingTreeClean);
        Assert.True(measurement.Counts.Findings > 0);
    }

    [Fact]
    public async Task Working_tree_clean_is_false_with_an_uncommitted_change()
    {
        using var repository = CommittedRepository();
        File.AppendAllText(Path.Combine(repository.Path, "Controller.cs"), "\n// touched\n");

        var (measurement, _) = await MetricsCommand.MeasureAsync(repository.Target, AnalysisLimits.Default, FixtureLoad, CancellationToken.None);

        Assert.Equal(Git(repository.Path, "rev-parse", "HEAD").Trim(), measurement.Revision);
        Assert.False(measurement.WorkingTreeClean);
    }

    [Fact]
    public async Task Comparisons_are_at_most_the_cartesian_bound()
    {
        var (measurement, _) = await DEMO.Value;
        var pairs = measurement.Counts.Pairs;

        Assert.True(pairs.Comparisons > 0);
        Assert.True(pairs.Comparisons <= pairs.CartesianBound);
        Assert.True(pairs.Candidates <= pairs.Comparisons);
    }

    [Fact]
    public async Task Targets_use_total_seconds_and_the_peak()
    {
        var (measurement, _) = await DEMO.Value;
        var targets = measurement.Targets;

        Assert.Equal((90d, measurement.Timings.TotalSeconds), (targets.DeterministicSeconds.Limit, targets.DeterministicSeconds.Actual));
        Assert.Equal(targets.DeterministicSeconds.Actual <= 90, targets.DeterministicSeconds.Met);
        Assert.Equal(4d, targets.PeakRssGb.Limit);
        Assert.True(Math.Abs(measurement.PeakWorkingSetMb / 1024 - targets.PeakRssGb.Actual) < 0.01);
        Assert.Equal(targets.PeakRssGb.Actual <= 4, targets.PeakRssGb.Met);
        Assert.True(measurement.Timings.TotalSeconds >= measurement.Timings.Steps.Sum(step => step.Seconds) - 0.05);
    }

    private static async Task<(Measurement, AnalysisResult)> MeasureDemoAsync()
    {
        await DemoWorkspace.EnsureRestoredAsync();
        var solution = RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "demo", "Demo.slnx");
        return await MetricsCommand.MeasureAsync(solution, AnalysisLimits.Default, MetricsCommand.LoadWithMsBuildAsync, CancellationToken.None);
    }

    /// <summary>Loads the C# files beside the target as one in-memory project, the small fixture solution of these tests.</summary>
    private static Task<LoadedTarget> FixtureLoad(string target, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(target))!;
        var files = Directory.GetFiles(directory, "*.cs").Order(StringComparer.Ordinal).Select(path => (Path.GetFileName(path), File.ReadAllText(path))).ToArray();
        var sources = files.Length == 0 ? [("Controller.cs", CONTROLLER), ("Startup.cs", STARTUP)] : files;
        return Task.FromResult(new LoadedTarget(FixtureSolution.Create(sources), 1, 1, [], new NoOwner()));
    }

    private const string CONTROLLER = """
        using Microsoft.AspNetCore.Mvc;
        public sealed class ValuesController : ControllerBase
        {
            private static int _value;
            public void Set() { _value = 1; }
        }
        """;

    private const string STARTUP = """
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Routing;
        using Microsoft.Extensions.DependencyInjection;
        public static class Startup
        {
            public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
            {
                services.AddControllers();
                app.MapControllers();
            }
        }
        """;

    /// <summary>A temporary git repository holding a committed copy of the fixture solution.</summary>
    private static TemporaryDirectory CommittedRepository()
    {
        var repository = new TemporaryDirectory();
        repository.Target = repository.Write("Fixture.slnx", "<Solution />");
        repository.Write("Controller.cs", CONTROLLER);
        repository.Write("Startup.cs", STARTUP);
        Git(repository.Path, "init", "--quiet");
        Git(repository.Path, "add", ".");
        Git(repository.Path, "-c", "user.name=metrics", "-c", "user.email=metrics@example.invalid", "-c", "commit.gpgsign=false",
            "commit", "--quiet", "-m", "fixture");
        return repository;
    }

    private static string Git(string directory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true }
        };
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(directory);
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error.Result}");
        return output;
    }

    private static string[] Names(JsonElement element) => element.EnumerateObject().Select(property => property.Name).ToArray();

    internal static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    /// <summary>A minimal unified diff of two texts: the lines from the first difference to the last, each side marked.</summary>
    internal static string UnifiedDiff(string expected, string actual)
    {
        var left = expected.Split('\n');
        var right = actual.Split('\n');
        var start = 0;
        while (start < left.Length && start < right.Length && left[start] == right[start])
            start++;
        var leftEnd = left.Length;
        var rightEnd = right.Length;
        while (leftEnd > start && rightEnd > start && left[leftEnd - 1] == right[rightEnd - 1])
        {
            leftEnd--;
            rightEnd--;
        }

        var diff = new StringBuilder();
        diff.Append("--- recorded snapshot\n+++ fresh measurement\n");
        diff.Append($"@@ -{start + 1},{leftEnd - start} +{start + 1},{rightEnd - start} @@\n");
        foreach (var line in left[start..leftEnd])
            diff.Append('-').Append(line).Append('\n');
        foreach (var line in right[start..rightEnd])
            diff.Append('+').Append(line).Append('\n');
        return diff.ToString();
    }

    private sealed class NoOwner : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ch-metrics-{Guid.NewGuid():N}");

        internal string Target { get; set; } = "";

        internal TemporaryDirectory() => Directory.CreateDirectory(Path);

        internal string Write(string name, string content)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
                return;
            foreach (var file in Directory.GetFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Path, true);
        }
    }
}
