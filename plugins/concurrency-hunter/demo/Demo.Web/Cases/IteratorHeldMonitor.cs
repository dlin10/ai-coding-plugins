namespace Demo.Web.Cases.IteratorHeldMonitor;

public sealed class State
{
    public readonly object Gate = new();
    public int Value;

    public IEnumerable<int> Walk()
    {
        Monitor.Enter(Gate);
        yield return 1;
    }
}

public sealed class EnumeratingWorker : BackgroundService
{
    private readonly State _state;
    public EnumeratingWorker(State state) => _state = state;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var item in _state.Walk()) { }
        try
        {
            _state.Value = 1;
        }
        finally
        {
            Monitor.Exit(_state.Gate);
        }
        return Task.CompletedTask;
    }
}

public sealed class WritingWorker : BackgroundService
{
    private readonly State _state;
    public WritingWorker(State state) => _state = state;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        lock (_state.Gate)
            _state.Value = 2;
        return Task.CompletedTask;
    }
}

public static class IteratorHeldMonitorCase
{
    public static IServiceCollection AddIteratorHeldMonitor(this IServiceCollection services) =>
        services.AddSingleton<State>()
                .AddHostedService<EnumeratingWorker>()
                .AddHostedService<WritingWorker>();
}
