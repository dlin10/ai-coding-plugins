using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.DiInstanceRegistration;

public sealed class PriceList
{
    public string? Currency { get; set; }
}

/// <summary>A singleton registered as a ready instance is shared like any other: a hosted service writes
/// it while an action reads it.</summary>
public sealed class PriceListWorker : BackgroundService
{
    private readonly PriceList _list;

    public PriceListWorker(PriceList list) => _list = list;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _list.Currency = "EUR";
        return Task.CompletedTask;
    }
}

[ApiController]
[Route("cases/di-instance-registration")]
public sealed class PriceListController : ControllerBase
{
    private readonly PriceList _list;

    public PriceListController(PriceList list) => _list = list;

    [HttpGet]
    public string? Get() => _list.Currency;
}

public static class DiInstanceRegistrationCase
{
    public static IServiceCollection AddDiInstanceRegistration(this IServiceCollection services) =>
        services.AddSingleton(new PriceList())
                .AddHostedService<PriceListWorker>();
}
