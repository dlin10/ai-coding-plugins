using System.Diagnostics;
using System.Text.Json;
using Common.Roslyn;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Expectations;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Solving;
using Xunit;
using Xunit.Abstractions;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class CallSiteProbeTests(CallSiteProbeFixture fixture, ITestOutputHelper output) : IClassFixture<CallSiteProbeFixture>
{
    [Fact(Skip = "Waits for #402 and #404: a pass in a library loop's continuation, later runs of a sequential-awaited record.")]
    public async Task Demo_with_polly_bodies_admitted_and_records_withheld_has_the_findings_of_the_records()
    {
        var runs = await fixture.Demo.Value;
        Counts(runs);
        Assert.Equal(runs.Records.Findings, runs.Admitted.Findings);
    }

    [Fact]
    public async Task Demo_with_records_withheld_and_nothing_admitted_differs_from_the_records()
    {
        var runs = await fixture.Demo.Value;
        Counts(runs);
        Assert.False(runs.Records.Findings.SequenceEqual(runs.Control.Findings));
    }

    [RequiresEShopFact]
    public async Task EShop_with_polly_bodies_admitted_and_records_withheld_has_the_findings_of_the_records()
    {
        var runs = await fixture.EShop.Value;
        Counts(runs);
        Assert.Equal(runs.Records.Findings, runs.Admitted.Findings);
    }

    [RequiresEShopFact]
    public async Task EShop_with_records_withheld_and_nothing_admitted_differs_from_the_records()
    {
        var runs = await fixture.EShop.Value;
        Counts(runs);
        Assert.False(runs.Records.Findings.SequenceEqual(runs.Control.Findings));
    }

    private void Counts(CallSiteProbeRuns runs)
    {
        foreach (var run in new[] { runs.Records, runs.Admitted, runs.Control })
            output.WriteLine($"findings={run.Findings.Count}, libraryBodies={run.LibraryBodies}, admitted={run.Admitted.Count}, seconds={run.Seconds:F3}");
    }
}

public sealed record CallSiteProbeFinding(string Fingerprint, string Rule, string Confidence);
public sealed record CallSiteProbeRun(IReadOnlyList<CallSiteProbeFinding> Findings, int LibraryBodies, IReadOnlyList<string> Admitted, double Seconds);
public sealed record CallSiteProbeRuns(CallSiteProbeRun Records, CallSiteProbeRun Admitted, CallSiteProbeRun Control);

public sealed class CallSiteProbeFixture
{
    private const string ESHOP_REVISION = "7e5ae7b54ec9f331a1c60b65bd45519f5aecc327";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public Lazy<Task<CallSiteProbeRuns>> Demo { get; } = new(() => Run("demo"));
    public Lazy<Task<CallSiteProbeRuns>> EShop { get; } = new(() => Run("eshop"));

    private static async Task<CallSiteProbeRuns> Run(string target)
    {
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
        using (var snapshot = JsonDocument.Parse(System.IO.File.ReadAllText(File("src", "ConcurrencyHunter.Cli.Tests", "Snapshots", target + ".json"))))
            Assert.Equal(snapshot.RootElement.GetProperty("counts").GetProperty("fingerprints").EnumerateArray().Select(value => value.GetString()!).Order(StringComparer.Ordinal),
                         records.Run.Findings.Select(finding => finding.Fingerprint).Order(StringComparer.Ordinal));
        if (demo)
        {
            var report = ExpectationMatcher.Match(records.Analysis.Findings, ExpectationFile.Load(File("demo", "expected-findings.json")), "5d-B4c");
            Assert.True(report.IsExactMatch, JsonSerializer.Serialize(report, JsonOptions));
        }
        else
            Assert.Equal(new[] { "7c919c7aedc4b38e", "fa3fbc528c46593e" }, records.Run.Findings.Select(finding => finding.Fingerprint));
        var admitted = await Analyze("admitted", Setup(true));
        var control = await Analyze("control", Setup(false));
        Assert.True(admitted.Run.LibraryBodies >= 1);
        Assert.NotEmpty(admitted.Run.Admitted);
        Assert.Equal(0, records.Run.LibraryBodies);
        Assert.Empty(records.Run.Admitted);
        Assert.Equal(0, control.Run.LibraryBodies);
        Assert.Empty(control.Run.Admitted);
        return new CallSiteProbeRuns(records.Run, admitted.Run, control.Run);

        async Task<(CallSiteProbeRun Run, AnalysisResult Analysis)> Analyze(string name, CallSiteAnalysis? setup)
        {
            var clock = Stopwatch.StartNew();
            var result = await PhaseOneAnalyzer.AnalyzeAsync(loaded.Solution, rootDirectory, RepositoryRoot.Find(rootDirectory), ProviderRegistry.BuiltIn,
                InterproceduralPairing.Pair, limits, Z3ConstraintSolver.Create, generation, CancellationToken.None, setup);
            clock.Stop();
            var run = new CallSiteProbeRun(result.Findings.Select(finding => new CallSiteProbeFinding(finding.Fingerprint, finding.RuleId, finding.Confidence.Label))
                                                .OrderBy(finding => finding.Fingerprint, StringComparer.Ordinal).ThenBy(finding => finding.Rule, StringComparer.Ordinal)
                                                .ThenBy(finding => finding.Confidence, StringComparer.Ordinal).ToArray(),
                setup?.Measurements.Sum(scope => scope.LibraryBodies) ?? 0,
                setup?.Measurements.SelectMany(scope => scope.Admitted).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() ?? [], clock.Elapsed.TotalSeconds);
            if (Environment.GetEnvironmentVariable("CH_H3_PROBE_OUT") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                await System.IO.File.WriteAllTextAsync(Path.Combine(directory, $"{target}-{name}.json"), JsonSerializer.Serialize(run, JsonOptions));
            }
            return (run, result);
        }
    }

    private static CallSiteAnalysis Setup(bool admit) => new("Polly", new Version(7, 0, 0, 0), new Version(8, 0, 0, 0),
        new HashSet<string>(StringComparer.Ordinal) { "polly" }, admit);

    private static string File(params string[] segments) => RepositoryFiles.FindRepositoryFile(["plugins", "concurrency-hunter", .. segments]);
}
