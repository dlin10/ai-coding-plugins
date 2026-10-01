using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Demo.Web.Cases.ProjectModelTryGetValue;

public sealed class Session
{
    public int Hits;
}

public sealed class SessionWorker(IMemoryCache cache) : BackgroundService
{
    private readonly IMemoryCache _cache = cache;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _cache.Set("session", new Session());
        return Task.CompletedTask;
    }
}

[ApiController]
[Route("cases/project-model-try-get-value")]
public sealed class SessionController(IMemoryCache cache) : ControllerBase
{
    private readonly IMemoryCache _cache = cache;

    [HttpPost]
    public void Post()
    {
        if (_cache.TryGetValue("session", out Session? session))
            session!.Hits++;
    }
}

public static class ProjectModelTryGetValueCase
{
    public static IServiceCollection AddProjectModelTryGetValue(this IServiceCollection services) =>
        services.AddMemoryCache().AddHostedService<SessionWorker>();
}
