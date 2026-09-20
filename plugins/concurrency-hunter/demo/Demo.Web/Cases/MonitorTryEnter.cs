namespace Demo.Web.Cases.MonitorTryEnter;

/// <summary><see cref="Monitor.TryEnter(object)"/> holds the monitor only where its result says it does: the first field is
/// written inside the branch that checked it, the second after a call whose result nobody looked at.</summary>
public sealed class Slotboard
{
    private readonly object _gate = new();
    private string? _guarded;
    private string? _unguarded;

    public void SetGuardedWithTryEnter(string value)
    {
        if (Monitor.TryEnter(_gate))
        {
            try
            {
                _guarded = value;
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }
    }

    public void SetGuardedWithLock(string value)
    {
        lock (_gate)
            _guarded = value;
    }

    public void SetUnguarded(string value)
    {
        Monitor.TryEnter(_gate);
        _unguarded = value;
        Monitor.Exit(_gate);
    }

    public void SetUnguardedWithLock(string value)
    {
        lock (_gate)
            _unguarded = value;
    }
}

public sealed class TryEnterWorker : BackgroundService
{
    private readonly Slotboard _slotboard;

    public TryEnterWorker(Slotboard slotboard) => _slotboard = slotboard;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _slotboard.SetGuardedWithTryEnter("try-enter");
        _slotboard.SetUnguarded("try-enter");
        return Task.CompletedTask;
    }
}

public sealed class LockingSlotWorker : BackgroundService
{
    private readonly Slotboard _slotboard;

    public LockingSlotWorker(Slotboard slotboard) => _slotboard = slotboard;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _slotboard.SetGuardedWithLock("lock");
        _slotboard.SetUnguardedWithLock("lock");
        return Task.CompletedTask;
    }
}

public static class MonitorTryEnterCase
{
    public static IServiceCollection AddMonitorTryEnter(this IServiceCollection services) =>
        services.AddSingleton<Slotboard>()
                .AddHostedService<TryEnterWorker>()
                .AddHostedService<LockingSlotWorker>();
}
