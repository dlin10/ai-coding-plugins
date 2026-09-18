namespace Demo.Web.Cases.TimerDisposeWaitHandle;

/// <summary><see cref="Timer.Dispose(WaitHandle)"/> signals the handle once the callback is done: the write
/// before the wait overlaps the callback, the write after it is ordered.</summary>
public sealed class DrainWorker : BackgroundService
{
    public string? BeforeWait { get; private set; }
    public string? AfterWait { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = new Timer(OnTick, null, 0, Timeout.Infinite);
        var handle = new ManualResetEvent(false);
        timer.Dispose(handle);
        BeforeWait = "stopping";
        handle.WaitOne();
        AfterWait = "stopped";
        return Task.CompletedTask;
    }

    private void OnTick(object? state)
    {
        BeforeWait = "tick";
        AfterWait = "tick";
    }
}

public static class TimerDisposeWaitHandleCase
{
    public static IServiceCollection AddTimerDisposeWaitHandle(this IServiceCollection services) =>
        services.AddHostedService<DrainWorker>();
}
