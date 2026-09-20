namespace Demo.Web.Cases.SpinLockNotProtection;

/// <summary><see cref="SpinLock"/> is not one of the modelled synchronization primitives, so both writers "under" it
/// are still reported: what is proven is the built-in coverage, not the intent of the code.</summary>
public sealed class Gauge
{
    private SpinLock _gate = new(enableThreadOwnerTracking: false);
    private int _level;

    public void Set(int level)
    {
        var taken = false;
        try
        {
            _gate.Enter(ref taken);
            _level = level;
        }
        finally
        {
            if (taken)
                _gate.Exit();
        }
    }
}

public sealed class RaiseGaugeWorker : BackgroundService
{
    private readonly Gauge _gauge;

    public RaiseGaugeWorker(Gauge gauge) => _gauge = gauge;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _gauge.Set(1);
        return Task.CompletedTask;
    }
}

public sealed class LowerGaugeWorker : BackgroundService
{
    private readonly Gauge _gauge;

    public LowerGaugeWorker(Gauge gauge) => _gauge = gauge;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _gauge.Set(-1);
        return Task.CompletedTask;
    }
}

public static class SpinLockNotProtectionCase
{
    public static IServiceCollection AddSpinLockNotProtection(this IServiceCollection services) =>
        services.AddSingleton<Gauge>()
                .AddHostedService<RaiseGaugeWorker>()
                .AddHostedService<LowerGaugeWorker>();
}
