namespace Demo.Web.Cases.IteratorEscapesToField;

public sealed class State
{
    public IEnumerable<int>? Escaped;
    public int Value;

    public IEnumerable<int> Walk()
    {
        Value = 1;
        yield return 1;
    }
}

public sealed class StoringWorker : BackgroundService
{
    private readonly State _state;
    public StoringWorker(State state) => _state = state;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _state.Escaped = _state.Walk();
        return Task.CompletedTask;
    }
}

public sealed class OtherWorker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}

public static class IteratorEscapesToFieldCase
{
    public static IServiceCollection AddIteratorEscapesToField(this IServiceCollection services) =>
        services.AddSingleton<State>()
                .AddHostedService<StoringWorker>()
                .AddHostedService<OtherWorker>();
}
