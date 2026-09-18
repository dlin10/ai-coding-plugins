namespace Demo.Web.Cases.TaskHandleJoinOrder;

/// <summary>A task kept in a local writes two fields; the parent writes the first before awaiting the handle,
/// which overlaps, and the second after it, which is ordered.</summary>
public sealed class JoinOrderWorker : BackgroundService
{
    public string? BeforeAwait { get; private set; }
    public string? AfterAwait { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var task = Task.Run(() =>
        {
            BeforeAwait = "spawned";
            AfterAwait = "spawned";
        });
        BeforeAwait = "parent";
        await task;
        AfterAwait = "parent";
    }
}

public static class TaskHandleJoinOrderCase
{
    public static IServiceCollection AddTaskHandleJoinOrder(this IServiceCollection services) =>
        services.AddHostedService<JoinOrderWorker>();
}
