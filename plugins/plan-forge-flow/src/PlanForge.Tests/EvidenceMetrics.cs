using PlanForge.Evidence;

namespace PlanForge.Tests;

/// <summary>The last observation for one plan, paired with that plan's independent labels.</summary>
/// <param name="Map">The last call's result and complete observation, including cache hits.</param>
/// <param name="Labels">The expectations for this plan.</param>
internal sealed record EvidenceEvaluation(EvidenceMap Map, IReadOnlyList<EvidenceLabel> Labels);

/// <summary>Pooled evaluation ratios, latency, reported usage and completion; empty denominators stay unknown.</summary>
/// <param name="CandidateRecall">Selected labelled candidate pairs divided by labelled pairs.</param>
/// <param name="ObligationRecall">Explicit obligations divided by checked truth obligation pairs.</param>
/// <param name="FalseCoverage">Covered items whose truth is not covered, divided by all covered items.</param>
/// <param name="Abstentions">Insufficient-context answers divided by checked questions.</param>
/// <param name="UncheckedShare">Unchecked items divided by every observed item.</param>
/// <param name="LocatorValidity">Resolved repository items divided by repository items.</param>
/// <param name="LatencyP50">Nearest-rank median over every request in every call, in milliseconds.</param>
/// <param name="LatencyP90">Nearest-rank ninetieth percentile over those same requests.</param>
/// <param name="InputTokens">All reported input tokens, null when none were reported.</param>
/// <param name="OutputTokens">All reported output tokens, null when none were reported.</param>
/// <param name="CostUsd">Input-only cost at the rate jev-1.13.0 and Microsoft-Decision-1 both publish.</param>
/// <param name="Complete">Whether the last call for every evaluated plan completed.</param>
internal sealed record EvidenceMetricsReport(double? CandidateRecall, double? ObligationRecall,
                                             double? FalseCoverage, double? Abstentions, double? UncheckedShare, double? LocatorValidity,
                                             double? LatencyP50, double? LatencyP90, long? InputTokens, long? OutputTokens,
                                             decimal? CostUsd, bool Complete);

/// <summary>Computes R18 metrics without consulting a model or counting earlier observations again.</summary>
internal static class EvidenceMetrics
{
    /// <summary>
    /// The input price in USD per million tokens published for jev-1.13.0 on 2026-10-19 and for Microsoft-Decision-1 on
    /// 2026-10-09; output is free for Jev and unpriced for Microsoft. A price that diverges needs a rate per Advisor.
    /// </summary>
    private const decimal InputUsdPerMillion = 0.042m;

    /// <summary>Pools counts over each plan's last observation and usage and raw durations over every call.</summary>
    /// <param name="evaluations">Exactly one last map and label set per plan.</param>
    /// <param name="progress">Every mapper call made during the evaluation.</param>
    public static EvidenceMetricsReport Of(IReadOnlyList<EvidenceEvaluation> evaluations,
                                           IReadOnlyList<EvidenceMapProgress> progress)
    {
        var candidateTotal = 0;
        var candidateSelected = 0;
        var obligationChecked = 0;
        var obligationAnswered = 0;
        var covered = 0;
        var falselyCovered = 0;
        var checkedQuestions = 0;
        var abstentions = 0;
        var items = 0;
        var uncheckedItems = 0;
        var repositoryItems = 0;
        var resolved = 0;
        foreach (var evaluation in evaluations)
        {
            var labels = evaluation.Labels.ToDictionary(label => label.Id);
            var observed = evaluation.Map.Observation.ToDictionary(item => item.Id);
            foreach (var label in evaluation.Labels)
            {
                candidateTotal += label.Candidates.Length;
                if (!observed.TryGetValue(label.Id, out var item)) continue;
                candidateSelected += label.Candidates.Count(item.CandidateUnitIds.Contains);
                foreach (var unit in label.Truth.Obligations)
                {
                    var answer = item.Judgments.FirstOrDefault(judgment => judgment.UnitId == unit)
                                     ?.Questions.GetValueOrDefault("obligation")?.Answer;
                    if (answer is null) continue;
                    obligationChecked++;
                    if (answer.Choice == "explicit_obligation") obligationAnswered++;
                }
            }
            foreach (var item in evaluation.Map.Observation)
            {
                items++;
                if (item.Status == "unchecked") uncheckedItems++;
                if (item.Status == "covered")
                {
                    covered++;
                    if (labels[item.Id].Truth.Status != "covered") falselyCovered++;
                }
                if (item.LocatorCheck is not null)
                {
                    repositoryItems++;
                    if (item.LocatorCheck.Resolved) resolved++;
                }
            }
            // A shared key has one actual outcome, even if several items or units use it.
            foreach (var judgment in evaluation.Map.Observation.SelectMany(item => item.Judgments).DistinctBy(judgment => judgment.Key))
                foreach (var outcome in judgment.Questions.Values)
                    if (outcome.Answer is { } answer)
                    {
                        checkedQuestions++;
                        if (answer.Choice == "insufficient_context") abstentions++;
                    }
        }
        var durations = progress.SelectMany(call => call.AskDurations).Order().ToArray();
        var input = Sum(progress.Select(call => call.InputTokens));
        var output = Sum(progress.Select(call => call.OutputTokens));
        return new(Ratio(candidateSelected, candidateTotal), Ratio(obligationAnswered, obligationChecked),
            Ratio(falselyCovered, covered), Ratio(abstentions, checkedQuestions), Ratio(uncheckedItems, items),
            Ratio(resolved, repositoryItems), Percentile(durations, 0.5), Percentile(durations, 0.9), input, output,
            input is null ? null : input.Value * InputUsdPerMillion / 1_000_000m,
            evaluations.All(evaluation => evaluation.Map.Result.Complete));
    }

    /// <summary>Leaves a zero denominator undefined.</summary>
    /// <param name="numerator">The pooled qualifying count.</param>
    /// <param name="denominator">The pooled eligible count.</param>
    private static double? Ratio(int numerator, int denominator) => denominator == 0 ? null : (double)numerator / denominator;

    /// <summary>Preserves missing usage rather than reporting a fabricated zero.</summary>
    /// <param name="counts">Reported token totals from every call.</param>
    private static long? Sum(IEnumerable<long?> counts)
    {
        var reported = counts.Where(count => count.HasValue).ToArray();
        return reported.Length == 0 ? null : reported.Sum(count => count!.Value);
    }

    /// <summary>Computes nearest rank on pooled raw durations, never on per-call percentiles.</summary>
    /// <param name="sorted">Every request duration sorted ascending.</param>
    /// <param name="fraction">The requested percentile fraction.</param>
    private static double? Percentile(double[] sorted, double fraction) => sorted.Length == 0 ? null
        : sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];
}
