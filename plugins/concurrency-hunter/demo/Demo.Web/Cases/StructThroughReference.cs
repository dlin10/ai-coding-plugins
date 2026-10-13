namespace Demo.Web.Cases.StructThroughReference;

public struct Balance
{
    public long Amount;
}

/// <summary>The singleton's struct field reached by reference two ways: a <c>ref</c> parameter and a <c>ref</c>
/// local bound to a <c>ref</c> return. Both increment the field's storage itself.</summary>
public sealed class Ledger
{
    private Balance _balance;

    public void Deposit() => Bump(ref _balance);

    public void Credit()
    {
        ref var b = ref Slot();
        b.Amount++;
    }

    private static void Bump(ref Balance b) { b.Amount++; }

    private ref Balance Slot() => ref _balance;
}

public sealed class DepositWorker : BackgroundService
{
    private readonly Ledger _ledger;
    public DepositWorker(Ledger ledger) => _ledger = ledger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ledger.Deposit();
        return Task.CompletedTask;
    }
}

public sealed class CreditWorker : BackgroundService
{
    private readonly Ledger _ledger;
    public CreditWorker(Ledger ledger) => _ledger = ledger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ledger.Credit();
        return Task.CompletedTask;
    }
}

public static class StructThroughReferenceCase
{
    public static IServiceCollection AddStructThroughReference(this IServiceCollection services) =>
        services.AddSingleton<Ledger>()
                .AddHostedService<DepositWorker>()
                .AddHostedService<CreditWorker>();
}
