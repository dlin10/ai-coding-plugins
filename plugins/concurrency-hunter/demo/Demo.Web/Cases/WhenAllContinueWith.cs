namespace Demo.Web.Cases.WhenAllContinueWith;

/// <summary>A continuation of a composite task is an unrecognized spawn: awaiting its unwrapped result orders
/// nothing, so the write after it still overlaps the continuation's tail, which also overlaps itself.</summary>
public sealed class CompositeWorker : BackgroundService
{
    public string? Primed { get; private set; }
    public string? Chained { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var composite = Task.WhenAll(Task.Run(Prime), Task.Delay(1, stoppingToken));
        await composite.ContinueWith(ChainAsync).Unwrap();
        Chained = "parent";
    }

    private void Prime() => Primed = "primed";

    private async Task ChainAsync(Task antecedent)
    {
        await Task.Yield();
        Chained = "chained";
    }
}

public static class WhenAllContinueWithCase
{
    public static IServiceCollection AddWhenAllContinueWith(this IServiceCollection services) =>
        services.AddHostedService<CompositeWorker>();
}
