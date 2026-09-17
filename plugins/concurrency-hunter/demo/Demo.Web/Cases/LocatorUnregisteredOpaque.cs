using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.LocatorUnregisteredOpaque;

public sealed class Orphan
{
    public int Hits { get; set; }
}

/// <summary>A type registered nowhere and resolved through <see cref="IServiceProvider"/> has no modeled
/// instance: the analysis must not invent a shared singleton for it.</summary>
[ApiController]
[Route("cases/locator-unregistered-opaque")]
public sealed class OrphanController : ControllerBase
{
    private readonly IServiceProvider _services;

    public OrphanController(IServiceProvider services) => _services = services;

    [HttpPut]
    public void Put(int hits) => _services.GetService<Orphan>()!.Hits = hits;
}

public sealed class OrphanWorker : BackgroundService
{
    private readonly IServiceProvider _services;

    public OrphanWorker(IServiceProvider services) => _services = services;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _services.GetService<Orphan>()!.Hits = 0;
        return Task.CompletedTask;
    }
}

public static class LocatorUnregisteredOpaqueCase
{
    public static IServiceCollection AddLocatorUnregisteredOpaque(this IServiceCollection services) =>
        services.AddHostedService<OrphanWorker>();
}
