using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.MonitorEnterExitSameGate;

internal static class WriterLog
{
    internal static readonly object Gate = new();
    internal static string? LastWriter;
}

/// <summary>Monitor.Enter and Monitor.Exit on the same gate protect both writers from racing.</summary>
public sealed class WriterLogWorker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Monitor.Enter(WriterLog.Gate);
        try
        {
            WriterLog.LastWriter = "worker";
        }
        finally
        {
            Monitor.Exit(WriterLog.Gate);
        }

        return Task.CompletedTask;
    }
}

[ApiController]
[Route("cases/monitor-enter-exit-same-gate")]
public sealed class WriterLogController : ControllerBase
{
    [HttpPost]
    public void Post(string writer)
    {
        Monitor.Enter(WriterLog.Gate);
        try
        {
            WriterLog.LastWriter = writer;
        }
        finally
        {
            Monitor.Exit(WriterLog.Gate);
        }
    }
}

public static class MonitorEnterExitSameGateCase
{
    public static IServiceCollection AddMonitorEnterExitSameGate(this IServiceCollection services) =>
        services.AddHostedService<WriterLogWorker>();
}
