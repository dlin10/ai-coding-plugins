namespace ConcurrencyHunter.Cli.Tests;

internal sealed class ManualTimeProvider(DateTimeOffset initialTime) : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = initialTime;

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    internal void Advance(TimeSpan amount)
    {
        _now += amount;
        foreach (var timer in _timers.ToArray())
            timer.FireIfDue(_now);
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? _dueAt;
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

        internal void FireIfDue(DateTimeOffset now)
        {
            if (_disposed || _dueAt is null || now < _dueAt)
                return;
            _dueAt = null;
            callback(state);
        }
    }
}
