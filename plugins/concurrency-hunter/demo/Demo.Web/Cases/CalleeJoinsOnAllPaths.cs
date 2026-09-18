namespace Demo.Web.Cases.CalleeJoinsOnAllPaths;

/// <summary>A call is a join only when every implementation it may reach waits: the field of the task both drains
/// wait for is ordered after the call, the field of the task only the eager drain waits for is not.</summary>
public class Drain
{
    public Task? Always;
    public Task? Maybe;

    public virtual void Run() => Always!.Wait();
}

public sealed class EagerDrain : Drain
{
    public override void Run()
    {
        Always!.Wait();
        Maybe!.Wait();
    }
}

public sealed class DispatchWorker : BackgroundService
{
    public string? Always { get; private set; }
    public string? Maybe { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var drain = stoppingToken.IsCancellationRequested ? new EagerDrain() : new Drain();
        drain.Always = Task.Run(WriteAlways);
        drain.Maybe = Task.Run(WriteMaybe);
        drain.Run();
        Always = "parent";
        Maybe = "parent";
        return Task.CompletedTask;
    }

    private void WriteAlways() => Always = "always";

    private void WriteMaybe() => Maybe = "maybe";
}

public static class CalleeJoinsOnAllPathsCase
{
    public static IServiceCollection AddCalleeJoinsOnAllPaths(this IServiceCollection services) =>
        services.AddHostedService<DispatchWorker>();
}
