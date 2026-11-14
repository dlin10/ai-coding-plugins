using System.Diagnostics;
using System.Text.Json;
using Common.Roslyn;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Solving;
using Xunit;
using Xunit.Abstractions;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class HandleExecutionProbeTests(HandleExecutionProbeFixture fixture, ITestOutputHelper output) : IClassFixture<HandleExecutionProbeFixture>
{
    [Fact]
    public async Task Demo_with_polly_bodies_admitted_loses_the_joined_retry_finding_and_keeps_the_records()
    {
        var runs = await fixture.Demo.Value;
        Counts(runs);
        Assert.Empty(runs.Errors);
        Assert.Contains(runs.Admitted.Run.Findings, finding => finding.Fingerprint == "0d8df6303781e5e8");
        Assert.DoesNotContain(runs.Admitted.Run.Findings, finding => finding.Fingerprint == "beab0b89c04944ad");
        Assert.All(runs.Records.Run.Findings, finding => Assert.Contains(finding, runs.Admitted.Run.Findings));
        Assert.True(runs.Admitted.Run.LibraryBodies >= 1);
        Assert.Equal(0, runs.Records.Run.LibraryBodies);
        var records = runs.Records.Run.Findings.ToHashSet();
        var extra = runs.Admitted.Analysis.Findings.Where(finding => !records.Contains(HandleExecutionProbeFixture.Triple(finding))).ToArray();
        // #404: the later runs of a sequential-awaited record; #402's joined retry finding beab0b89c04944ad is gone (ADR 0035).
        string[] allowed = ["0d8df6303781e5e8"];
        Assert.Single(extra);
        Assert.All(extra, finding => Assert.Contains(finding.Fingerprint, allowed));
        var entries = runs.Residuals.GetProperty("findings").EnumerateArray().ToArray();
        Assert.Equal(extra.Length, entries.Length);
        foreach (var finding in extra)
        {
            var entry = Assert.Single(entries, entry => entry.GetProperty("fingerprint").GetString() == finding.Fingerprint);
            var scope = Assert.Single(runs.Admitted.Scopes, scope => scope.Run.ScopeProgram!.ScopeId == finding.Resource.Scope);
            // Rebuild from the complete candidate list, independently of the diagnostic grouping and of folded occurrences.
            var expected = scope.Pairs.Pairs.Where(pair => ConflictFindings.FindingIdentity(pair) == ConflictFindings.FindingIdentity(finding))
                                .Select(HandleExecutionProbeFixture.PairKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var listed = entry.GetProperty("occurrences").EnumerateArray().Select(pair =>
            {
                string Key(string side)
                {
                    var access = pair.GetProperty(side);
                    return $"{access.GetProperty("instance").GetString()}#{access.GetProperty("operation").GetInt32()}@{access.GetProperty("execution").GetString()}";
                }
                return string.Join("|", new[] { Key("first"), Key("second") }.Order(StringComparer.Ordinal));
            }).ToArray();
            Assert.NotEmpty(expected);
            Assert.Equal(expected, listed.Order(StringComparer.Ordinal));
            Assert.Equal(listed.Length, listed.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(listed.Length, entry.GetProperty("pairCount").GetInt32());
            Assert.Equal(finding.OccurrenceCount, entry.GetProperty("occurrenceCount").GetInt32());
        }
    }

    [RequiresEShopFact]
    public async Task EShop_with_polly_bodies_admitted_has_exactly_the_findings_of_the_records()
    {
        var runs = await fixture.EShop.Value;
        Counts(runs);
        Assert.Equal(runs.Records.Run.Findings, runs.Admitted.Run.Findings);
    }

    private void Counts(HandleExecutionProbeRuns runs)
    {
        foreach (var run in new[] { runs.Records.Run, runs.Admitted.Run })
            output.WriteLine($"findings={run.Findings.Count}, libraryBodies={run.LibraryBodies}, admitted={run.Admitted.Count}, seconds={run.Seconds:F3}");
    }
}

public sealed class HandleExecutionProbeFixture
{
    private const string ESHOP_REVISION = "7e5ae7b54ec9f331a1c60b65bd45519f5aecc327";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal Lazy<Task<HandleExecutionProbeRuns>> Demo { get; } = new(() => Run("demo"));
    internal Lazy<Task<HandleExecutionProbeRuns>> EShop { get; } = new(() => Run("eshop"));

    private static async Task<HandleExecutionProbeRuns> Run(string target)
    {
        var directory = OutputDirectory();
        var demo = target == "demo";
        if (demo)
            await DemoWorkspace.EnsureRestoredAsync();
        var solutionPath = demo ? File("demo", "Demo.slnx") : Path.Combine(
            Path.GetFullPath(Environment.GetEnvironmentVariable(RequiresEShopFactAttribute.VARIABLE)!), "src", "eShopOnContainers-ServicesAndWebApps.sln");
        string[] projects = [];
        if (!demo)
        {
            var root = Path.GetFullPath(Environment.GetEnvironmentVariable(RequiresEShopFactAttribute.VARIABLE)!);
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-C", root, "rev-parse", "HEAD" })
                start.ArgumentList.Add(argument);
            using var git = Process.Start(start)!;
            var revision = git.StandardOutput.ReadToEndAsync();
            var error = git.StandardError.ReadToEndAsync();
            await git.WaitForExitAsync();
            Assert.True(git.ExitCode == 0, await error);
            Assert.Equal(ESHOP_REVISION, (await revision).Trim());
            projects = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories);
            Assert.Equal(30, projects.Length);
            Assert.All(projects, project => Assert.True(System.IO.File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "obj/project.assets.json")),
                                                        $"Missing assets for {project}"));
        }
        using var loaded = await new MsBuildSolutionLoader().LoadAsync(solutionPath);
        Assert.True(loaded.Coverage.LoadComplete, $"Missing projects: {string.Join(", ", loaded.Coverage.MissingProjects)}");
        if (!demo)
        {
            Assert.Equal(30, loaded.Coverage.ProjectsExpected);
            Assert.Equal(30, loaded.Coverage.ProjectsLoaded);
            Assert.Equal(30, loaded.Solution.Projects.Count());
            Assert.Equal(projects.Order(StringComparer.OrdinalIgnoreCase), loaded.Solution.Projects.Select(project => project.FilePath!).Order(StringComparer.OrdinalIgnoreCase),
                         StringComparer.OrdinalIgnoreCase);
        }
        var rootDirectory = Path.GetDirectoryName(solutionPath)!;
        var limits = demo ? AnalysisLimits.Default : new AnalysisLimits(8, 16, 16);
        var generation = new GenerationSetup(demo ? ModelGeneration.ReadOnly : ModelGeneration.None);
        var records = await Analyze("records", null);
        var admitted = await Analyze("admitted", new CallSiteAnalysis("Polly", new Version(7, 0, 0, 0), new Version(8, 0, 0, 0),
            new HashSet<string>(StringComparer.Ordinal) { "polly" }, true));
        var errors = new List<string>();
        var residuals = demo ? Residuals(records, admitted, errors) : JsonSerializer.SerializeToElement(new { findings = Array.Empty<object>() }, JsonOptions);
        if (demo && directory is not null)
            await System.IO.File.WriteAllTextAsync(Path.Combine(directory, "demo-residuals.json"), residuals.GetRawText());
        return new HandleExecutionProbeRuns(records, admitted, residuals, errors);

        async Task<HandleExecutionProbeMeasurement> Analyze(string name, CallSiteAnalysis? setup)
        {
            var scopes = new List<HandleExecutionProbeScope>();
            var clock = Stopwatch.StartNew();
            var result = await PhaseOneAnalyzer.AnalyzeAsync(loaded.Solution, rootDirectory, RepositoryRoot.Find(rootDirectory), ProviderRegistry.BuiltIn,
                InterproceduralPairing.Pair, limits, Z3ConstraintSolver.Create, generation, CancellationToken.None, setup,
                (run, pairs) => scopes.Add(new HandleExecutionProbeScope(run, pairs)));
            clock.Stop();
            var run = new CallSiteProbeRun(result.Findings.Select(Triple).OrderBy(finding => finding.Fingerprint, StringComparer.Ordinal)
                .ThenBy(finding => finding.Rule, StringComparer.Ordinal).ThenBy(finding => finding.Confidence, StringComparer.Ordinal).ToArray(),
                setup?.Measurements.Sum(scope => scope.LibraryBodies) ?? 0,
                setup?.Measurements.SelectMany(scope => scope.Admitted).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() ?? [], clock.Elapsed.TotalSeconds);
            if (directory is not null)
                await System.IO.File.WriteAllTextAsync(Path.Combine(directory, $"{target}-{name}.json"), JsonSerializer.Serialize(run, JsonOptions));
            return new HandleExecutionProbeMeasurement(run, result, scopes);
        }
    }

    private static JsonElement Residuals(HandleExecutionProbeMeasurement records, HandleExecutionProbeMeasurement admitted, List<string> errors)
    {
        var known = records.Run.Findings.ToHashSet();
        var candidates = admitted.Scopes.SelectMany(scope => scope.Pairs.Pairs.Select(pair => (Pair: pair, Analysis: scope.Run.Executions!)))
                                 .GroupBy(item => ConflictFindings.FindingIdentity(item.Pair), StringComparer.Ordinal)
                                 .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var findings = new List<object>();
        foreach (var finding in admitted.Analysis.Findings.Where(finding => !known.Contains(Triple(finding))))
        {
            var pairs = candidates[ConflictFindings.FindingIdentity(finding)].DistinctBy(item => PairKey(item.Pair)).OrderBy(item => PairKey(item.Pair), StringComparer.Ordinal).ToArray();
            var occurrences = pairs.Select(item =>
            {
                var pair = item.Pair;
                object proof;
                try
                {
                    var evidence = item.Analysis.OrderProof(pair.First, pair.Second);
                    proof = new
                    {
                        timing = evidence.Timing.ToString(), branch = evidence.Branch.ToString(), evidence.Creator, evidence.Facts,
                        evidence.Frames, evidence.Steps
                    };
                }
                catch (Exception exception)
                {
                    errors.Add(exception.ToString());
                    proof = new { error = exception.ToString() };
                }
                object Access(Access access) => new { access.Symbol, operation = access.OperationId, instance = access.InstanceId, execution = access.ExecutionId };
                return new { roots = new[] { pair.First.PathRoot.RootId, pair.Second.PathRoot.RootId }, first = Access(pair.First), second = Access(pair.Second), proof };
            }).ToArray();
            findings.Add(new
            {
                finding.Fingerprint, rule = finding.RuleId, confidence = finding.Confidence.Label, resource = ConflictFindings.ResourceText(finding.Resource),
                finding.OccurrenceCount, pairCount = pairs.Length, occurrences
            });
        }
        return JsonSerializer.SerializeToElement(new { findings }, JsonOptions);
    }

    internal static CallSiteProbeFinding Triple(Finding finding) => new(finding.Fingerprint, finding.RuleId, finding.Confidence.Label);

    internal static string PairKey(AccessPair pair) => string.Join("|", new[]
    {
        $"{pair.First.InstanceId}#{pair.First.OperationId}@{pair.First.ExecutionId}",
        $"{pair.Second.InstanceId}#{pair.Second.OperationId}@{pair.Second.ExecutionId}"
    }.Order(StringComparer.Ordinal));

    private static string? OutputDirectory()
    {
        if (Environment.GetEnvironmentVariable("CH_H3B_PROBE_OUT") is not { Length: > 0 } path)
            return null;
        var directory = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var scratch = Path.GetFullPath(File("scratch", "h3b")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var baseline = Path.Combine(scratch, "baseline") + Path.DirectorySeparatorChar;
        Assert.True(directory.StartsWith(scratch, StringComparison.OrdinalIgnoreCase) && directory != scratch &&
                    !directory.StartsWith(baseline, StringComparison.OrdinalIgnoreCase) && !baseline.StartsWith(directory, StringComparison.OrdinalIgnoreCase),
                    $"The probe folder {directory} must be inside scratch/h3b and outside its baseline.");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string File(params string[] segments) => RepositoryFiles.FindRepositoryFile(["plugins", "concurrency-hunter", .. segments]);
}

internal sealed record HandleExecutionProbeScope(ScopeRun Run, PairAnalysis Pairs);
internal sealed record HandleExecutionProbeMeasurement(CallSiteProbeRun Run, AnalysisResult Analysis, IReadOnlyList<HandleExecutionProbeScope> Scopes);
internal sealed record HandleExecutionProbeRuns(HandleExecutionProbeMeasurement Records, HandleExecutionProbeMeasurement Admitted, JsonElement Residuals,
                                               IReadOnlyList<string> Errors);
