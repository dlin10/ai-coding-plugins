namespace Demo.Web.Cases.AsyncTailEntry;

/// <summary>A detached async call enters its tail only at the awaits it can first suspend at: the await that joins
/// a spawn later in the tail still orders the write after it, while the write before that await overlaps the
/// work.</summary>
public sealed class DrainWorker : BackgroundService
{
    public string? Before { get; private set; }
    public string? After { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = DrainAsync();
        return Task.CompletedTask;
    }

    private async Task DrainAsync()
    {
        await Task.Yield();
        var inner = Task.Run(Fill);
        Before = "draining";
        await inner;
        After = "drained";
    }

    private void Fill()
    {
        Before = "fill";
        After = "fill";
    }
}

public static class AsyncTailEntryCase
{
    public static IServiceCollection AddAsyncTailEntry(this IServiceCollection services) =>
        services.AddHostedService<DrainWorker>();
}
