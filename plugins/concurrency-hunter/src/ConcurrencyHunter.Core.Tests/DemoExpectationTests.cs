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
using Xunit.Abstractions;

namespace ConcurrencyHunter.Core.Tests;

public sealed class DemoExpectationTests(ITestOutputHelper output)
{
    private const string CURRENT_POINT = "5d-B4c";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
    /// <summary>One analysis of the demo, shared by the tests that read it without running it again.</summary>
    internal static readonly Lazy<Task<AnalysisResult>> SharedDemo = new(() => AnalyzeDemoAsync());

    [Fact]
    public async Task Demo_matches_every_phase_5d_expectation_on_three_runs()
    {
        var expectationPath = RepositoryFiles.FindRepositoryFile(
            "plugins", "concurrency-hunter", "demo", "expected-findings.json");
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => AnalyzeDemoAsync()));
        var serializedRuns = results.Select(result => JsonSerializer.Serialize(result.Findings, JsonOptions)).ToArray();

        var scratchRoot = Path.Combine(Path.GetDirectoryName(expectationPath)!, "..", "scratch", "divergent-demo-runs");
        var savedTo = SaveRunsIfTheyDiffer(serializedRuns, scratchRoot);
        Assert.True(savedTo is null, $"The three demo runs gave different findings; every run's findings are saved in {savedTo}");
        var finalResult = results[^1];
        var report = ExpectationMatcher.Match(
            finalResult.Findings,
            ExpectationFile.Load(expectationPath),
            CURRENT_POINT);
        Assert.True(report.IsExactMatch,
            $"Missing: {string.Join(", ", report.Missing)}{Environment.NewLine}" +
            $"Forbidden hits: {string.Join(", ", report.ForbiddenHits)}{Environment.NewLine}" +
            $"False positives: {string.Join(", ", report.FalsePositives)}{Environment.NewLine}" +
            $"Confidence mismatches: {string.Join(", ", report.ConfidenceMismatches)}");
    }

    [Fact]
    public void Divergent_runs_are_saved_one_file_each_and_equal_runs_write_nothing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"divergent-runs-{Guid.NewGuid():N}");
        try
        {
            Assert.Null(SaveRunsIfTheyDiffer(["[1]", "[1]", "[1]"], root));
            Assert.False(Directory.Exists(root));

            var folder = SaveRunsIfTheyDiffer(["[1]", "[2]", "[1]"], root);

            Assert.NotNull(folder);
            Assert.StartsWith(Path.GetFullPath(root), folder);
            Assert.Equal("[1]", File.ReadAllText(Path.Combine(folder, "run-0.json")));
            Assert.Equal("[2]", File.ReadAllText(Path.Combine(folder, "run-1.json")));
            Assert.Equal("[1]", File.ReadAllText(Path.Combine(folder, "run-2.json")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    /// <summary>A solver query is limited by Z3's work, not by time (ADR 0021): the demo analysed while a busy thread loads every
    /// core reports what an idle analysis reports, findings and every scope's coverage counters alike, and the safeguard stopped
    /// no query in either.</summary>
    [Fact]
    public async Task Demo_findings_do_not_depend_on_the_machines_load()
    {
        var idle = await AnalyzeDemoAsync();
        AnalysisResult loaded;
        using (new BusyCores())
            loaded = await AnalyzeDemoAsync();

        Assert.Equal(JsonSerializer.Serialize(idle.Findings, JsonOptions), JsonSerializer.Serialize(loaded.Findings, JsonOptions));
        Assert.Equal(Counters(idle), Counters(loaded));
        Assert.All(idle.Coverage.Concat(loaded.Coverage),
                   scope => Assert.DoesNotContain(SolverCounters.SAFEGUARD_TIMEOUT, scope.Skips.Keys));

        static string[] Counters(AnalysisResult result) =>
            [.. result.Coverage.SelectMany(scope => scope.Skips.OrderBy(counter => counter.Key, StringComparer.Ordinal)
                                                               .Select(counter => $"{scope.ScopeId} {counter.Key} {counter.Value}"))];
    }

    [Fact]
    public async Task Demo_test_project_is_not_a_process_scope()
    {
        var result = await SharedDemo.Value;

        Assert.Equal(["Demo.Web", "Demo.Worker"], result.Scopes.Select(scope => scope.Id).Order(StringComparer.Ordinal));
        Assert.All(result.Scopes, scope => Assert.DoesNotContain(scope.Projects, project => project.Contains("Demo.Tests", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Demo_counts_two_unresolved_locator_calls_and_no_unanalysed_registration_in_the_web_scope()
    {
        var result = await SharedDemo.Value;

        var web = Assert.Single(result.Coverage, coverage => coverage.ScopeId == "Demo.Web");
        Assert.Equal(2, web.Skips.GetValueOrDefault(CoverageCounters.UNRESOLVED_LOCATOR));
        Assert.Equal(0, web.Skips.GetValueOrDefault(CoverageCounters.UNANALYSED_REGISTRATION));
    }

    [Fact]
    public async Task Demo_shared_helper_is_one_finding_with_six_occurrences_in_one_group()
    {
        var result = await SharedDemo.Value;

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
        var indexedTask = AnalyzeDemoAsync();
        var referenceTask = AnalyzeDemoAsync(EngineFixture.ReferencePair);
        await Task.WhenAll(indexedTask, referenceTask);
        var indexed = await indexedTask;
        var reference = await referenceTask;
        // The one answer the clock decides is a solver query stopped by its safeguard (ADR 0021): reported on every run, so a
        // divergence shows whether a query of either analysis was cut by the machine's load.
        var solver = $"reference: {SolverAnswers(reference)}; indexed: {SolverAnswers(indexed)}";
        output.WriteLine(solver);

        Assert.NotEmpty(indexed.Findings);
        var serialized = new[] { JsonSerializer.Serialize(reference.Findings, JsonOptions), JsonSerializer.Serialize(indexed.Findings, JsonOptions) };
        var scratchRoot = Path.Combine(Path.GetDirectoryName(RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "demo", "Demo.slnx"))!,
                                       "..", "scratch", "divergent-demo-runs");
        var savedTo = SaveRunsIfTheyDiffer(serialized, scratchRoot);
        Assert.True(savedTo is null,
                    $"The reference pairing (run-0) and the candidate index (run-1) gave different findings, saved in {savedTo}; {solver}");
        Assert.Equal(reference.Pairs.Candidates, indexed.Pairs.Candidates);
        Assert.True(indexed.Pairs.Comparisons <= reference.Pairs.Comparisons);
    }

    /// <summary>The candidate index enumerates its buckets in the order of their keys, and no outcome of pairing reads that order (issue
    /// 465): with every bucket enumerated in reverse the demo gives the same findings, the same pair counters and the same skips of every
    /// scope.</summary>
    [Fact]
    public async Task Demo_outcome_is_the_same_whatever_order_the_candidate_index_enumerates_its_buckets_in()
    {
        var reverse = Comparer<string>.Create((first, second) => string.CompareOrdinal(second, first));
        var ordinalTask = AnalyzeDemoAsync();
        var reversedTask = AnalyzeDemoAsync((accesses, executions, heap) => InterproceduralPairing.Pair(accesses, executions, heap, reverse));
        await Task.WhenAll(ordinalTask, reversedTask);
        var ordinal = await ordinalTask;
        var reversed = await reversedTask;

        Assert.NotEmpty(ordinal.Findings);
        Assert.Equal(JsonSerializer.Serialize(ordinal.Findings, JsonOptions), JsonSerializer.Serialize(reversed.Findings, JsonOptions));
        Assert.Equal(JsonSerializer.Serialize(ordinal.Pairs, JsonOptions), JsonSerializer.Serialize(reversed.Pairs, JsonOptions));
        Assert.Equal(JsonSerializer.Serialize(ordinal.Coverage.Select(scope => scope.Skips), JsonOptions),
                     JsonSerializer.Serialize(reversed.Coverage.Select(scope => scope.Skips), JsonOptions));
    }

    /// <summary>What the solver answered over every scope of an analysis: its queries, the unknown answers and, among them, the
    /// queries its wall-clock safeguard stopped.</summary>
    /// <param name="result">The analysis.</param>
    private static string SolverAnswers(AnalysisResult result)
    {
        int Count(string counter) => result.Coverage.Sum(scope => scope.Skips.GetValueOrDefault(counter));
        return $"queries={Count(SolverCounters.QUERIES)} unknown={Count(SolverCounters.UNKNOWN)} safeguard={Count(SolverCounters.SAFEGUARD_TIMEOUT)}";
    }

    /// <summary>The runs serialized differently: writes each one's findings to its own file in a new folder under the root, so
    /// the evidence of a divergence outlives the failing test. Equal runs write nothing.</summary>
    /// <param name="serializedRuns">The serialized findings of each run.</param>
    /// <param name="scratchRoot">The folder under which the new folder is created.</param>
    /// <returns>The new folder, or <see langword="null"/> when all runs are equal.</returns>
    internal static string? SaveRunsIfTheyDiffer(IReadOnlyList<string> serializedRuns, string scratchRoot)
    {
        if (serializedRuns.All(serialized => serialized == serializedRuns[0]))
            return null;

        var folder = Path.GetFullPath(Path.Combine(scratchRoot, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(folder);
        for (var index = 0; index < serializedRuns.Count; index++)
            File.WriteAllText(Path.Combine(folder, $"run-{index}.json"), serializedRuns[index]);

        return folder;
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
            CancellationToken.None,
            ModelGeneration.ReadOnly);
    }
}
