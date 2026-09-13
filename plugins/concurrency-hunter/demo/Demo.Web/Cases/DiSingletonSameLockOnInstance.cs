using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.DiSingletonSameLockOnInstance;

public sealed class Ledger
{
    public string? LastEntry { get; set; }
}

/// <summary>Hard negative: the controller-versus-worker race, with both sides locking the singleton itself.</summary>
[ApiController]
[Route("cases/di-singleton-same-lock-on-instance")]
public sealed class LedgerController : ControllerBase
{
    private readonly Ledger _ledger;

    public LedgerController(Ledger ledger) => _ledger = ledger;

    [HttpPost]
    public void Post(string entry)
    {
        lock (_ledger)
        {
            _ledger.LastEntry = entry;
        }
    }
}

public sealed class LedgerResetWorker : BackgroundService
{
    private readonly Ledger _ledger;

    public LedgerResetWorker(Ledger ledger) => _ledger = ledger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        lock (_ledger)
        {
            _ledger.LastEntry = null;
        }
        return Task.CompletedTask;
    }
}

public static class DiSingletonSameLockOnInstanceCase
{
    public static IServiceCollection AddDiSingletonSameLockOnInstance(this IServiceCollection services) =>
        services.AddSingleton<Ledger>()
                .AddHostedService<LedgerResetWorker>();
}
