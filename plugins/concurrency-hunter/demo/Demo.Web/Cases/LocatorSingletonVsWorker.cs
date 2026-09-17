using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.LocatorSingletonVsWorker;

public sealed class HitCounter
{
    public int Value { get; set; }
}

/// <summary>A singleton resolved through <see cref="IServiceProvider"/> is the same shared instance as an
/// injected one: the action and the hosted service write it.</summary>
[ApiController]
[Route("cases/locator-singleton-vs-worker")]
public sealed class HitController : ControllerBase
{
    private readonly IServiceProvider _services;

    public HitController(IServiceProvider services) => _services = services;

    [HttpPut]
    public void Put(int value) => _services.GetRequiredService<HitCounter>().Value = value;
}

public sealed class HitWorker : BackgroundService
{
    private readonly IServiceProvider _services;

    public HitWorker(IServiceProvider services) => _services = services;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _services.GetRequiredService<HitCounter>().Value = 0;
        return Task.CompletedTask;
    }
}

public static class LocatorSingletonVsWorkerCase
{
    public static IServiceCollection AddLocatorSingletonVsWorker(this IServiceCollection services) =>
        services.AddSingleton<HitCounter>()
                .AddHostedService<HitWorker>();
}
