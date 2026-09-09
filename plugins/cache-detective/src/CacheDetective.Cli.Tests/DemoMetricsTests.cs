using System.Text.Json;
using System.Text.Json.Serialization;
using CacheDetective.Database;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Rules;
using CacheDetective.Tests.Database;
using CacheDetective.Workspaces;
using Xunit;

namespace CacheDetective.Tests;

public sealed class DemoMetricsTests(DemoMetricsFixture fixture) : IClassFixture<DemoMetricsFixture>
{
    private const string PURPOSE =
        "The demo metrics gate needs MSBuild and SQL Server. Point it at a SQL Server connection string.";

    [RequiresSqlServerFact(PURPOSE)]
    public async Task Precision_meets_the_threshold()
    {
        var (expected, runs) = await fixture.GetAsync();
        var metrics = DemoMetrics.Measure(expected, runs[0].Reported);

        Assert.True(metrics.Precision >= 0.95, $"Demo precision was {metrics.Precision:F3}; expected at least 0.950.");
    }

    [RequiresSqlServerFact(PURPOSE)]
    public async Task Recall_meets_the_threshold()
    {
        var (expected, runs) = await fixture.GetAsync();
        var metrics = DemoMetrics.Measure(expected, runs[0].Reported);

        Assert.True(metrics.Recall >= 0.8, $"Demo recall was {metrics.Recall:F3}; expected at least 0.800.");
    }

    [Fact]
    public void A_difference_between_three_runs_fails()
    {
        var first = new DemoRun([Finding("UNGUARDED_WRITE", Confidence.Confirmed)], []);
        var different = new DemoRun([Finding("EXTERNAL_NO_TTL", Confidence.Confirmed)], []);

        var error = Assert.Throws<InvalidOperationException>(() => DemoMetrics.AssertStable([first, first, different]));

        Assert.Contains("run 3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_metric_below_its_threshold_fails()
    {
        var expected = new ExpectedFindings([Finding("UNGUARDED_WRITE", Confidence.Confirmed)], [], []);
        var observed = new[] { Finding("EXTERNAL_NO_TTL", Confidence.Confirmed) };

        var error = Assert.Throws<InvalidOperationException>(() => DemoMetrics.AssertThresholds(DemoMetrics.Measure(expected, observed)));

        Assert.Contains("precision", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_expected_finding_with_an_unknown_rule_is_rejected()
    {
        var path = SqlServerHarness.FindRepositoryFile("plugins", "cache-detective", "demo", "expected-findings.json");
        var parsed = await DemoMetrics.ReadExpectedAsync(path);
        Assert.NotEmpty(parsed.Findings);
        var expected = new ExpectedFindings([Finding("NOT_A_RULE", Confidence.Confirmed)], [], []);

        var error = Assert.Throws<InvalidDataException>(() => DemoMetrics.Validate(expected));

        Assert.Contains("NOT_A_RULE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Confidence_is_part_of_the_identity()
    {
        var expected = new ExpectedFindings([Finding("UNGUARDED_WRITE", Confidence.Confirmed)], [], []);

        var metrics = DemoMetrics.Measure(expected,
            [Finding("UNGUARDED_WRITE", Confidence.Likely), Finding("EXTERNAL_NO_TTL", Confidence.Confirmed)]);

        Assert.Equal(0, metrics.Precision);
        Assert.Equal(0, metrics.Recall);
    }

    [RequiresSqlServerFact(PURPOSE)]
    public async Task Suppressed_findings_are_compared_separately()
    {
        var (expected, runs) = await fixture.GetAsync();

        DemoMetrics.AssertSuppressed(expected, runs[0].Suppressed);
    }

    [Fact]
    public void A_finding_at_a_not_defect_location_fails()
    {
        var location = Finding("UNGUARDED_WRITE", Confidence.Confirmed);
        var expected = new ExpectedFindings([], [location with { Confidence = null }], []);

        var error = Assert.Throws<InvalidOperationException>(() => DemoMetrics.AssertNoNotDefects(expected, [location]));

        Assert.Contains("notDefects", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zero_denominator_fails_with_a_clear_message()
    {
        var expected = new ExpectedFindings([], [], []);

        var error = Assert.Throws<InvalidOperationException>(() => DemoMetrics.Measure(expected, []));

        Assert.Contains("denominator", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ExpectedFinding Finding(string rule, Confidence? confidence) =>
        new(rule, "Shop.slnx", "Demo.Controller.Get()", "Demo", "product:{id}", "memory", "dbo.Products", confidence);
}

public sealed class DemoMetricsFixture : IAsyncLifetime
{
    private const string SOLUTION_NAME = "Shop.slnx";
    private const string DATABASE = "shop";
    private readonly Lazy<Task<(ExpectedFindings Expected, IReadOnlyList<DemoRun> Runs)>> _runs;

    public DemoMetricsFixture() => _runs = new Lazy<Task<(ExpectedFindings, IReadOnlyList<DemoRun>)>>(RunAsync);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;
    internal Task<(ExpectedFindings Expected, IReadOnlyList<DemoRun> Runs)> GetAsync() => _runs.Value;

    private static async Task<(ExpectedFindings Expected, IReadOnlyList<DemoRun> Runs)> RunAsync()
    {
        var solutionPath = SqlServerHarness.FindRepositoryFile("plugins", "cache-detective", "demo", "Shop.slnx");
        var scriptPath = SqlServerHarness.FindRepositoryFile("plugins", "cache-detective", "demo", "db", "shop.sql");
        var expectedPath = SqlServerHarness.FindRepositoryFile("plugins", "cache-detective", "demo", "expected-findings.json");
        var expected = await DemoMetrics.ReadExpectedAsync(expectedPath);
        await using var harness = await SqlServerHarness.CreateAsync();
        await harness.ApplyAsync(scriptPath);
        var runs = new List<DemoRun>();
        for (var index = 0; index < 3; index++)
            runs.Add(await IndexAsync(solutionPath, harness));
        DemoMetrics.AssertStable(runs);
        DemoMetrics.AssertNoNotDefects(expected, runs[0].Reported.Concat(runs[0].Suppressed).ToArray());
        return (expected, runs);
    }

    private static async Task<DemoRun> IndexAsync(string solutionPath, SqlServerHarness harness)
    {
        var graph = new CacheGraph();
        using (var loaded = await new MsBuildSolutionLoader().LoadAsync(solutionPath))
        {
            var code = await new CallGraphIndexer().IndexAsync(loaded.Solution, SOLUTION_NAME);
            graph.ReplaceSolution(SOLUTION_NAME, code);
        }
        await using var connection = await harness.OpenAsync();
        var catalogue = await new DatabaseIndexer().IndexAsync(connection, DATABASE);
        graph.ReplaceDatabase(DATABASE, catalogue.Graph);
        return DemoMetrics.Collect(graph);
    }
}

internal static class DemoMetrics
{
    private static readonly HashSet<string> KNOWN_RULES =
    [
        UnguardedWriteFinding.Rule,
        UnguardedWriteFinding.CrossServiceGapRule,
        ExternalNoTtlFinding.Rule,
        StaleParentKeyFinding.Rule,
        OrphanInvalidationFinding.Rule,
        PatternMismatchFinding.Rule
    ];

    /// <summary>The findings of one run, named by the shared identity so that the metrics and the
    /// behaviour snapshot cannot disagree about which finding is which.</summary>
    internal static DemoRun Collect(CacheGraph graph)
    {
        var all = FindingIdentities.Collect(graph)
                                   .Select(finding => (Finding: new ExpectedFinding(finding.Rule, finding.Solution, finding.Handler,
                                                                                     finding.Project, finding.Template, finding.Store,
                                                                                     finding.Target, finding.Confidence),
                                                       finding.Suppressed))
                                   .ToArray();
        return new DemoRun(all.Where(item => !item.Suppressed).Select(item => item.Finding).Order().ToArray(),
                           all.Where(item => item.Suppressed).Select(item => item.Finding).Order().ToArray());
    }

    internal static async Task<ExpectedFindings> ReadExpectedAsync(string path)
    {
        ExpectedFindings? expected;
        await using (var stream = File.OpenRead(path))
        {
            try
            {
                expected = await JsonSerializer.DeserializeAsync<ExpectedFindings>(stream, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
                });
            }
            catch (JsonException error)
            {
                // A raw System.Text.Json stack says nothing about which expectation is malformed, and this
                // file is hand-written, so name the file and the offending path instead.
                throw new InvalidDataException($"'{path}' is not a valid expectation at {error.Path}: {error.Message}", error);
            }
        }

        Validate(expected ?? throw new InvalidDataException($"'{path}' is empty."));
        return expected;
    }

    internal static void Validate(ExpectedFindings expected)
    {
        foreach (var finding in expected.Findings.Concat(expected.NotDefects).Concat(expected.Suppressed))
            if (!KNOWN_RULES.Contains(finding.Rule))
                throw new InvalidDataException($"Expected finding names unknown rule '{finding.Rule}'.");
    }

    internal static DemoScores Measure(ExpectedFindings expected, IReadOnlyList<ExpectedFinding> reported)
    {
        var confirmed = reported.Where(finding => finding.Confidence == Confidence.Confirmed).ToArray();
        if (confirmed.Length == 0)
            throw new InvalidOperationException("Precision denominator is zero: the demo emitted no confirmed findings.");
        if (expected.Findings.Count == 0)
            throw new InvalidOperationException("Recall denominator is zero: the demo expectation contains no findings.");
        var precision = (double)confirmed.Count(expected.Findings.Contains) / confirmed.Length;
        var recall = (double)expected.Findings.Count(reported.Contains) / expected.Findings.Count;
        return new DemoScores(precision, recall);
    }

    internal static void AssertThresholds(DemoScores scores)
    {
        if (scores.Precision < 0.95)
            throw new InvalidOperationException($"Demo precision {scores.Precision:F3} is below 0.950.");
        if (scores.Recall < 0.8)
            throw new InvalidOperationException($"Demo recall {scores.Recall:F3} is below 0.800.");
    }

    internal static void AssertStable(IReadOnlyList<DemoRun> runs)
    {
        var first = runs.FirstOrDefault() ?? throw new InvalidOperationException("No demo runs were supplied.");
        for (var index = 1; index < runs.Count; index++)
        {
            // DemoRun is a record over two lists, and record equality compares those lists by reference,
            // so two runs that agree in every finding would still compare unequal. Compare the elements.
            var run = runs[index];
            if (first.Reported.SequenceEqual(run.Reported) && first.Suppressed.SequenceEqual(run.Suppressed))
                continue;

            var added = run.Reported.Except(first.Reported).Concat(run.Suppressed.Except(first.Suppressed)).ToArray();
            var vanished = first.Reported.Except(run.Reported).Concat(first.Suppressed.Except(run.Suppressed)).ToArray();
            throw new InvalidOperationException(
                $"Demo findings differ in run {index + 1}. Added: [{string.Join("; ", added.Select(f => f.ToString()))}]. " +
                $"Vanished: [{string.Join("; ", vanished.Select(f => f.ToString()))}].");
        }
    }

    internal static void AssertSuppressed(ExpectedFindings expected, IReadOnlyList<ExpectedFinding> suppressed)
    {
        if (!expected.Suppressed.Order().SequenceEqual(suppressed.Order()))
            throw new InvalidOperationException("Suppressed demo findings differ from the expectation.");
    }

    internal static void AssertNoNotDefects(ExpectedFindings expected, IReadOnlyList<ExpectedFinding> reported)
    {
        var defect = reported.FirstOrDefault(finding => expected.NotDefects.Any(notDefect => notDefect.MatchesLocation(finding)));
        if (defect is not null)
            throw new InvalidOperationException($"A finding was emitted at a notDefects location: {defect.Rule} {defect.Handler}.");
    }
}

internal sealed record ExpectedFindings(IReadOnlyList<ExpectedFinding> Findings, IReadOnlyList<ExpectedFinding> NotDefects,
                                        IReadOnlyList<ExpectedFinding> Suppressed);
/// <param name="Target">The rule's own target: the table for an unguarded write, the external source for
/// <c>EXTERNAL_NO_TTL</c>, the child key for <c>STALE_PARENT_KEY</c>.</param>
internal sealed record ExpectedFinding(string Rule, string Solution, string Handler, string Project, string? KeyTemplate,
                                       string? Store, string Target, Confidence? Confidence) : IComparable<ExpectedFinding>
{
    public int CompareTo(ExpectedFinding? other) => string.Compare(ToString(), other?.ToString(), StringComparison.Ordinal);

    /// <summary>
    /// Whether this row and that finding are at the same <em>place</em>. A place is the handler, and only
    /// the handler: a <c>notDefects</c> entry says this code is known to be correct, so a finding of any
    /// rule, about any target, at any confidence, sitting there is the failure it exists to catch.
    /// Comparing the rule and the target as well meant a wrong finding of a different rule slipped past
    /// the very entry that was meant to stop it.
    /// </summary>
    public bool MatchesLocation(ExpectedFinding other) =>
        Solution == other.Solution && Handler == other.Handler && Project == other.Project;
}
internal sealed record DemoRun(IReadOnlyList<ExpectedFinding> Reported, IReadOnlyList<ExpectedFinding> Suppressed);
internal sealed record DemoScores(double Precision, double Recall);
