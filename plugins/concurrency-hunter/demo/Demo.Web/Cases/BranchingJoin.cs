namespace Demo.Web.Cases.BranchingJoin;

/// <summary>A wait every branch performs orders the write after it although no single wait dominates it; awaits of
/// two different tasks in two branches order neither, because only one of them runs.</summary>
public sealed class BranchWorker : BackgroundService
{
    public string? Waited { get; private set; }
    public string? First { get; private set; }
    public string? Second { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var waited = Task.Run(WriteWaited);
        var first = Task.Run(WriteFirst);
        var second = Task.Run(WriteSecond);

        if (stoppingToken.IsCancellationRequested)
        {
            waited.Wait();
            await first;
        }
        else
        {
            waited.Wait();
            await second;
        }

        Waited = "parent";
        First = "parent";
        Second = "parent";
    }

    private void WriteWaited() => Waited = "waited";

    private void WriteFirst() => First = "first";

    private void WriteSecond() => Second = "second";
}

public static class BranchingJoinCase
{
    public static IServiceCollection AddBranchingJoin(this IServiceCollection services) =>
        services.AddHostedService<BranchWorker>();
}
