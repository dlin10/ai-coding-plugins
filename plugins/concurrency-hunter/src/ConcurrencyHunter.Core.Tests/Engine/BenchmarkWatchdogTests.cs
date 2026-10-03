using ConcurrencyHunter.Analysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class BenchmarkWatchdogTests
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    private ulong _commit = 4UL * 1024 * 1024 * 1024;
    private long _workingSet = 1024 * 1024;

    [Fact]
    public void Heap_and_executions_each_get_their_own_ten_minutes()
    {
        using var watchdog = Watchdog();
        watchdog.Start(ScopeStep.SummariesAndFixpoint);
        _now += TimeSpan.FromMinutes(9);
        watchdog.Tick();
        Assert.False(watchdog.Token.IsCancellationRequested);
        watchdog.Start(ScopeStep.Executions);
        _now += TimeSpan.FromMinutes(9);
        watchdog.Tick();
        Assert.False(watchdog.Token.IsCancellationRequested);
        _now += TimeSpan.FromMinutes(1);
        watchdog.Tick();
        Assert.Equal("timeout", watchdog.Reason);
    }

    [Fact]
    public void Deadline_cuts_with_reason_timeout()
    {
        using var watchdog = Watchdog();
        watchdog.Start(ScopeStep.SummariesAndFixpoint);
        _now += TimeSpan.FromMinutes(10);
        watchdog.Tick();
        Assert.True(watchdog.Token.IsCancellationRequested);
        Assert.Equal("timeout", watchdog.Reason);
        _commit = 0;
        watchdog.Tick();
        Assert.Equal("timeout", watchdog.Reason);
    }

    [Fact]
    public void Low_available_commit_cuts_with_reason_memory_in_any_step()
    {
        foreach (var step in Enum.GetValues<ScopeStep>())
        {
            using var watchdog = Watchdog();
            watchdog.Start(step);
            _commit = 2UL * 1024 * 1024 * 1024;
            watchdog.Tick();
            Assert.False(watchdog.Token.IsCancellationRequested);
            _commit--;
            watchdog.Tick();
            Assert.True(watchdog.Token.IsCancellationRequested);
            Assert.Equal("memory", watchdog.Reason);
        }
    }

    [Fact]
    public void Steps_before_the_heap_have_no_deadline()
    {
        using var watchdog = Watchdog();
        foreach (var step in new[] { ScopeStep.RootDiscovery, ScopeStep.ProgramIndex, ScopeStep.ReachableSet })
        {
            watchdog.Start(step);
            _now += TimeSpan.FromDays(1);
            watchdog.Tick();
            Assert.False(watchdog.Token.IsCancellationRequested);
            Assert.Null(watchdog.Reason);
        }
    }

    [Fact]
    public void Peak_working_set_belongs_to_its_scope_window()
    {
        using (var first = Watchdog())
        {
            _workingSet = 16 * 1024 * 1024;
            first.Tick();
            _workingSet = 8 * 1024 * 1024;
            first.Tick();
            Assert.Equal(16, first.PeakWorkingSetMb);
        }

        using var second = Watchdog();
        second.Tick();
        Assert.Equal(8, second.PeakWorkingSetMb);
    }

    private BenchmarkWatchdog Watchdog() => new(() => _now, () => _commit, () => _workingSet, startTimer: false);
}
