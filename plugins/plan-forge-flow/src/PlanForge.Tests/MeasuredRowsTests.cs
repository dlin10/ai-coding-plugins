using System.Globalization;
using PlanForge.Advisors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>How live measurements read in the README: known values in their formats, unknown and zero never confused.</summary>
public sealed class MeasuredRowsTests
{
    /// <summary>The day every case is measured on.</summary>
    private static readonly DateOnly DATE = new(2026, 11, 1);

    /// <summary>Known values use their formats in the invariant culture, whatever the current culture.</summary>
    [Fact]
    public void Known_values_use_their_formats()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.Equal([
                "Measured on 2026-11-01",
                "| Fixture obligation recall | 0.857 |",
                "| Fixture false coverage | 0.062 |",
                "| Fixture p50 latency | 1235 ms |",
                "| Fixture cost | $0.0012 |"
            ], MeasuredRows.Fixture(Report(0.8571428, 0.0624, 1234.6, 0.00123456m), DATE));
            Assert.Equal([
                "| Cache request 1 tokens (input / cached / written) | 3012 / 0 / 2759 |",
                "| Cache request 2 tokens (input / cached / written) | 3008 / 2759 / 0 |"
            ], MeasuredRows.Cache(new(3012, 2, 0, 2759), new(3008, 2, 2759, 0)));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    /// <summary>Every unknown value reads as the word unknown, alone.</summary>
    [Fact]
    public void Unknown_values_read_unknown()
    {
        Assert.Equal([
            "Measured on 2026-11-01",
            "| Fixture obligation recall | unknown |",
            "| Fixture false coverage | unknown |",
            "| Fixture p50 latency | unknown |",
            "| Fixture cost | unknown |"
        ], MeasuredRows.Fixture(Report(null, null, null, null), DATE));
        Assert.Equal([
            "| Cache request 1 tokens (input / cached / written) | unknown / unknown / unknown |",
            "| Cache request 2 tokens (input / cached / written) | unknown / unknown / unknown |"
        ], MeasuredRows.Cache(new(null, null, null, null), new(null, null, null, null)));
    }

    /// <summary>Measured zeros keep their formats and never read like a missing measurement.</summary>
    [Fact]
    public void Zeros_read_as_measured_zeros()
    {
        Assert.Equal([
            "Measured on 2026-11-01",
            "| Fixture obligation recall | 0.000 |",
            "| Fixture false coverage | 0.000 |",
            "| Fixture p50 latency | 0 ms |",
            "| Fixture cost | $0.0000 |"
        ], MeasuredRows.Fixture(Report(0, 0, 0, 0m), DATE));
        Assert.Equal([
            "| Cache request 1 tokens (input / cached / written) | 0 / 0 / 0 |",
            "| Cache request 2 tokens (input / cached / written) | 0 / 0 / 0 |"
        ], MeasuredRows.Cache(new(0, 0, 0, 0), new(0, 0, 0, 0)));
    }

    /// <summary>A report whose written fields are the given values and whose other fields mirror them.</summary>
    /// <param name="ratio">Every ratio but false coverage, the obligation recall among them.</param>
    /// <param name="falseCoverage">The false coverage.</param>
    /// <param name="latency">The median and ninetieth-percentile latency in milliseconds.</param>
    /// <param name="cost">The cost in USD; every token count is zero when it is known and null when it is not.</param>
    private static EvidenceMetricsReport Report(double? ratio, double? falseCoverage, double? latency, decimal? cost)
    {
        long? tokens = cost is null ? null : 0;
        return new(ratio, ratio, falseCoverage, ratio, ratio, ratio, latency, latency, tokens, tokens, tokens, tokens, cost,
                   cost is not null);
    }
}
