namespace Demo.Web.Cases.ParallelForeach;

/// <summary>The iterations of <see cref="Parallel.ForEach{TSource}(IEnumerable{TSource}, Action{TSource})"/>
/// overlap and each writes one field.</summary>
public sealed class LastItemWorker : BackgroundService
{
    private string? _last;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string[] items = ["first", "second"];
        Parallel.ForEach(items, x => _last = x);
        return Task.CompletedTask;
    }
}

public static class ParallelForeachCase
{
    public static IServiceCollection AddParallelForeach(this IServiceCollection services) =>
        services.AddHostedService<LastItemWorker>();
}
