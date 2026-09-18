namespace Demo.Web.Cases.ParallelForSharedTotal;

/// <summary>The iterations of <see cref="Parallel.For(int, int, Action{int})"/> overlap and each adds to one
/// total: a lost update.</summary>
public sealed class TotalWorker : BackgroundService
{
    private const int Iterations = 8;

    private int _total;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Parallel.For(0, Iterations, i => _total += i);
        return Task.CompletedTask;
    }
}

public static class ParallelForSharedTotalCase
{
    public static IServiceCollection AddParallelForSharedTotal(this IServiceCollection services) =>
        services.AddHostedService<TotalWorker>();
}
