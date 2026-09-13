using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.StaticFieldHttpVsWorker;

public static class Heartbeat
{
    internal static string? LastBeat;
}

/// <summary>A static shared across two root providers: the hosted service writes once, actions only read,
/// so the only pair is worker against action.</summary>
public sealed class HeartbeatWorker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Heartbeat.LastBeat = "worker";
        return Task.CompletedTask;
    }
}

[ApiController]
[Route("cases/static-field-http-vs-worker")]
public sealed class HeartbeatController : ControllerBase
{
    [HttpGet]
    public string? Get() => Heartbeat.LastBeat;
}

public static class StaticFieldHttpVsWorkerCase
{
    public static IServiceCollection AddStaticFieldHttpVsWorker(this IServiceCollection services) =>
        services.AddHostedService<HeartbeatWorker>();
}
