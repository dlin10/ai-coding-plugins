using System.Text.Json;
using Common.Roslyn;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Expectations;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

public sealed class DemoExpectationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    [Fact]
    public async Task Demo_matches_every_phase_2_expectation_on_three_runs()
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
            "2");
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

    private static async Task<AnalysisResult> AnalyzeDemoAsync()
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
            CancellationToken.None);
    }
}
