namespace Demo.Web.Cases.ThreadStartJoin;

/// <summary>A started thread writes two fields; the parent writes one before <see cref="Thread.Join()"/> and the
/// other after it.</summary>
public sealed class ThreadJoinWorker : BackgroundService
{
    public string? BeforeJoin { get; private set; }
    public string? AfterJoin { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var thread = new Thread(() =>
        {
            BeforeJoin = "thread";
            AfterJoin = "thread";
        });
        thread.Start();
        BeforeJoin = "parent";
        thread.Join();
        AfterJoin = "parent";
        return Task.CompletedTask;
    }
}

public static class ThreadStartJoinCase
{
    public static IServiceCollection AddThreadStartJoin(this IServiceCollection services) =>
        services.AddHostedService<ThreadJoinWorker>();
}
