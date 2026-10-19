using System.Text.Json;
using PlanForge.Advisors;
using PlanForge.Evidence;
using PlanForge.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace PlanForge.Tests;

/// <summary>Opt-in live evaluation of sanitized evidence, reporting quality without a quality threshold.</summary>
/// <param name="output">The test runner's evaluation report sink.</param>
public sealed class EvidenceMapLiveEvaluationTests(ITestOutputHelper output)
{
    /// <summary>Uses the configured Jev, resuming bounded calls until complete or no further judgment is checked.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Evaluate_sanitized_fixture_with_configured_Jev()
    {
        Assert.True(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PLANFORGE_JEV_API_KEY")),
            "Set PLANFORGE_JEV_API_KEY to run the opt-in Jev evaluation.");
        var advisor = AdvisorCatalog.Default.Select(Environment.GetEnvironmentVariable, "jev");
        using var lifetime = advisor as IDisposable;
        using var fixture = new EvidenceMapFixture();
        var evaluations = new List<EvidenceEvaluation>();
        foreach (var plan in fixture.Labels.Keys)
        {
            // Each plan starts with an independent store while progress remains pooled over every call.
            File.Delete(fixture.Run.EvidenceMapPath);
            EvidenceMap last;
            do { last = await fixture.Map(plan, advisor); }
            while (!last.Result.Complete && last.Result.CheckedThisCall != 0);
            evaluations.Add(new(last, fixture.Labels[plan]));
            output.WriteLine($"{plan}: complete={last.Result.Complete}, remaining={last.Result.Remaining}");
        }
        var report = EvidenceMetrics.Of(evaluations, fixture.Progress);
        var json = JsonSerializer.Serialize(report, EvidenceFixtureJson.Default.EvidenceMetricsReport);
        var path = Path.Combine(Path.GetTempPath(), $"planforge-evidence-evaluation-{Guid.NewGuid():N}.json");
        AtomicFile.Write(path, json);
        output.WriteLine(json);
        output.WriteLine($"Evidence evaluation report: {path}");
        foreach (var evaluation in evaluations)
        {
            Assert.Equal(evaluation.Labels.Select(label => label.Id).Order(),
                evaluation.Map.Observation.Select(item => item.Id).Order());
            Assert.DoesNotContain(evaluation.Map.Observation.SelectMany(item => item.Judgments)
                .SelectMany(judgment => judgment.Questions.Values), outcome => outcome.Reason == "invalid_answer");
        }
    }
}
