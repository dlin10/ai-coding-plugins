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
    public async Task Demo_matches_every_phase_1b_expectation_on_three_runs()
    {
        await DemoWorkspace.EnsureRestoredAsync();
        var solutionPath = RepositoryFiles.FindRepositoryFile(
            "plugins", "concurrency-hunter", "demo", "Demo.slnx");
        var demoDirectory = Path.GetDirectoryName(solutionPath)!;
        var expectationPath = RepositoryFiles.FindRepositoryFile(
            "plugins", "concurrency-hunter", "demo", "expected-findings.json");
        var serializedRuns = new List<string>();
        var results = new List<AnalysisResult>();

        for (var run = 0; run < 3; run++)
        {
            using var loaded = await new MsBuildSolutionLoader().LoadAsync(solutionPath);
            Assert.True(loaded.Coverage.LoadComplete,
                $"Missing demo projects: {string.Join(", ", loaded.Coverage.MissingProjects)}");
            var result = await PhaseOneAnalyzer.AnalyzeAsync(
                loaded.Solution,
                demoDirectory,
                CancellationToken.None);
            results.Add(result);
            serializedRuns.Add(JsonSerializer.Serialize(result.Findings, JsonOptions));
        }

        Assert.All(serializedRuns.Skip(1), serialized => Assert.Equal(serializedRuns[0], serialized));
        var finalResult = results[^1];
        var report = ExpectationMatcher.Match(
            finalResult.Findings,
            ExpectationFile.Load(expectationPath),
            "1b");
        Assert.True(report.IsExactMatch,
            $"Missing: {string.Join(", ", report.Missing)}{Environment.NewLine}" +
            $"Forbidden hits: {string.Join(", ", report.ForbiddenHits)}{Environment.NewLine}" +
            $"False positives: {string.Join(", ", report.FalsePositives)}");
    }
}
