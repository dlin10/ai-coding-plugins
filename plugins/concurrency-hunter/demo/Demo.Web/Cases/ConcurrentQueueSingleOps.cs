using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ConcurrentQueueSingleOps;

/// <summary>One enqueue in an action against one dequeue in a worker: a single operation of a
/// <see cref="ConcurrentQueue{T}"/> is atomic on its own, and nothing here composes two of them.</summary>
public sealed class Inbox
{
    public ConcurrentQueue<string> Messages { get; } = new();
}

[ApiController]
[Route("cases/concurrent-queue-single-ops")]
public sealed class InboxController : ControllerBase
{
    private readonly Inbox _inbox;

    public InboxController(Inbox inbox) => _inbox = inbox;

    [HttpPost]
    public void Post(string message) => _inbox.Messages.Enqueue(message);
}

public sealed class InboxDrainWorker : BackgroundService
{
    private readonly Inbox _inbox;

    public InboxDrainWorker(Inbox inbox) => _inbox = inbox;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _inbox.Messages.TryDequeue(out _);
        return Task.CompletedTask;
    }
}

public static class ConcurrentQueueSingleOpsCase
{
    public static IServiceCollection AddConcurrentQueueSingleOps(this IServiceCollection services) =>
        services.AddSingleton<Inbox>()
                .AddHostedService<InboxDrainWorker>();
}
