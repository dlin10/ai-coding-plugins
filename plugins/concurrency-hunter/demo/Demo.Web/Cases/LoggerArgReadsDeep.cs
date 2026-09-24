using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.LoggerArgReadsDeep;

/// <summary>A singleton handed to a message template as an argument: the logger formats it, which reads every field it reaches.</summary>
public sealed class Basket
{
    public int Items;
}

public sealed class BasketWorker : BackgroundService
{
    private readonly Basket _basket;

    public BasketWorker(Basket basket) => _basket = basket;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _basket.Items = 3;
        return Task.CompletedTask;
    }
}

[ApiController]
[Route("cases/logger-arg-reads-deep")]
public sealed class BasketController : ControllerBase
{
    private readonly Basket _basket;
    private readonly ILogger<BasketController> _logger;

    public BasketController(Basket basket, ILogger<BasketController> logger)
    {
        _basket = basket;
        _logger = logger;
    }

    [HttpGet]
    public void Get() => _logger.LogInformation("Basket {Basket}", _basket);
}

public static class LoggerArgReadsDeepCase
{
    public static IServiceCollection AddLoggerArgReadsDeep(this IServiceCollection services) =>
        services.AddSingleton<Basket>()
                .AddHostedService<BasketWorker>();
}
