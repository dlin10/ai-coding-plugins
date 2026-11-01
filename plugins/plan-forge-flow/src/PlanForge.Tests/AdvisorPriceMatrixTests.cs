using PlanForge.Advisors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// Totals and price (R4) walked as a matrix, through <see cref="AdviceUsageTotals"/> and <see cref="AdvisorPrice.Usd"/>
/// only, with a fake Advisor per rate set. Every expectation is written by hand from R4, never from the code under test.
/// </summary>
public sealed class AdvisorPriceMatrixTests
{
    /// <summary>The cells known to be wider than their expectation: none, since every price and total must be exact.</summary>
    private static readonly string[] WIDER = [];

    /// <summary>Price axis: all input tokens.</summary>
    private static readonly long?[] INPUTS = [null, 0, 1_000_000];

    /// <summary>Price axis: the part of the input read from the cache.</summary>
    private static readonly long?[] CACHED = [null, 0, 200_000];

    /// <summary>Price axis: the part of the input written to the cache.</summary>
    private static readonly long?[] WRITTEN = [null, 0, 300_000];

    /// <summary>The reported usage of each reply; no usage at all for <see cref="Reply.None"/>.</summary>
    private static readonly Dictionary<Reply, AdviceUsage?> USAGE = new()
    {
        [Reply.A] = new AdviceUsage(1_000_000, 10, 200_000, 300_000),
        [Reply.B] = new AdviceUsage(400_000, 5, null, null),
        [Reply.C] = new AdviceUsage(null, 7, 300_000, 50_000),
        [Reply.D] = new AdviceUsage(500_000, null, 100_000, null),
        [Reply.E] = new AdviceUsage(0, 0, 0, 0),
        [Reply.None] = null,
    };

    /// <summary>
    /// What each reply adds to totals under R4: input and output as reported; the cache counts only together with that
    /// reply's own input, so C's never count; a reply without usage adds nothing.
    /// </summary>
    private static readonly Dictionary<Reply, AdviceUsageTotals> COUNTED = new()
    {
        [Reply.A] = new(1_000_000, 10, 200_000, 300_000),
        [Reply.B] = new(400_000, 5, null, null),
        [Reply.C] = new(null, 7, null, null),
        [Reply.D] = new(500_000, null, 100_000, null),
        [Reply.E] = new(0, 0, 0, 0),
        [Reply.None] = new(null, null, null, null),
    };

    /// <summary>
    /// Each reply's own price under R4: (input − cached − written) × uncached + cached × read + written × write over a
    /// million, an unknown cache count as zero, an unknown input (C, no usage) unknown.
    /// </summary>
    private static readonly Dictionary<(Reply, Rates), decimal?> REPLY_PRICE = new()
    {
        [(Reply.A, Rates.SystemOne)] = 0.042m,
        [(Reply.B, Rates.SystemOne)] = 0.0168m,
        [(Reply.C, Rates.SystemOne)] = null,
        [(Reply.D, Rates.SystemOne)] = 0.021m,
        [(Reply.E, Rates.SystemOne)] = 0m,
        [(Reply.None, Rates.SystemOne)] = null,
        [(Reply.A, Rates.OpenAi)] = 0.0895m,
        [(Reply.B, Rates.OpenAi)] = 0.04m,
        [(Reply.C, Rates.OpenAi)] = null,
        [(Reply.D, Rates.OpenAi)] = 0.041m,
        [(Reply.E, Rates.OpenAi)] = 0m,
        [(Reply.None, Rates.OpenAi)] = null,
    };

    /// <summary>The rate sets: uncached, cache-read and cache-write input rates in USD per million tokens.</summary>
    public enum Rates
    {
        /// <summary>Jev and Microsoft-Decision-1: their one rate, 0.042, for all three.</summary>
        SystemOne,
        /// <summary>OpenAI Decisions: 0.10, 0.01 and 0.125.</summary>
        OpenAi,
    }

    /// <summary>The replies a total is made of.</summary>
    public enum Reply
    {
        /// <summary>1,000,000 input, 10 output, 200,000 cached, 300,000 written.</summary>
        A,
        /// <summary>400,000 input, 5 output, no cache counts.</summary>
        B,
        /// <summary>Unknown input, 7 output, 300,000 cached, 50,000 written.</summary>
        C,
        /// <summary>500,000 input, unknown output, 100,000 cached, unknown written.</summary>
        D,
        /// <summary>Every count zero.</summary>
        E,
        /// <summary>No usage at all.</summary>
        None,
    }

    /// <summary>Every allowed price cell.</summary>
    public static TheoryData<long?, long?, long?, Rates> PriceCells()
    {
        var data = new TheoryData<long?, long?, long?, Rates>();
        foreach (var (input, cached, written, rates) in AllowedPriceCells()) data.Add(input, cached, written, rates);
        return data;
    }

    /// <summary>Every totals cell: an ordered pair of replies and a rate set.</summary>
    public static TheoryData<Reply, Reply, Rates> TotalsCells()
    {
        var data = new TheoryData<Reply, Reply, Rates>();
        foreach (var (first, second, rates) in AllTotalsCells()) data.Add(first, second, rates);
        return data;
    }

    // ---- The matrix ----

    /// <summary>One price cell: the price of the counts is exactly R4's.</summary>
    /// <param name="input">All input tokens, or null when unknown.</param>
    /// <param name="cached">The cached part, or null when unknown.</param>
    /// <param name="written">The cache-written part, or null when unknown.</param>
    /// <param name="rates">The Advisor's rate set.</param>
    [Theory]
    [MemberData(nameof(PriceCells))]
    public void Every_price_cell_gets_its_expected_answer(long? input, long? cached, long? written, Rates rates)
    {
        Assert.Empty(WIDER);

        var price = AdvisorPrice.Usd(Advisor(rates), input, cached, written);

        Assert.Equal(ExpectedPrice(input, cached, written, rates), price);
        // R4: an unknown input gives an unknown price; a known one is never negative.
        Assert.Equal(input is null, price is null);
        if (price is { } known) Assert.True(known >= 0m, $"negative price {known}");
        // R4: one rate for all three, so a million input tokens cost 0.042 whatever the cache counts.
        if (rates == Rates.SystemOne && input == 1_000_000) Assert.Equal(0.042m, price);
        if (input == 0) Assert.Equal(0m, price);
        if (rates == Rates.OpenAi && input == 1_000_000 && cached == 200_000 && written == 300_000) Assert.Equal(0.0895m, price);
    }

    /// <summary>One totals cell: the totals of two replies, their pooled form, and the price of the totals are exactly R4's.</summary>
    /// <param name="first">The first reply.</param>
    /// <param name="second">The second reply.</param>
    /// <param name="rates">The Advisor's rate set.</param>
    [Theory]
    [MemberData(nameof(TotalsCells))]
    public void Every_totals_cell_gets_its_expected_answer(Reply first, Reply second, Rates rates)
    {
        Assert.Empty(WIDER);
        var advisor = Advisor(rates);

        var totals = AdviceUsageTotals.Empty.Add(USAGE[first]).Add(USAGE[second]);
        var pooled = AdviceUsageTotals.Empty.Add(USAGE[first]).Add(AdviceUsageTotals.Empty.Add(USAGE[second]));

        // R4: input and output add up the known counts and stay unknown until one is; cache counts only with their input.
        var expected = new AdviceUsageTotals(Sum(COUNTED[first].InputTokens, COUNTED[second].InputTokens),
                                             Sum(COUNTED[first].OutputTokens, COUNTED[second].OutputTokens),
                                             Sum(COUNTED[first].CachedInputTokens, COUNTED[second].CachedInputTokens),
                                             Sum(COUNTED[first].CacheWriteTokens, COUNTED[second].CacheWriteTokens));
        Assert.Equal(expected, totals);
        // R4: totals of several calls add up the same way.
        Assert.Equal(expected, pooled);
        // R4: totals never hold more cached plus written tokens than input.
        Assert.True((totals.CachedInputTokens ?? 0) + (totals.CacheWriteTokens ?? 0) <= (totals.InputTokens ?? 0), $"{totals}");

        // R4: each reply's own price, then the price of the totals as the sum of theirs, null only when both are.
        foreach (var reply in new[] { first, second })
        {
            var usage = USAGE[reply];
            Assert.Equal(REPLY_PRICE[(reply, rates)],
                         usage is null ? null : AdvisorPrice.Usd(advisor, usage.InputTokens, usage.CachedInputTokens, usage.CacheWriteTokens));
        }

        var price = AdvisorPrice.Usd(advisor, totals.InputTokens, totals.CachedInputTokens, totals.CacheWriteTokens);
        var (left, right) = (REPLY_PRICE[(first, rates)], REPLY_PRICE[(second, rates)]);
        Assert.Equal(left is null && right is null ? null : (left ?? 0m) + (right ?? 0m), price);
        if (price is { } known) Assert.True(known >= 0m, $"negative price {known}");
    }

    /// <summary>Every value of every axis of both matrices occurs in some cell.</summary>
    [Fact]
    public void Every_axis_value_occurs_in_some_cell()
    {
        var prices = AllowedPriceCells().ToList();
        Assert.Equal(INPUTS, prices.Select(cell => cell.Input).Distinct());
        Assert.Equal(CACHED, prices.Select(cell => cell.Cached).Distinct());
        Assert.Equal(WRITTEN, prices.Select(cell => cell.Written).Distinct());
        Assert.Equal(Enum.GetValues<Rates>(), prices.Select(cell => cell.Rates).Distinct());

        var totals = AllTotalsCells().ToList();
        Assert.Equal(Enum.GetValues<Reply>(), totals.Select(cell => cell.First).Distinct());
        Assert.Equal(Enum.GetValues<Reply>(), totals.Select(cell => cell.Second).Distinct());
        Assert.Equal(Enum.GetValues<Rates>(), totals.Select(cell => cell.Rates).Distinct());
        Assert.Equal(72, totals.Count);
    }

    /// <summary>The filter drops exactly the ten price cells whose known cache counts exceed a known input of zero.</summary>
    [Fact]
    public void Excluded_price_cells_are_the_ten_inconsistent_ones()
    {
        var excluded = AllPriceCells().Except(AllowedPriceCells()).ToList();

        Assert.Equal(10, excluded.Count);
        Assert.Equal(44, AllowedPriceCells().Count());
        Assert.All(excluded, cell => Assert.True(cell.Input == 0 && (cell.Cached ?? 0) + (cell.Written ?? 0) > 0, $"{cell}"));
        foreach (var rates in Enum.GetValues<Rates>()) Assert.Equal(5, excluded.Count(cell => cell.Rates == rates));
        Assert.All(AllowedPriceCells(), cell => Assert.True(cell.Input is null || (cell.Cached ?? 0) + (cell.Written ?? 0) <= cell.Input,
                                                            $"{cell}"));
    }

    // ---- The expectation ----

    /// <summary>Every combination of the price axes.</summary>
    private static IEnumerable<(long? Input, long? Cached, long? Written, Rates Rates)> AllPriceCells() =>
        from input in INPUTS
        from cached in CACHED
        from written in WRITTEN
        from rates in Enum.GetValues<Rates>()
        select (input, cached, written, rates);

    /// <summary>The price cells the totals rule and the OpenAI parser can hand to pricing.</summary>
    private static IEnumerable<(long? Input, long? Cached, long? Written, Rates Rates)> AllowedPriceCells() =>
        AllPriceCells().Where(cell => Allowed(cell.Input, cell.Cached, cell.Written));

    /// <summary>
    /// R4, R5: the totals rule and the OpenAI parser never hand pricing known cache counts above a known input, so input 0
    /// with 200,000 cached or 300,000 written cannot occur.
    /// </summary>
    /// <param name="input">All input tokens, or null when unknown.</param>
    /// <param name="cached">The cached part, or null when unknown.</param>
    /// <param name="written">The cache-written part, or null when unknown.</param>
    private static bool Allowed(long? input, long? cached, long? written) =>
        input is not { } known || (cached ?? 0) + (written ?? 0) <= known;

    /// <summary>Every ordered pair of replies under every rate set.</summary>
    private static IEnumerable<(Reply First, Reply Second, Rates Rates)> AllTotalsCells() =>
        from first in Enum.GetValues<Reply>()
        from second in Enum.GetValues<Reply>()
        from rates in Enum.GetValues<Rates>()
        select (first, second, rates);

    /// <summary>Written from R4, never from the code under test.</summary>
    /// <param name="input">All input tokens, or null when unknown.</param>
    /// <param name="cached">The cached part, or null when unknown.</param>
    /// <param name="written">The cache-written part, or null when unknown.</param>
    /// <param name="rates">The Advisor's rate set.</param>
    private static decimal? ExpectedPrice(long? input, long? cached, long? written, Rates rates)
    {
        // R4: an unknown input gives an unknown price.
        if (input is null) return null;

        // R4: an unknown cache count is taken as zero.
        decimal c = cached ?? 0, w = written ?? 0;
        var (uncached, read, write) = rates == Rates.SystemOne ? (0.042m, 0.042m, 0.042m) : (0.10m, 0.01m, 0.125m);
        // R4: the uncached rest at the uncached rate, cached at the read rate, written at the write rate.
        return ((input.Value - c - w) * uncached + c * read + w * write) / 1_000_000m;
    }

    /// <summary>Adds two counts, unknown only when both are.</summary>
    /// <param name="left">One count, or null when unknown.</param>
    /// <param name="right">The other count, or null when unknown.</param>
    private static long? Sum(long? left, long? right) => left is null && right is null ? null : (left ?? 0) + (right ?? 0);

    /// <summary>A fake Advisor carrying only the rate set.</summary>
    /// <param name="rates">The rate set.</param>
    private static IAdvisor Advisor(Rates rates) =>
        rates == Rates.SystemOne ? new RatedAdvisor(0.042m, 0.042m, 0.042m) : new RatedAdvisor(0.10m, 0.01m, 0.125m);

    /// <summary>An Advisor with three input rates and nothing else; it is never asked or measured.</summary>
    /// <param name="input">The uncached input rate in USD per million tokens.</param>
    /// <param name="read">The cache-read input rate in USD per million tokens.</param>
    /// <param name="write">The cache-write input rate in USD per million tokens.</param>
    private sealed class RatedAdvisor(decimal input, decimal read, decimal write) : IAdvisor
    {
        public string Id => "rated";

        public string RequestedModel => "rated";

        public AdvisorLimits Limits => throw new NotSupportedException();

        public decimal InputUsdPerMillion => input;

        public decimal CacheReadUsdPerMillion => read;

        public decimal CacheWriteUsdPerMillion => write;

        /// <summary>Never called by a pricing cell.</summary>
        /// <param name="request">Unused.</param>
        public AdviceSize Measure(AdviceRequest request) => throw new NotSupportedException();

        /// <summary>Never called by a pricing cell.</summary>
        /// <param name="request">Unused.</param>
        /// <param name="ct">Unused.</param>
        public Task<AdviceResult> AskAsync(AdviceRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}
