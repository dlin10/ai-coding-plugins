namespace Demo.Web.Cases.TaskHandleAwaitedElsewhere;

/// <summary>The handle is stored in a field and awaited in another method before the parent writes: a spawn with
/// a join, not fire-and-forget.</summary>
public sealed class PendingWorker : BackgroundService
{
    private Task? _pending;

    public string? Result { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _pending = Task.Run(() => { Result = "spawned"; });
        await WaitForPendingAsync();
        Result = "parent";
    }

    private async Task WaitForPendingAsync() => await _pending!;
}

public static class TaskHandleAwaitedElsewhereCase
{
    public static IServiceCollection AddTaskHandleAwaitedElsewhere(this IServiceCollection services) =>
        services.AddHostedService<PendingWorker>();
}
