namespace Demo.Web.Cases.IteratorCreatedUnderLock;

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
        IEnumerable<int> sequence;
        lock (_state.Gate)
            sequence = _state.Walk();
        foreach (var item in sequence) { }
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

public static class IteratorCreatedUnderLockCase
{
    public static IServiceCollection AddIteratorCreatedUnderLock(this IServiceCollection services) =>
        services.AddSingleton<State>()
                .AddHostedService<EnumeratingWorker>()
                .AddHostedService<WritingWorker>();
}
