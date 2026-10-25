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
    /// <summary>Uses the configured Jev, mapping each fixture plan in one call.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public Task Evaluate_sanitized_fixture_with_configured_Jev() => Evaluate("jev", "PLANFORGE_JEV_API_KEY");

    /// <summary>Uses the configured Microsoft-Decision-1 deployment the same way, paced by its request quota.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public Task Evaluate_sanitized_fixture_with_configured_Microsoft_Decision() =>
        Evaluate("microsoft-decision", "PLANFORGE_MSDECISION_API_KEY");

    /// <summary>Maps every fixture plan in one call with one configured Advisor and writes the pooled report.</summary>
    /// <param name="id">The Advisor to select.</param>
    /// <param name="keyVariable">Its credential variable, named in the failure when unset.</param>
    private async Task Evaluate(string id, string keyVariable)
    {
        Assert.True(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(keyVariable)),
            $"Set {keyVariable} to run the opt-in {id} evaluation.");
        var advisor = AdvisorCatalog.Default.Select(Environment.GetEnvironmentVariable, id);
        using var lifetime = advisor as IDisposable;
        output.WriteLine($"{advisor.Id}: requested model {advisor.RequestedModel}");
        using var fixture = new EvidenceMapFixture();
        var evaluations = new List<EvidenceEvaluation>();
        foreach (var plan in fixture.Labels.Keys)
        {
            var map = await fixture.Map(plan, advisor);
            var call = fixture.Progress[^1];
            output.WriteLine($"{plan} call: requests={call.Requests}, failed={call.FailedRequests}, "
                             + $"checked={map.Result.CheckedThisCall}, duration={call.Duration.TotalSeconds:F1} s");
            evaluations.Add(new(map, fixture.Labels[plan]));
            output.WriteLine($"{plan}: complete={map.Result.Complete}, remaining={map.Result.Remaining}");
        }
        var report = EvidenceMetrics.Of(evaluations, fixture.Progress);
        var json = JsonSerializer.Serialize(report, EvidenceFixtureJson.Default.EvidenceMetricsReport);
        var path = Path.Combine(Path.GetTempPath(), $"planforge-evidence-evaluation-{id}-{Guid.NewGuid():N}.json");
        AtomicFile.Write(path, json);
        output.WriteLine(json);
        output.WriteLine($"Evidence evaluation report: {path}");
        foreach (var evaluation in evaluations)
        {
            Assert.True(evaluation.Map.Result.Complete, $"{evaluation.Map.Result.Remaining} judgments remain after one call");
            Assert.Equal(evaluation.Labels.Select(label => label.Id).Order(),
                evaluation.Map.Observation.Select(item => item.Id).Order());
            Assert.DoesNotContain(evaluation.Map.Observation.SelectMany(item => item.Judgments)
                .SelectMany(judgment => judgment.Questions.Values), outcome => outcome.Reason == "invalid_answer");
        }
    }
}
