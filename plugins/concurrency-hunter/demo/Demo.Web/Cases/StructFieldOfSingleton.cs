namespace Demo.Web.Cases.StructFieldOfSingleton;

public struct Counter
{
    public int Count;
}

/// <summary>The struct lives inline in the singleton's field, so its field is the singleton's storage: two workers
/// incrementing it lose updates.</summary>
public sealed class Tally
{
    private Counter _counter;

    public void Add() => _counter.Count++;
}

public sealed class FirstWorker : BackgroundService
{
    private readonly Tally _tally;
    public FirstWorker(Tally tally) => _tally = tally;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _tally.Add();
        return Task.CompletedTask;
    }
}

public sealed class SecondWorker : BackgroundService
{
    private readonly Tally _tally;
    public SecondWorker(Tally tally) => _tally = tally;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _tally.Add();
        return Task.CompletedTask;
    }
}

public static class StructFieldOfSingletonCase
{
    public static IServiceCollection AddStructFieldOfSingleton(this IServiceCollection services) =>
        services.AddSingleton<Tally>()
                .AddHostedService<FirstWorker>()
                .AddHostedService<SecondWorker>();
}
