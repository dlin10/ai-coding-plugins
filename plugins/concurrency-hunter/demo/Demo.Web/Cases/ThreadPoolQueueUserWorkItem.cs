namespace Demo.Web.Cases.ThreadPoolQueueUserWorkItem;

/// <summary>Work queued to the thread pool, one field per queueing method, writes a field the parent writes
/// too.</summary>
public sealed class QueueWorker : BackgroundService
{
    public string? Queued { get; private set; }
    public string? UnsafeQueued { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ThreadPool.QueueUserWorkItem(_ => Queued = "callback");
        Queued = "parent";
        ThreadPool.UnsafeQueueUserWorkItem(_ => UnsafeQueued = "callback", null);
        UnsafeQueued = "parent";
        return Task.CompletedTask;
    }
}

public static class ThreadPoolQueueUserWorkItemCase
{
    public static IServiceCollection AddThreadPoolQueueUserWorkItem(this IServiceCollection services) =>
        services.AddHostedService<QueueWorker>();
}
