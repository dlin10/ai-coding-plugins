namespace Demo.Web.Cases.TimerDisposeAsyncAwaited;

/// <summary>An awaited <see cref="Timer.DisposeAsync"/> waits for the callback, so the write after it is ordered;
/// it does not wait for the work the callback dropped, whose write still overlaps.</summary>
public sealed class FlushWorker : BackgroundService
{
    public string? LastTick { get; private set; }
    public string? LastFlush { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = new Timer(OnTick, null, 0, Timeout.Infinite);
        await timer.DisposeAsync();
        LastTick = "stopped";
        LastFlush = "stopped";
    }

    private void OnTick(object? state)
    {
        LastTick = "tick";
        _ = FlushAsync();
    }

    private async Task FlushAsync()
    {
        await Task.Yield();
        LastFlush = "flush";
    }
}

public static class TimerDisposeAsyncAwaitedCase
{
    public static IServiceCollection AddTimerDisposeAsyncAwaited(this IServiceCollection services) =>
        services.AddHostedService<FlushWorker>();
}
