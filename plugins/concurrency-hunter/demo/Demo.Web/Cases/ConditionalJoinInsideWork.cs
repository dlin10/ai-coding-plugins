namespace Demo.Web.Cases.ConditionalJoinInsideWork;

/// <summary>Joined work bounds only the spawns it waits for on every path: the field of the spawn it waits for in
/// one branch still overlaps the parent's write after the outer await, the field of the spawn it always waits for
/// does not.</summary>
public sealed class NestedWorker : BackgroundService
{
    public string? Maybe { get; private set; }
    public string? Always { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Run(() => Spawn(stoppingToken));
        Maybe = "parent";
        Always = "parent";
    }

    private void Spawn(CancellationToken token)
    {
        var maybe = Task.Run(WriteMaybe);
        var always = Task.Run(WriteAlways);
        if (token.IsCancellationRequested)
        {
            maybe.Wait();
        }

        always.Wait();
    }

    private void WriteMaybe() => Maybe = "maybe";

    private void WriteAlways() => Always = "always";
}

public static class ConditionalJoinInsideWorkCase
{
    public static IServiceCollection AddConditionalJoinInsideWork(this IServiceCollection services) =>
        services.AddHostedService<NestedWorker>();
}
