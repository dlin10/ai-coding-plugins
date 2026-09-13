using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.EscapeIntoSingleton;

public sealed class Draft
{
    public string? Body { get; set; }
}

public sealed class DraftRegistry
{
    public Draft? Current { get; set; }
}

/// <summary>A fresh object published into a singleton before it is filled in. The allocation runs in the
/// single hosted-service instance, so the only pairs are worker against action, and both are real.</summary>
public sealed class DraftWorker : BackgroundService
{
    private readonly DraftRegistry _registry;

    public DraftWorker(DraftRegistry registry) => _registry = registry;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var draft = new Draft();
        _registry.Current = draft;
        draft.Body = "worker";
        return Task.CompletedTask;
    }
}

[ApiController]
[Route("cases/escape-into-singleton")]
public sealed class DraftsController : ControllerBase
{
    private readonly DraftRegistry _registry;

    public DraftsController(DraftRegistry registry) => _registry = registry;

    [HttpGet]
    public string? Get() => _registry.Current!.Body;
}

public static class EscapeIntoSingletonCase
{
    public static IServiceCollection AddEscapeIntoSingleton(this IServiceCollection services) =>
        services.AddSingleton<DraftRegistry>()
                .AddHostedService<DraftWorker>();
}
