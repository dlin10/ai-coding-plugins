namespace Demo.Web.Cases.LockIdentityThroughAlias;

/// <summary>Hard negative: two fields hold the same gate object, so a lock taken through either one excludes
/// the other writer.</summary>
public sealed class Ledger
{
    private readonly object _gate = new();
    private readonly object _second;
    private string? _lastEntry;

    public Ledger() => _second = _gate;

    public void WriteFirst(string entry)
    {
        lock (_gate)
        {
            _lastEntry = entry;
        }
    }

    public void WriteSecond(string entry)
    {
        lock (_second)
        {
            _lastEntry = entry;
        }
    }
}

public sealed class FirstLedgerWorker : BackgroundService
{
    private readonly Ledger _ledger;

    public FirstLedgerWorker(Ledger ledger) => _ledger = ledger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ledger.WriteFirst("first");
        return Task.CompletedTask;
    }
}

public sealed class SecondLedgerWorker : BackgroundService
{
    private readonly Ledger _ledger;

    public SecondLedgerWorker(Ledger ledger) => _ledger = ledger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ledger.WriteSecond("second");
        return Task.CompletedTask;
    }
}

public static class LockIdentityThroughAliasCase
{
    public static IServiceCollection AddLockIdentityThroughAlias(this IServiceCollection services) =>
        services.AddSingleton<Ledger>()
                .AddHostedService<FirstLedgerWorker>()
                .AddHostedService<SecondLedgerWorker>();
}
