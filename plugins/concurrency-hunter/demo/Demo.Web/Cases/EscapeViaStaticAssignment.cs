using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.EscapeViaStaticAssignment;

public sealed class Snapshot
{
    public string? Owner { get; set; }
}

public static class LatestSnapshot
{
    public static Snapshot? Current;
}

/// <summary>A worker creates an object, publishes it in a static field and keeps writing it: an action reads
/// both the static slot and the escaped object. The allocation runs in a worker, not an action, so each
/// pair is a real race.</summary>
public sealed class SnapshotWorker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var snapshot = new Snapshot();
        LatestSnapshot.Current = snapshot;
        snapshot.Owner = "worker";
        return Task.CompletedTask;
    }
}

[ApiController]
[Route("cases/escape-via-static-assignment")]
public sealed class SnapshotController : ControllerBase
{
    [HttpGet]
    public string? Get() => LatestSnapshot.Current!.Owner;
}

public static class EscapeViaStaticAssignmentCase
{
    public static IServiceCollection AddEscapeViaStaticAssignment(this IServiceCollection services) =>
        services.AddHostedService<SnapshotWorker>();
}
