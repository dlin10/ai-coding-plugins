using System.Text.Json;
using Common.Roslyn;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Engine;
using ConcurrencyHunter.Core.Tests.Expectations;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

public sealed class DemoExpectationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    [Fact]
    public async Task Demo_matches_every_phase_2b_expectation_on_three_runs()
    {
        var expectationPath = RepositoryFiles.FindRepositoryFile(
            "plugins", "concurrency-hunter", "demo", "expected-findings.json");
        var serializedRuns = new List<string>();
        var results = new List<AnalysisResult>();

        for (var run = 0; run < 3; run++)
        {
            var result = await AnalyzeDemoAsync();
            results.Add(result);
            serializedRuns.Add(JsonSerializer.Serialize(result.Findings, JsonOptions));
        }

        Assert.All(serializedRuns.Skip(1), serialized => Assert.Equal(serializedRuns[0], serialized));
        var finalResult = results[^1];
        var report = ExpectationMatcher.Match(
            finalResult.Findings,
            ExpectationFile.Load(expectationPath),
            "2b");
        Assert.True(report.IsExactMatch,
            $"Missing: {string.Join(", ", report.Missing)}{Environment.NewLine}" +
            $"Forbidden hits: {string.Join(", ", report.ForbiddenHits)}{Environment.NewLine}" +
            $"False positives: {string.Join(", ", report.FalsePositives)}{Environment.NewLine}" +
            $"Confidence mismatches: {string.Join(", ", report.ConfidenceMismatches)}");
    }

    [Fact]
    public async Task Demo_test_project_is_not_a_process_scope()
    {
        var result = await AnalyzeDemoAsync();

        Assert.Equal(["Demo.Web", "Demo.Worker"], result.Scopes.Select(scope => scope.Id).Order(StringComparer.Ordinal));
        Assert.All(result.Scopes, scope => Assert.DoesNotContain(scope.Projects, project => project.Contains("Demo.Tests", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Demo_counts_two_unresolved_locator_calls_and_no_unanalysed_registration_in_the_web_scope()
    {
        var result = await AnalyzeDemoAsync();

        var web = Assert.Single(result.Coverage, coverage => coverage.ScopeId == "Demo.Web");
        Assert.Equal(2, web.Skips.GetValueOrDefault(CoverageCounters.UNRESOLVED_LOCATOR));
        Assert.Equal(0, web.Skips.GetValueOrDefault(CoverageCounters.UNANALYSED_REGISTRATION));
    }

    [Fact]
    public async Task Demo_shared_helper_is_one_finding_with_six_occurrences_in_one_group()
    {
        var result = await AnalyzeDemoAsync();

        var finding = Assert.Single(result.Findings,
                                    item => item.Resource.Region == "di:Demo.Web.Cases.GroupSharedHelperManyCallers.ActivityLog@Singleton");
        Assert.Equal(6, finding.OccurrenceCount);
        Assert.Equal(3, finding.Occurrences.Count);
        Assert.Equal(finding.AccessA.BodyId, finding.AccessB.BodyId);
        var group = Assert.Single(result.Groups, item => item.GroupId == finding.GroupId);
        Assert.Equal([finding.FindingId], group.FindingIds);
        Assert.Equal(6, group.OccurrenceCount);
    }

    [Fact]
    public async Task Demo_findings_are_identical_with_and_without_the_candidate_index()
    {
        var indexed = await AnalyzeDemoAsync();
        var reference = await AnalyzeDemoAsync(EngineFixture.ReferencePair);

        Assert.NotEmpty(indexed.Findings);
        Assert.Equal(JsonSerializer.Serialize(reference.Findings, JsonOptions), JsonSerializer.Serialize(indexed.Findings, JsonOptions));
        Assert.Equal(reference.Pairs.Candidates, indexed.Pairs.Candidates);
        Assert.True(indexed.Pairs.Comparisons <= reference.Pairs.Comparisons);
    }

    private static async Task<AnalysisResult> AnalyzeDemoAsync(
        Func<IReadOnlyList<Access>, ExecutionAnalysis, HeapSolution, PairAnalysis>? pairing = null)
    {
        await DemoWorkspace.EnsureRestoredAsync();
        var solutionPath = RepositoryFiles.FindRepositoryFile(
            "plugins", "concurrency-hunter", "demo", "Demo.slnx");
        using var loaded = await new MsBuildSolutionLoader().LoadAsync(solutionPath);
        Assert.True(loaded.Coverage.LoadComplete,
            $"Missing demo projects: {string.Join(", ", loaded.Coverage.MissingProjects)}");
        return await PhaseOneAnalyzer.AnalyzeAsync(
            loaded.Solution,
            Path.GetDirectoryName(solutionPath)!,
            ProviderRegistry.BuiltIn,
            pairing ?? InterproceduralPairing.Pair,
            CancellationToken.None);
    }
}
