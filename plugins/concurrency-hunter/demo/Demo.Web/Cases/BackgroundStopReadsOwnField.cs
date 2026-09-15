namespace Demo.Web.Cases.BackgroundStopReadsOwnField;

/// <summary>A background service whose stop path reads a field while its execute path can write it.</summary>
public sealed class ProgressWorker : BackgroundService
{
    private string? _lastItem;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lastItem = "item";
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        Report(_lastItem);
        return base.StopAsync(cancellationToken);
    }

    private static void Report(string? item) { }
}

public static class BackgroundStopReadsOwnFieldCase
{
    public static IServiceCollection AddBackgroundStopReadsOwnField(this IServiceCollection services) =>
        services.AddHostedService<ProgressWorker>();
}
