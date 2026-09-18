namespace Demo.Web.Cases.ThreadingTimerSelfOverlap;

/// <summary>The callbacks of a periodic <see cref="Timer"/> overlap each other and each increments one
/// field.</summary>
public sealed class PulseWorker : BackgroundService
{
    private Timer? _timer;
    private int _ticks;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _timer = new Timer(OnTick, null, 0, 1000);
        return Task.CompletedTask;
    }

    private void OnTick(object? state) => _ticks++;
}

public static class ThreadingTimerSelfOverlapCase
{
    public static IServiceCollection AddThreadingTimerSelfOverlap(this IServiceCollection services) =>
        services.AddHostedService<PulseWorker>();
}
