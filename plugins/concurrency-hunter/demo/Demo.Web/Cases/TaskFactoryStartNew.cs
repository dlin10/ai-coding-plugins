namespace Demo.Web.Cases.TaskFactoryStartNew;

/// <summary>A <see cref="TaskFactory.StartNew(Action)"/> lambda writes a field that the parent writes too; nothing
/// ever joins the task.</summary>
public sealed class ModeWorker : BackgroundService
{
    public string? Mode { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task.Factory.StartNew(() => { Mode = "spawned"; });
        Mode = "parent";
        return Task.CompletedTask;
    }
}

public static class TaskFactoryStartNewCase
{
    public static IServiceCollection AddTaskFactoryStartNew(this IServiceCollection services) =>
        services.AddHostedService<ModeWorker>();
}
