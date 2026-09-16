using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.HostedConstructorBeforeRoots;

public static class PrimedCache
{
    public static string? Region;
}

/// <summary>Hard negative: a hosted service's constructor runs when the host starts, before any root, so its
/// write to a static precedes every read by an action (ADR 0006).</summary>
public sealed class PrimingWorker : BackgroundService
{
    public PrimingWorker() => PrimedCache.Region = "eu";

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}

[ApiController]
[Route("cases/hosted-constructor-before-roots")]
public sealed class PrimedCacheController : ControllerBase
{
    [HttpGet]
    public string? Get() => PrimedCache.Region;
}

public static class HostedConstructorBeforeRootsCase
{
    public static IServiceCollection AddHostedConstructorBeforeRoots(this IServiceCollection services) =>
        services.AddHostedService<PrimingWorker>();
}
