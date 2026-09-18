using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Common.Mcp;
using Common.Roslyn;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Reporting;
using ConcurrencyHunter.Serialization;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter;

/// <summary>A loaded target: its solution, what loading covered, and what to dispose when the measurement is done.</summary>
internal sealed record LoadedTarget(Solution Solution, int ProjectsExpected, int ProjectsLoaded, IReadOnlyList<string> MissingProjects,
                                    IDisposable Owner);

/// <summary>
/// <c>metrics</c> (R7): loads one target, runs the deterministic analysis once, renders the bundle in memory as a run without
/// narratives would, and writes one JSON measurement. Modelled on cache-detective's command, without its sample, compare and
/// score modes.
/// </summary>
internal static class MetricsCommand
{
    private const string USAGE = "Usage: concurrency-hunter metrics --target <path> --out <file> [--limits depth,contexts,scc]";

    /// <summary>What a revision reads as when git could not produce one.</summary>
    internal const string UNKNOWN_REVISION = "unknown";

    private const string LOAD = "load";
    private const string RENDER = "render";
    // 1.1 added the ordering counters to each coverage entry.
    private const string SCHEMA_VERSION = "1.1";
    private const double DETERMINISTIC_SECONDS_LIMIT = 90;
    private const double PEAK_RSS_GB_LIMIT = 4;
    private const double BYTES_PER_MB = 1024d * 1024d;
    private const double BYTES_PER_GB = 1024d * 1024d * 1024d;

    /// <summary>How a target is loaded. A parameter rather than shared state, so tests can measure an in-memory solution.</summary>
    internal delegate Task<LoadedTarget> TargetLoad(string path, CancellationToken cancellationToken);

    internal static async Task<int> RunAsync(string[] args, TextWriter error, TargetLoad? load = null)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException exception)
        {
            error.WriteLine(exception.Message);
            error.WriteLine(USAGE);
            return ExitCode.UsageError;
        }

        try
        {
            var (measurement, _) = await MeasureAsync(options.Target, options.Limits, load ?? LoadWithMsBuildAsync, CancellationToken.None)
                                       .ConfigureAwait(false);
            var path = Path.GetFullPath(options.Out);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(measurement, MetricsJsonContext.Default.Measurement) + Environment.NewLine)
                      .ConfigureAwait(false);
            return ExitCode.Ok;
        }
        catch (Exception exception)
        {
            error.WriteLine(exception.Message);
            return ExitCode.Failure;
        }
    }

    /// <summary>One measurement of a target, with the analysis it was taken from.</summary>
    internal static async Task<(Measurement Measurement, AnalysisResult Analysis)> MeasureAsync(string target, AnalysisLimits limits, TargetLoad load,
                                                                                                 CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        var step = Stopwatch.StartNew();
        var loaded = await load(target, cancellationToken).ConfigureAwait(false);
        using var owner = loaded.Owner;
        var loadSeconds = step.Elapsed.TotalSeconds;
        var rootDirectory = Path.GetDirectoryName(Path.GetFullPath(target))!;
        var startedAt = DateTimeOffset.UtcNow;
        step.Restart();
        var analysis = await PhaseOneAnalyzer.AnalyzeAsync(loaded.Solution, rootDirectory, limits, cancellationToken).ConfigureAwait(false);
        var analysisSeconds = step.Elapsed.TotalSeconds;

        step.Restart();
        var decision = RunStatusRules.Decide(new StatusInputs(false, false, loaded.MissingProjects.Count == 0, false, analysis,
                                                              new HashSet<string>(StringComparer.Ordinal)));
        var finishedAt = DateTimeOffset.UtcNow;
        var report = new RunReport("metrics", target, BuildInfo.Version, startedAt, finishedAt, finishedAt, decision, loaded.ProjectsExpected,
                                   loaded.ProjectsLoaded, loaded.MissingProjects, analysis, new Dictionary<string, string>(StringComparer.Ordinal),
                                   null, [], [], [], loadSeconds, analysisSeconds);
        _ = ReportRenderer.Render(report);
        var renderSeconds = step.Elapsed.TotalSeconds;
        total.Stop();

        var steps = new List<StepSeconds> { new(LOAD, Round(loadSeconds)) };
        steps.AddRange(analysis.Timings.Select(timing => new StepSeconds(timing.Step, Round(timing.Seconds))));
        steps.Add(new StepSeconds(RENDER, Round(renderSeconds)));
        var totalSeconds = Round(total.Elapsed.TotalSeconds);
        var peak = Process.GetCurrentProcess().PeakWorkingSet64;
        var measurement = new Measurement(
            SCHEMA_VERSION,
            target,
            Git(WorkingDirectory(target), "rev-parse", "HEAD") is (0, var head) && !string.IsNullOrWhiteSpace(head) ? head.Trim() : UNKNOWN_REVISION,
            Git(WorkingDirectory(target), "status", "--porcelain") is (0, var status) && string.IsNullOrWhiteSpace(status),
            BuildInfo.Version,
            DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            new MachineInfo(Environment.ProcessorCount, Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / BYTES_PER_GB, 1)),
            new LimitsInfo(limits.MaxAccessPathDepth, limits.MaxContextsPerMethod, limits.MaxSccIterations),
            new TimingsInfo(steps, totalSeconds),
            Math.Round(peak / BYTES_PER_MB, 1),
            Counts(loaded, analysis),
            Coverage(analysis),
            new TargetsInfo(new TargetValue(DETERMINISTIC_SECONDS_LIMIT, totalSeconds, totalSeconds <= DETERMINISTIC_SECONDS_LIMIT),
                            new TargetValue(PEAK_RSS_GB_LIMIT, Math.Round(peak / BYTES_PER_GB, 3), peak / BYTES_PER_GB <= PEAK_RSS_GB_LIMIT)));
        return (measurement, analysis);
    }

    /// <summary>The counts of R7: sums over the target's process scopes, the largest bucket a maximum.</summary>
    internal static MeasurementCounts Counts(LoadedTarget loaded, AnalysisResult analysis)
    {
        var pairs = analysis.Pairs;
        return new MeasurementCounts(
            loaded.ProjectsExpected,
            loaded.ProjectsLoaded,
            analysis.Scopes.Count,
            analysis.Roots.Count,
            Sum(analysis.Coverage.Select(scope => scope.RootsPerProvider)),
            analysis.Coverage.Sum(scope => scope.Skips.GetValueOrDefault(Accesses.CoverageCounters.REACHABLE_BODIES)),
            analysis.ScopeSizes.Sum(size => size.Summaries),
            analysis.ScopeSizes.Sum(size => size.Regions),
            analysis.ScopeSizes.Sum(size => size.Instances),
            analysis.Accesses.Count,
            new PairCountsInfo(pairs.Comparisons,
                               pairs.CartesianBound,
                               pairs.LargestBucket,
                               pairs.Buckets,
                               Sorted(pairs.Skips),
                               pairs.Suppressed,
                               pairs.Candidates),
            analysis.Findings.Count,
            analysis.Findings.Sum(finding => finding.OccurrenceCount),
            analysis.Groups.Count,
            analysis.Findings.Select(finding => finding.Fingerprint).Order(StringComparer.Ordinal).ToArray());
    }

    internal static IReadOnlyList<ScopeMeasurement> Coverage(AnalysisResult analysis) =>
        analysis.Coverage.Select(scope => new ScopeMeasurement(scope.ScopeId, Sorted(scope.RootsPerProvider),
                                                               analysis.ScopeSizes.FirstOrDefault(size => size.ScopeId == scope.ScopeId)?.Accesses ?? 0,
                                                               Sorted(scope.Skips), Sorted(scope.Ordering)))
                .ToArray();

    internal static async Task<LoadedTarget> LoadWithMsBuildAsync(string path, CancellationToken cancellationToken)
    {
        var loaded = await new MsBuildSolutionLoader().LoadAsync(path, cancellationToken).ConfigureAwait(false);
        return new LoadedTarget(loaded.Solution, loaded.Coverage.ProjectsExpected, loaded.Coverage.ProjectsLoaded, loaded.Coverage.MissingProjects,
                                loaded);
    }

    private static SortedDictionary<string, int> Sorted(IReadOnlyDictionary<string, int> values) => new(values.ToDictionary(), StringComparer.Ordinal);

    private static SortedDictionary<string, int> Sum(IEnumerable<IReadOnlyDictionary<string, int>> values)
    {
        var sum = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, value) in values.SelectMany(item => item))
            sum[key] = sum.GetValueOrDefault(key) + value;
        return sum;
    }

    private static double Round(double seconds) => Math.Round(seconds, 3);

    /// <summary>The directory git runs in: the target itself when it is a directory, else the directory holding it.</summary>
    private static string WorkingDirectory(string target)
    {
        var full = Path.GetFullPath(target);
        return Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
    }

    private static (int ExitCode, string Output) Git(string directory, params string[] arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add($"safe.directory={directory.Replace('\\', '/')}");
            process.StartInfo.ArgumentList.Add("-C");
            process.StartInfo.ArgumentList.Add(directory);
            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            _ = error.GetAwaiter().GetResult();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, "");
        }
    }

    private sealed record Options(string Target, string Out, AnalysisLimits Limits)
    {
        internal static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index] is not ("--target" or "--out" or "--limits") || index + 1 >= args.Length || !values.TryAdd(args[index], args[++index]))
                    throw new ArgumentException($"Unexpected argument '{args[index]}'.");
            }

            if (!values.TryGetValue("--target", out var target) || string.IsNullOrWhiteSpace(target))
                throw new ArgumentException("--target is required.");
            if (!values.TryGetValue("--out", out var output) || string.IsNullOrWhiteSpace(output))
                throw new ArgumentException("--out is required.");
            return new Options(target, output, values.TryGetValue("--limits", out var limits) ? ParseLimits(limits) : AnalysisLimits.Default);
        }

        private static AnalysisLimits ParseLimits(string text)
        {
            var parts = text.Split(',');
            var values = parts.Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0).ToArray();
            if (parts.Length != 3 || values.Any(value => value <= 0))
                throw new ArgumentException("--limits must be three positive integers: depth,contexts,scc.");
            return new AnalysisLimits(values[0], values[1], values[2]);
        }
    }
}

internal sealed record Measurement(string SchemaVersion, string Target, string Revision, bool WorkingTreeClean, string EngineVersion,
                                   string RecordedAt, MachineInfo Machine, LimitsInfo Limits, TimingsInfo Timings, double PeakWorkingSetMb,
                                   MeasurementCounts Counts, IReadOnlyList<ScopeMeasurement> Coverage, TargetsInfo Targets);

internal sealed record MachineInfo(int LogicalProcessors, double MemoryGb);

internal sealed record LimitsInfo(int Depth, int Contexts, int Scc);

internal sealed record StepSeconds(string Step, double Seconds);

internal sealed record TimingsInfo(IReadOnlyList<StepSeconds> Steps, double TotalSeconds);

internal sealed record PairCountsInfo(int Comparisons, long CartesianBound, int LargestBucket, int Buckets, IReadOnlyDictionary<string, int> Skips,
                                      int Suppressed, int Candidates);

internal sealed record MeasurementCounts(int ProjectsExpected, int ProjectsLoaded, int Scopes, int Roots, IReadOnlyDictionary<string, int> RootsPerProvider,
                                         int ReachableBodies, int Summaries, int Regions, int Instances, int Accesses, PairCountsInfo Pairs,
                                         int Findings, int Occurrences, int Groups, IReadOnlyList<string> Fingerprints);

internal sealed record ScopeMeasurement(string ScopeId, IReadOnlyDictionary<string, int> RootsPerProvider, int Accesses,
                                        IReadOnlyDictionary<string, int> Counters, IReadOnlyDictionary<string, int> Ordering);

internal sealed record TargetValue(double Limit, double Actual, bool Met);

internal sealed record TargetsInfo(TargetValue DeterministicSeconds, TargetValue PeakRssGb);

/// <summary>The deterministic part of a measurement, as the demo snapshot records it.</summary>
internal sealed record MeasurementSnapshot(MeasurementCounts Counts, IReadOnlyList<ScopeMeasurement> Coverage);
