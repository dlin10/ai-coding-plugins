using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.DiSingletonControllerVsWorker;

public sealed class VisitorStats
{
    public string? LastPath { get; set; }
}

/// <summary>A DI singleton written by an action, which overlaps itself, and by a hosted service.</summary>
[ApiController]
[Route("cases/di-singleton-controller-vs-worker")]
public sealed class VisitsController : ControllerBase
{
    private readonly VisitorStats _stats;

    public VisitsController(VisitorStats stats) => _stats = stats;

    [HttpPost]
    public void Post(string path) => _stats.LastPath = path;
}

public sealed class StatsResetWorker : BackgroundService
{
    private readonly VisitorStats _stats;

    public StatsResetWorker(VisitorStats stats) => _stats = stats;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stats.LastPath = null;
        return Task.CompletedTask;
    }
}

public static class DiSingletonControllerVsWorkerCase
{
    public static IServiceCollection AddDiSingletonControllerVsWorker(this IServiceCollection services) =>
        services.AddSingleton<VisitorStats>()
                .AddHostedService<StatsResetWorker>();
}
