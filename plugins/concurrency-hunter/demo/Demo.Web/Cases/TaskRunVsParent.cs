namespace Demo.Web.Cases.TaskRunVsParent;

/// <summary>A <see cref="Task.Run(Action)"/> lambda writes a field that the parent writes before it awaits the
/// handle: the two writes overlap.</summary>
public sealed class StatusWorker : BackgroundService
{
    public string? Status { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var task = Task.Run(() => { Status = "spawned"; });
        Status = "parent";
        await task;
    }
}

public static class TaskRunVsParentCase
{
    public static IServiceCollection AddTaskRunVsParent(this IServiceCollection services) =>
        services.AddHostedService<StatusWorker>();
}
