namespace Demo.Web.Cases.RefLocalWrite;

public sealed class Counter
{
    public int Value;
}

public sealed class RefWorker : BackgroundService
{
    private readonly Counter _counter;
    public RefWorker(Counter counter) => _counter = counter;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ref int value = ref _counter.Value;
        value = 1;
        return Task.CompletedTask;
    }
}

public sealed class IncrementWorker : BackgroundService
{
    private readonly Counter _counter;
    public IncrementWorker(Counter counter) => _counter = counter;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _counter.Value++;
        return Task.CompletedTask;
    }
}

public static class RefLocalWriteCase
{
    public static IServiceCollection AddRefLocalWrite(this IServiceCollection services) =>
        services.AddSingleton<Counter>()
                .AddHostedService<RefWorker>()
                .AddHostedService<IncrementWorker>();
}
