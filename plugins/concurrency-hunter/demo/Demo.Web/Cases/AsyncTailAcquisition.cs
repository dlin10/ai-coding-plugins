namespace Demo.Web.Cases.AsyncTailAcquisition;

public sealed class State
{
    public readonly SemaphoreSlim Gate = new(1, 1);
    public int Value;

    public async Task EnterAsync()
    {
        await Task.Yield();
        Gate.Wait();
    }
}

public sealed class SpawningWorker : BackgroundService
{
    private readonly State _state;
    public SpawningWorker(State state) => _state = state;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = _state.EnterAsync();
        try
        {
            _state.Value = 1;
        }
        finally
        {
            _state.Gate.Release();
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
        _state.Gate.Wait();
        try
        {
            _state.Value = 2;
        }
        finally
        {
            _state.Gate.Release();
        }
        return Task.CompletedTask;
    }
}

public static class AsyncTailAcquisitionCase
{
    public static IServiceCollection AddAsyncTailAcquisition(this IServiceCollection services) =>
        services.AddSingleton<State>()
                .AddHostedService<SpawningWorker>()
                .AddHostedService<WritingWorker>();
}
