namespace Demo.Web.Cases.SemaphoreSlimCapacityOne;

/// <summary>A <see cref="SemaphoreSlim"/> constructed with a constant capacity of one, waited on before every write and
/// released in `finally`: that is a mutex, so the two workers exclude each other.</summary>
public sealed class Airlock
{
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public string? Occupant { get; set; }
}

public sealed class InnerDoorWorker : BackgroundService
{
    private readonly Airlock _airlock;

    public InnerDoorWorker(Airlock airlock) => _airlock = airlock;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _airlock.Gate.WaitAsync(stoppingToken);
        try
        {
            _airlock.Occupant = "inner";
        }
        finally
        {
            _airlock.Gate.Release();
        }
    }
}

public sealed class OuterDoorWorker : BackgroundService
{
    private readonly Airlock _airlock;

    public OuterDoorWorker(Airlock airlock) => _airlock = airlock;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _airlock.Gate.WaitAsync(stoppingToken);
        try
        {
            _airlock.Occupant = "outer";
        }
        finally
        {
            _airlock.Gate.Release();
        }
    }
}

public static class SemaphoreSlimCapacityOneCase
{
    public static IServiceCollection AddSemaphoreSlimCapacityOne(this IServiceCollection services) =>
        services.AddSingleton<Airlock>()
                .AddHostedService<InnerDoorWorker>()
                .AddHostedService<OuterDoorWorker>();
}
