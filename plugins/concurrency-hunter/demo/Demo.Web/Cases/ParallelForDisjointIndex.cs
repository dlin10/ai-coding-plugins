namespace Demo.Web.Cases.ParallelForDisjointIndex;

/// <summary>The iterations of <see cref="Parallel.For(int, int, Action{int})"/> overlap, but each writes the cell its
/// own loop counter names: two iterations never hold the same index, so the array is partitioned, not shared.</summary>
public sealed class ResultsWorker : BackgroundService
{
    private const int Iterations = 8;

    private readonly string[] _results = new string[Iterations];

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Parallel.For(0, Iterations, i => _results[i] = "filled");
        return Task.CompletedTask;
    }
}

public static class ParallelForDisjointIndexCase
{
    public static IServiceCollection AddParallelForDisjointIndex(this IServiceCollection services) =>
        services.AddHostedService<ResultsWorker>();
}
