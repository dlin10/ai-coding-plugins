namespace Demo.Web.Cases.WhenAnyNoJoin;

/// <summary><see cref="Task.WhenAny(Task, Task)"/> joins only the task that finished first, so the parent's
/// write after it overlaps the other task's write.</summary>
public sealed class WinnerWorker : BackgroundService
{
    public string? Winner { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var a = Task.Delay(1, stoppingToken);
        var b = Task.Run(() => { Winner = "b"; });
        await Task.WhenAny(a, b);
        Winner = "parent";
    }
}

public static class WhenAnyNoJoinCase
{
    public static IServiceCollection AddWhenAnyNoJoin(this IServiceCollection services) =>
        services.AddHostedService<WinnerWorker>();
}
