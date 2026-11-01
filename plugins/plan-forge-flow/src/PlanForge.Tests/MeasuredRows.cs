using System.Globalization;
using PlanForge.Advisors;

namespace PlanForge.Tests;

/// <summary>
/// The one owner of how a live measurement is written for the README, in the invariant culture. Every unknown value reads
/// as the word <c>unknown</c>, alone, so a measured zero never reads like a missing measurement and a missing one never
/// reads like zero.
/// </summary>
internal static class MeasuredRows
{
    /// <summary>The word that stands for an unknown value.</summary>
    private const string UNKNOWN = "unknown";

    /// <summary>The date line and the fixture evaluation's recall, false coverage, median latency and cost rows.</summary>
    /// <param name="report">The live fixture evaluation's report.</param>
    /// <param name="date">The day the measurement was taken.</param>
    public static IReadOnlyList<string> Fixture(EvidenceMetricsReport report, DateOnly date) =>
    [
        $"Measured on {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
        $"| Fixture obligation recall | {Ratio(report.ObligationRecall)} |",
        $"| Fixture false coverage | {Ratio(report.FalseCoverage)} |",
        $"| Fixture p50 latency | {Latency(report.LatencyP50)} |",
        $"| Fixture cost | {Usd(report.CostUsd)} |"
    ];

    /// <summary>One token row per cache request: its input, cached and written counts.</summary>
    /// <param name="first">The usage the first request reported.</param>
    /// <param name="second">The usage the second request reported.</param>
    public static IReadOnlyList<string> Cache(AdviceUsage first, AdviceUsage second) => [CacheRow(1, first), CacheRow(2, second)];

    /// <summary>The token row of one cache request.</summary>
    /// <param name="number">The request's position in the sequence, from 1.</param>
    /// <param name="usage">The usage that request reported.</param>
    private static string CacheRow(int number, AdviceUsage usage) =>
        $"| Cache request {number} tokens (input / cached / written) | {Count(usage.InputTokens)} / "
        + $"{Count(usage.CachedInputTokens)} / {Count(usage.CacheWriteTokens)} |";

    /// <summary>A ratio with three decimals, or unknown.</summary>
    /// <param name="value">The ratio, or null when unknown.</param>
    private static string Ratio(double? value) => value?.ToString("0.000", CultureInfo.InvariantCulture) ?? UNKNOWN;

    /// <summary>Whole milliseconds followed by the unit, or unknown.</summary>
    /// <param name="milliseconds">The latency in milliseconds, or null when unknown.</param>
    private static string Latency(double? milliseconds) =>
        milliseconds is { } value ? $"{value.ToString("0", CultureInfo.InvariantCulture)} ms" : UNKNOWN;

    /// <summary>A dollar sign followed by four decimals, or unknown.</summary>
    /// <param name="usd">The cost in USD, or null when unknown.</param>
    private static string Usd(decimal? usd) => usd is { } value ? $"${value.ToString("0.0000", CultureInfo.InvariantCulture)}" : UNKNOWN;

    /// <summary>A plain integer, or unknown.</summary>
    /// <param name="tokens">The token count, or null when unknown.</param>
    private static string Count(long? tokens) => tokens?.ToString(CultureInfo.InvariantCulture) ?? UNKNOWN;
}
