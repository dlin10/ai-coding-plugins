namespace Demo.Web.Cases.TimerChangeReactivates;

/// <summary>A <see cref="Timer"/> created disabled becomes periodic through <see cref="Timer.Change(int, int)"/>,
/// so its callbacks overlap each other on one counter.</summary>
public sealed class PollWorker : BackgroundService
{
    private Timer? _timer;
    private int _polls;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
        timer.Change(0, 1000);
        _timer = timer;
        return Task.CompletedTask;
    }

    private void OnTick(object? state) => _polls++;
}

public static class TimerChangeReactivatesCase
{
    public static IServiceCollection AddTimerChangeReactivates(this IServiceCollection services) =>
        services.AddHostedService<PollWorker>();
}
