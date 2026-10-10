using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.SpawnBodylessWork;

/// <summary>A singleton buffer a worker refills in the background and an action reads.</summary>
public sealed class NoiseBuffer
{
    public readonly byte[] Bytes = new byte[16];
}

/// <summary>Queues <c>Random.NextBytes</c> as the work itself: a method group of a member neither the library table nor the collection
/// table describes, so the work has no body and no model. Its unknown effect runs in the spawn's execution and touches the buffer it is
/// handed as state (question 145).</summary>
public sealed class NoiseRefresher(NoiseBuffer buffer) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ThreadPool.QueueUserWorkItem(Random.Shared.NextBytes, buffer.Bytes, preferLocal: false);
        return Task.CompletedTask;
    }
}

[ApiController]
[Route("cases/spawn-bodyless-work")]
public sealed class NoiseController(NoiseBuffer buffer) : ControllerBase
{
    [HttpGet]
    public byte Get() => buffer.Bytes[0];
}

public static class SpawnBodylessWorkCase
{
    public static IServiceCollection AddSpawnBodylessWork(this IServiceCollection services) =>
        services.AddSingleton<NoiseBuffer>()
                .AddHostedService<NoiseRefresher>();
}
