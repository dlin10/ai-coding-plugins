namespace Demo.Web.Cases.InterlockedMixedWithPlainWrite;

/// <summary><see cref="Interlocked"/> makes one side atomic, never the pair: the worker that simply assigns the counter still
/// races with the atomic increment, and one of the two updates is lost.</summary>
public sealed class Meter
{
    public int Count;
}

public sealed class CountingWorker : BackgroundService
{
    private readonly Meter _meter;

    public CountingWorker(Meter meter) => _meter = meter;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Interlocked.Increment(ref _meter.Count);
        return Task.CompletedTask;
    }
}

public sealed class MeterResetWorker : BackgroundService
{
    private readonly Meter _meter;

    public MeterResetWorker(Meter meter) => _meter = meter;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _meter.Count = 0;
        return Task.CompletedTask;
    }
}

public static class InterlockedMixedWithPlainWriteCase
{
    public static IServiceCollection AddInterlockedMixedWithPlainWrite(this IServiceCollection services) =>
        services.AddSingleton<Meter>()
                .AddHostedService<CountingWorker>()
                .AddHostedService<MeterResetWorker>();
}
