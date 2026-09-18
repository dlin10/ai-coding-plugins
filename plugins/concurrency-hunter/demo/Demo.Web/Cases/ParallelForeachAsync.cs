namespace Demo.Web.Cases.ParallelForeachAsync;

/// <summary>The bodies of <c>Parallel.ForEachAsync</c> overlap; each reads a field, awaits and writes it back
/// from what it read.</summary>
public sealed class ProcessedWorker : BackgroundService
{
    private int _processed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int[] items = [1, 2, 3];
        await Parallel.ForEachAsync(items, stoppingToken, async (item, token) =>
        {
            var seen = _processed;
            await Task.Delay(item, token);
            _processed = seen + item;
        });
    }
}

public static class ParallelForeachAsyncCase
{
    public static IServiceCollection AddParallelForeachAsync(this IServiceCollection services) =>
        services.AddHostedService<ProcessedWorker>();
}
