using System.Diagnostics;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The member limit (ADR 0033): the CPU time the member's thread spends, a backstop of four limits on the clock for a member that
/// waits, and the system clock's reading of the calling thread's own CPU time.</summary>
public sealed class MemberLimitTests
{
    private static readonly TimeSpan LIMIT = TimeSpan.FromSeconds(10);

    [Fact]
    public void The_limit_counts_the_cpu_time_spent_not_the_time_passed()
    {
        var clock = new FakeClock();
        var spent = TimeSpan.Zero;
        using var member = new CancellationTokenSource();
        using var limit = new MemberLimit(member, LIMIT, clock, () => spent);

        // A whole limit passes on the clock with half of it spent: the limit is read again when the rest could be spent.
        spent = LIMIT / 2;
        clock.Advance(LIMIT);
        Assert.False(member.IsCancellationRequested);
        spent = LIMIT - TimeSpan.FromSeconds(1);
        clock.Advance(LIMIT / 2);
        Assert.False(member.IsCancellationRequested);

        spent = LIMIT;
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(member.IsCancellationRequested);
        Assert.True(limit.Reached);
    }

    [Fact]
    public void A_member_that_waits_without_spending_cpu_time_is_cut_at_four_limits()
    {
        var clock = new FakeClock();
        using var member = new CancellationTokenSource();
        using var limit = new MemberLimit(member, LIMIT, clock, () => TimeSpan.Zero);

        clock.Advance(4 * LIMIT - TimeSpan.FromSeconds(1));
        Assert.False(member.IsCancellationRequested);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(member.IsCancellationRequested);
        Assert.True(limit.Reached);
    }

    [Fact]
    public void The_system_clock_counts_the_calling_threads_own_cpu_time()
    {
        using var member = new CancellationTokenSource();
        using var limit = new MemberLimit(member, TimeSpan.FromMilliseconds(300), TimeProvider.System);

        // Sleeping spends no CPU time: twice the limit passes, and the member is not cut.
        Thread.Sleep(600);
        Assert.False(member.IsCancellationRequested);

        // Spinning spends it, and the limit cuts the member.
        var spinning = Stopwatch.StartNew();
        while (!member.IsCancellationRequested && spinning.Elapsed < TimeSpan.FromSeconds(30))
            Thread.SpinWait(1_000);
        Assert.True(limit.Reached);
    }
}
