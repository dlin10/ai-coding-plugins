namespace ConcurrencyHunter.Core.Tests.Fixtures;

/// <summary>A clock that moves only when a test moves it, and fires the timers that fall due.</summary>
public sealed class FakeClock : TimeProvider
{
    private readonly List<FakeTimer> _timers = [];
    private TimeSpan _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _now.Ticks;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan amount)
    {
        _now += amount;
        foreach (var timer in _timers.ToArray())
            timer.FireIfDue();
    }

    private sealed class FakeTimer(FakeClock owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan? _dueAt;
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
                return false;
            _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }

        public void Dispose()
        {
            _disposed = true;
            _dueAt = null;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void FireIfDue()
        {
            if (_disposed || _dueAt is null || owner._now < _dueAt)
                return;
            _dueAt = null;
            callback(state);
        }
    }
}
