using System.Collections.Concurrent;
using System.Net;
using PlanForge.Advisors.MicrosoftDecision;
using Xunit;

namespace PlanForge.Tests;

/// <summary>The client-side mirror of the deployment's request bucket, on a manual clock.</summary>
public sealed class QuotaPacerTests
{
    /// <summary>The fraction of the reported refill rate the pacer spends.</summary>
    private const double SAFETY = 0.9;

    /// <summary>The manual clock the bucket refills by.</summary>
    private readonly MapperClock _clock = new();

    /// <summary>Every wait the pacer asked for, in order.</summary>
    private readonly ConcurrentQueue<TimeSpan> _waits = new();

    /// <summary>Before any report the pacer assumes the smallest tier: one start a second, with no burst.</summary>
    [Fact]
    public async Task A_cold_pacer_spaces_starts_by_the_smallest_tier()
    {
        var pacer = Pacer();
        for (var start = 0; start < 3; start++) await pacer.WaitAsync(CancellationToken.None);
        Assert.Equal([Seconds(1, 1), Seconds(2, 1)], _waits);
    }

    /// <summary>The first report opens the bucket it describes, so its remaining starts need no wait.</summary>
    [Fact]
    public async Task A_reported_bucket_lets_its_remaining_requests_start_at_once()
    {
        var pacer = Pacer();
        await pacer.WaitAsync(CancellationToken.None);
        pacer.Observe(Response(200, 60, 60, 59));
        for (var start = 0; start < 59; start++) await pacer.WaitAsync(CancellationToken.None);
        Assert.Empty(_waits);
        await pacer.WaitAsync(CancellationToken.None);
        Assert.Equal([Seconds(1, 1)], _waits);
    }

    /// <summary>Starts reserved against the cold guess are still owed once the real bucket is known.</summary>
    [Fact]
    public async Task The_first_report_keeps_the_starts_already_reserved()
    {
        var pacer = Pacer();
        for (var start = 0; start < 3; start++) await pacer.WaitAsync(CancellationToken.None);
        pacer.Observe(Response(200, 60, 60, 59));
        _waits.Clear();
        for (var start = 0; start < 57; start++) await pacer.WaitAsync(CancellationToken.None);
        Assert.Empty(_waits);
        await pacer.WaitAsync(CancellationToken.None);
        Assert.Equal([Seconds(1, 1)], _waits);
    }

    /// <summary>A larger quota in the headers refills faster, with no configuration.</summary>
    [Fact]
    public async Task A_raised_quota_shortens_the_spacing()
    {
        var pacer = Pacer();
        await pacer.WaitAsync(CancellationToken.None);
        pacer.Observe(Response(200, 900, 60, 0));
        await pacer.WaitAsync(CancellationToken.None);
        Assert.Equal([Seconds(1, 15)], _waits);
    }

    /// <summary>After the first report the service can lower the estimate, but only time raises it.</summary>
    [Fact]
    public async Task A_later_report_lowers_but_never_raises_the_estimate()
    {
        var pacer = Pacer();
        await pacer.WaitAsync(CancellationToken.None);
        pacer.Observe(Response(200, 60, 60, 5));
        pacer.Observe(Response(200, 60, 60, 59));
        for (var start = 0; start < 5; start++) await pacer.WaitAsync(CancellationToken.None);
        Assert.Empty(_waits);
        pacer.Observe(Response(200, 60, 60, 2));
        await pacer.WaitAsync(CancellationToken.None);
        Assert.Equal([Seconds(1, 1)], _waits);
    }

    /// <summary>A 429 proves the service's bucket empty, whatever the estimate and whether or not it has headers.</summary>
    [Fact]
    public async Task A_429_empties_the_bucket_even_without_headers()
    {
        var pacer = Pacer();
        await pacer.WaitAsync(CancellationToken.None);
        pacer.Observe(Response(200, 60, 60, 59));
        pacer.Observe(Response(429));
        await pacer.WaitAsync(CancellationToken.None);
        Assert.Equal([Seconds(1, 1)], _waits);
    }

    /// <summary>Idle time refills the bucket, never beyond its reported size.</summary>
    [Fact]
    public async Task Idle_time_refills_the_bucket_up_to_its_size()
    {
        var pacer = Pacer();
        await pacer.WaitAsync(CancellationToken.None);
        pacer.Observe(Response(200, 60, 60, 0));
        _clock.Advance(TimeSpan.FromHours(1));
        for (var start = 0; start < 60; start++) await pacer.WaitAsync(CancellationToken.None);
        Assert.Empty(_waits);
        await pacer.WaitAsync(CancellationToken.None);
        Assert.Equal([Seconds(1, 1)], _waits);
    }

    /// <summary>A start whose wait is cancelled goes back to the bucket, so it costs the next caller nothing.</summary>
    [Fact]
    public async Task A_cancelled_wait_returns_its_start()
    {
        var pacer = Pacer();
        await pacer.WaitAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pacer.WaitAsync(cancellation.Token));
        _waits.Clear();
        await pacer.WaitAsync(CancellationToken.None);
        Assert.Equal([Seconds(1, 1)], _waits);
    }

    /// <summary>Headers that do not describe a bucket leave the pacer cold.</summary>
    /// <param name="limit">The limit header value.</param>
    /// <param name="period">The renewal period header value.</param>
    /// <param name="remaining">The remaining header value.</param>
    [Theory]
    [InlineData("abc", "60", "59")]
    [InlineData("60", "0", "59")]
    [InlineData("0", "60", "0")]
    [InlineData("60", "60", "61")]
    [InlineData("60", "60", "-1")]
    [InlineData("60", "60", null)]
    public async Task Headers_that_describe_no_bucket_are_ignored(string limit, string period, string? remaining)
    {
        var pacer = Pacer();
        await pacer.WaitAsync(CancellationToken.None);
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("x-ratelimit-limit-requests", limit);
        response.Headers.TryAddWithoutValidation("x-ratelimit-renewalperiod-requests", period);
        if (remaining is not null) response.Headers.TryAddWithoutValidation("x-ratelimit-remaining-requests", remaining);
        pacer.Observe(response);
        await pacer.WaitAsync(CancellationToken.None);
        Assert.Equal([Seconds(1, 1)], _waits);
    }

    /// <summary>A reported refill of decades still yields a wait the system's delay accepts.</summary>
    [Fact]
    public async Task An_absurd_period_waits_at_most_a_day()
    {
        var pacer = Pacer();
        await pacer.WaitAsync(CancellationToken.None);
        pacer.Observe(Response(200, 1, int.MaxValue, 0));
        await pacer.WaitAsync(CancellationToken.None);
        Assert.Equal([TimeSpan.FromDays(1)], _waits);
    }

    /// <summary>Concurrent callers each reserve a start of their own.</summary>
    [Fact]
    public async Task Concurrent_callers_get_distinct_starts()
    {
        var pacer = Pacer();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => pacer.WaitAsync(CancellationToken.None))));
        Assert.Equal(Enumerable.Range(1, 99).Select(start => Seconds(start, 1)), _waits.Order());
    }

    /// <summary>Creates a cold pacer whose waits are recorded instead of slept.</summary>
    private QuotaPacer Pacer() => new(_clock, (wait, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        _waits.Enqueue(wait);
        return Task.CompletedTask;
    });

    /// <summary>The wait for the given number of queued starts at a refill rate, as the pacer computes it.</summary>
    /// <param name="starts">The starts ahead of the caller, its own included.</param>
    /// <param name="perSecond">The reported refill rate.</param>
    private static TimeSpan Seconds(int starts, double perSecond) => TimeSpan.FromSeconds(starts / (perSecond * SAFETY));

    /// <summary>Creates a response reporting a bucket, or none when the counts are omitted.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="limit">The bucket size.</param>
    /// <param name="period">The renewal period in seconds.</param>
    /// <param name="remaining">The requests left.</param>
    private static HttpResponseMessage Response(int status, int? limit = null, int? period = null, int? remaining = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status);
        if (limit is not null) response.Headers.Add("x-ratelimit-limit-requests", limit.ToString());
        if (period is not null) response.Headers.Add("x-ratelimit-renewalperiod-requests", period.ToString());
        if (remaining is not null) response.Headers.Add("x-ratelimit-remaining-requests", remaining.ToString());
        return response;
    }
}
