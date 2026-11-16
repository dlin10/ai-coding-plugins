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

public sealed class CallSitePackageProbeTests(CallSitePackageProbeFixture fixture, ITestOutputHelper output) : IClassFixture<CallSitePackageProbeFixture>
{
    [Fact]
    public async Task Demo_without_a_call_site_input_is_the_committed_normal_run()
    {
        var run = await fixture.Records.Value;
        Counts(run);
        using var snapshot = JsonDocument.Parse(File.ReadAllText(RepositoryFile("src", "ConcurrencyHunter.Cli.Tests", "Snapshots", "demo.json")));
        Assert.Equal(snapshot.RootElement.GetProperty("counts").GetProperty("fingerprints").EnumerateArray().Select(value => value.GetString()!).Order(StringComparer.Ordinal),
                     run.Analysis.Findings.Select(finding => finding.Fingerprint).Order(StringComparer.Ordinal));
        var report = ExpectationMatcher.Match(run.Analysis.Findings, ExpectationFile.Load(RepositoryFile("demo", "expected-findings.json")), "5d-B4c");
        Assert.True(report.IsExactMatch, JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task Demo_with_polly_core_bodies_admitted_reaches_library_bodies()
    {
        var run = await fixture.Admitted.Value;
        Counts(run);
        Assert.True(run.LibraryBodies >= 1, $"libraryBodies={run.LibraryBodies}");
    }

    [Fact]
    public async Task Pipeline_case_finding_is_unchanged_with_polly_core_bodies_admitted()
    {
        var run = await fixture.Admitted.Value;
        Counts(run);
        var records = await fixture.Records.Value;
        fixture.AssertPipelineFindingIsUnchanged(records, run);
    }

    [Fact]
    public async Task Demo_with_polly_core_bodies_admitted_has_no_finding_in_polly_core_code()
    {
        var run = await fixture.Admitted.Value;
        Counts(run);
        fixture.AssertNoLibraryAccess(run);
        var records = await fixture.Records.Value;
        var admitted = run.Analysis.Findings.Select(finding => (finding.Fingerprint, finding.RuleId, finding.Confidence.Label)).ToHashSet();
        var lost = records.Analysis.Findings.Where(finding => !admitted.Contains((finding.Fingerprint, finding.RuleId, finding.Confidence.Label))).ToArray();
        Assert.True(lost.Length == 0, $"Normal triples missing from admitted run:{Environment.NewLine}{string.Join(Environment.NewLine, lost.Select(CallSitePackageProbeFixture.Describe))}");
    }

    private void Counts(CallSitePackageProbeRun run) =>
        output.WriteLine($"findings={run.Analysis.Findings.Count}, libraryBodies={run.LibraryBodies}, seconds={run.Seconds:F3}");

    private static string RepositoryFile(params string[] segments) => RepositoryFiles.FindRepositoryFile(["plugins", "concurrency-hunter", .. segments]);
}

public sealed record CallSitePackageProbeRun(AnalysisResult Analysis, int LibraryBodies, double Seconds);

public sealed class CallSitePackageProbeFixture : IDisposable
{
    private const string PIPELINE_NAMESPACE = "Demo.Web.Cases.PollyExecuteInvokesDelegate.";
    private const string PIPELINE_MEMBER = PIPELINE_NAMESPACE + "PipelineController.Post()";
    private const string PIPELINE_REGION = "di:" + PIPELINE_NAMESPACE + "State@Singleton";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly Lazy<Task<MsBuildLoadResult>> _loaded = new(Load);

    public CallSitePackageProbeFixture()
    {
        Records = new(() => Analyze("records", null));
        Admitted = new(() => Analyze("admitted", new CallSiteAnalysis("Polly.Core", new Version(8, 0, 0, 0), new Version(9, 0, 0, 0),
            new HashSet<string>(StringComparer.Ordinal), true)));
    }

    public Lazy<Task<CallSitePackageProbeRun>> Records { get; }
    public Lazy<Task<CallSitePackageProbeRun>> Admitted { get; }

    public void AssertPipelineFindingIsUnchanged(CallSitePackageProbeRun records, CallSitePackageProbeRun run)
    {
        var findings = run.Analysis.Findings.Where(IsPipelineFinding).ToArray();
        var description = string.Join(Environment.NewLine, run.Analysis.Findings.Where(IsPipelineCase).Select(Describe));
        var expected = Assert.Single(records.Analysis.Findings, IsPipelineFinding);
        Assert.Equal(("687f86f10b06fac5", "DCA1002", "Medium"), (expected.Fingerprint, expected.RuleId, expected.Confidence.Label));
        Assert.True(findings.Length == 1 && (findings[0].Fingerprint, findings[0].RuleId, findings[0].Confidence.Label) ==
                                           (expected.Fingerprint, expected.RuleId, expected.Confidence.Label),
                    $"Expected exactly one unchanged pipeline finding; found {findings.Length}. Pipeline case findings:{Environment.NewLine}{(description.Length == 0 ? "none" : description)}");
    }

    public void AssertNoLibraryAccess(CallSitePackageProbeRun run)
    {
        var offending = run.Analysis.Findings.Where(HasLibraryAccess).ToArray();
        Assert.True(offending.Length == 0, $"Findings with a Polly.Core access:{Environment.NewLine}{string.Join(Environment.NewLine, offending.Select(Describe))}");
    }

    public void Dispose()
    {
        if (_loaded.IsValueCreated && _loaded.Value.IsCompletedSuccessfully)
            _loaded.Value.Result.Dispose();
    }

    private static async Task<MsBuildLoadResult> Load()
    {
        await DemoWorkspace.EnsureRestoredAsync();
        return await new MsBuildSolutionLoader().LoadAsync(RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "demo", "Demo.slnx"));
    }

    private async Task<CallSitePackageProbeRun> Analyze(string name, CallSiteAnalysis? setup)
    {
        setup ??= new CallSiteAnalysis(string.Empty, new Version(), new Version(), new HashSet<string>()) { Mode = CallSiteMode.Withhold };
        var unified = Environment.GetEnvironmentVariable("CH_H3_CALLSITE_OUT");
        var directory = unified ?? Environment.GetEnvironmentVariable("CH_H3_PACKAGE_PROBE_OUT");
        var jsonPath = string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, unified is null ? $"demo-{name}.json" : $"package-demo-{name}.json");
        try
        {
            var loaded = await _loaded.Value;
            Assert.True(loaded.Coverage.LoadComplete, $"Missing projects: {string.Join(", ", loaded.Coverage.MissingProjects)}");
            var rootDirectory = Path.GetDirectoryName(RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "demo", "Demo.slnx"))!;
            var clock = Stopwatch.StartNew();
            var analysis = await PhaseOneAnalyzer.AnalyzeAsync(loaded.Solution, rootDirectory, RepositoryRoot.Find(rootDirectory), ProviderRegistry.BuiltIn,
                InterproceduralPairing.Pair, AnalysisLimits.Default, Z3ConstraintSolver.Create, new GenerationSetup(ModelGeneration.ReadOnly), CancellationToken.None, setup);
            clock.Stop();
            var run = new CallSitePackageProbeRun(analysis, setup.Measurements.Sum(scope => scope.LibraryBodies), clock.Elapsed.TotalSeconds);
            if (jsonPath is not null)
            {
                Directory.CreateDirectory(directory!);
                var record = new
                {
                    Findings = analysis.Findings.OrderBy(finding => finding.Fingerprint, StringComparer.Ordinal).Select(finding => new
                    {
                        finding.Fingerprint,
                        Rule = finding.RuleId,
                        Confidence = finding.Confidence.Label,
                        Resource = Resource(finding.Resource),
                        PipelineCase = IsPipelineCase(finding),
                        LibraryAccess = HasLibraryAccess(finding),
                        PipelineFinding = IsPipelineFinding(finding),
                        Accesses = new[] { finding.AccessA, finding.AccessB }.Select(access => new
                        {
                            access.Symbol,
                            access.BodyId,
                            File = access.Source.Path,
                            Line = access.Source.StartLine,
                            Root = access.Root.Symbol,
                            Operation = access.Operation.ToWireName(),
                            access.CallPath
                        }).ToArray()
                    }).ToArray(),
                    run.LibraryBodies,
                    run.Seconds,
                    Scopes = setup.Measurements
                };
                await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(record, JsonOptions));
            }
            return run;
        }
        catch (Exception exception)
        {
            if (jsonPath is not null)
            {
                Directory.CreateDirectory(directory!);
                File.Delete(jsonPath);
                await File.WriteAllTextAsync(Path.Combine(directory!, $"demo-{name}.error.txt"), exception.ToString());
            }
            throw;
        }
    }

    private static bool IsPipelineFinding(Finding finding) =>
        finding.RuleId == "DCA1002" && finding.AccessA.Symbol == PIPELINE_MEMBER && finding.AccessB.Symbol == PIPELINE_MEMBER &&
        finding.Resource.Region == PIPELINE_REGION && finding.Resource.AccessPath.SequenceEqual(new[] { "Hits" }, StringComparer.Ordinal);

    private static bool HasLibraryAccess(Finding finding) =>
        finding.AccessA.BodyId.StartsWith("body:Polly.Core:", StringComparison.Ordinal) ||
        finding.AccessB.BodyId.StartsWith("body:Polly.Core:", StringComparison.Ordinal);

    private static bool IsPipelineCase(Finding finding) =>
        finding.AccessA.CallPath.Any(member => member.StartsWith(PIPELINE_NAMESPACE, StringComparison.Ordinal)) ||
        finding.AccessB.CallPath.Any(member => member.StartsWith(PIPELINE_NAMESPACE, StringComparison.Ordinal));

    private static string Resource(AccessResource resource) => $"{resource.Region} / {string.Join(".", resource.AccessPath)}";

    internal static string Describe(Finding finding) =>
        $"{finding.Fingerprint} {finding.RuleId} {finding.Confidence.Label}: {Resource(finding.Resource)}; {finding.AccessA.Symbol}; {finding.AccessB.Symbol}";
}
