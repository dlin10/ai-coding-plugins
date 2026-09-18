namespace Demo.Web.Cases.TaskWaitJoin;

/// <summary>The parent writes one field before <see cref="Task.Wait()"/> and another after it; the task writes
/// both.</summary>
public sealed class WaitJoinWorker : BackgroundService
{
    public string? BeforeWait { get; private set; }
    public string? AfterWait { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var task = Task.Run(() =>
        {
            BeforeWait = "spawned";
            AfterWait = "spawned";
        });
        BeforeWait = "parent";
        task.Wait();
        AfterWait = "parent";
        return Task.CompletedTask;
    }
}

public static class TaskWaitJoinCase
{
    public static IServiceCollection AddTaskWaitJoin(this IServiceCollection services) =>
        services.AddHostedService<WaitJoinWorker>();
}
