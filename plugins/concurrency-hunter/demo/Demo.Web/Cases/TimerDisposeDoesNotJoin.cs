namespace Demo.Web.Cases.TimerDisposeDoesNotJoin;

/// <summary><see cref="Timer.Dispose()"/> does not wait for a running callback, so the write after it overlaps
/// the callback's write.</summary>
public sealed class StopWorker : BackgroundService
{
    public string? LastRun { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = new Timer(OnTick, null, 0, Timeout.Infinite);
        timer.Dispose();
        LastRun = "stopped";
        return Task.CompletedTask;
    }

    private void OnTick(object? state) => LastRun = "tick";
}

public static class TimerDisposeDoesNotJoinCase
{
    public static IServiceCollection AddTimerDisposeDoesNotJoin(this IServiceCollection services) =>
        services.AddHostedService<StopWorker>();
}
