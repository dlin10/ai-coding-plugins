using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.FactoryResolvesOtherService;

public interface ILedgerView
{
    void Post(int amount);
}

public sealed class Ledger : ILedgerView
{
    public int Balance { get; set; }

    public void Post(int amount) => Balance = amount;
}

/// <summary>An interface singleton whose factory resolves another singleton is that singleton: the action
/// writes it through the interface and the worker writes it directly.</summary>
[ApiController]
[Route("cases/factory-resolves-other-service")]
public sealed class LedgerController : ControllerBase
{
    private readonly ILedgerView _view;

    public LedgerController(ILedgerView view) => _view = view;

    [HttpPost]
    public void Post(int amount) => _view.Post(amount);
}

public sealed class LedgerWorker : BackgroundService
{
    private readonly Ledger _ledger;

    public LedgerWorker(Ledger ledger) => _ledger = ledger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ledger.Balance = 0;
        return Task.CompletedTask;
    }
}

public static class FactoryResolvesOtherServiceCase
{
    public static IServiceCollection AddFactoryResolvesOtherService(this IServiceCollection services) =>
        services.AddSingleton<Ledger>()
                .AddSingleton<ILedgerView>(sp => sp.GetRequiredService<Ledger>())
                .AddHostedService<LedgerWorker>();
}
