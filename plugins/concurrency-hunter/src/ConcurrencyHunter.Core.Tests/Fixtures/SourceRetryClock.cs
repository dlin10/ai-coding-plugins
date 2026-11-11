using System.Threading.Channels;

namespace ConcurrencyHunter.Core.Tests.Fixtures;

internal sealed class SourceRetryClock(bool autoAdvance = true) : TimeProvider
{
    private readonly FakeClock _clock = new();
    private readonly Channel<TimeSpan> _delays = Channel.CreateUnbounded<TimeSpan>();

    public override DateTimeOffset GetUtcNow() { lock (_clock) return _clock.GetUtcNow(); }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_clock)
        {
            var timer = _clock.CreateTimer(callback, state, dueTime, period);
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                _delays.Writer.TryWrite(dueTime);
                if (autoAdvance) _clock.Advance(dueTime);
            }
            return timer;
        }
    }

    internal Task<TimeSpan> NextDelayAsync() => _delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

    internal void Advance(TimeSpan amount) { lock (_clock) _clock.Advance(amount); }
}
