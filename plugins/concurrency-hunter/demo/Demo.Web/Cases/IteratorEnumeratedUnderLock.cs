namespace Demo.Web.Cases.IteratorEnumeratedUnderLock;

public sealed class State
{
    public readonly object Gate = new();
    public int Value;

    public IEnumerable<int> Walk()
    {
        Value = 1;
        yield return 1;
    }
}

public sealed class EnumeratingWorker : BackgroundService
{
    private readonly State _state;
    public EnumeratingWorker(State state) => _state = state;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sequence = _state.Walk();
        lock (_state.Gate)
        {
            foreach (var item in sequence) { }
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

public static class IteratorEnumeratedUnderLockCase
{
    public static IServiceCollection AddIteratorEnumeratedUnderLock(this IServiceCollection services) =>
        services.AddSingleton<State>()
                .AddHostedService<EnumeratingWorker>()
                .AddHostedService<WritingWorker>();
}
