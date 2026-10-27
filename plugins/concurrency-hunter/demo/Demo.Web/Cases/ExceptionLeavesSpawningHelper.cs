namespace Demo.Web.Cases.ExceptionLeavesSpawningHelper;

/// <summary>The helper starts its work and then throws: the exception leaves the helper and reaches the worker's handler, which
/// runs while the work does, so the handler's write is no write before the work's.</summary>
public sealed class FallbackWorker : BackgroundService
{
    public string? Outcome { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            Start();
        }
        catch (InvalidOperationException)
        {
            Outcome = "fallback";
        }

        return Task.CompletedTask;
    }

    private void Start()
    {
        Task.Run(Work);
        Risky();
    }

    private void Work() => Outcome = "work";

    private static void Risky() => throw new InvalidOperationException("The step failed.");
}

public static class ExceptionLeavesSpawningHelperCase
{
    public static IServiceCollection AddExceptionLeavesSpawningHelper(this IServiceCollection services) =>
        services.AddHostedService<FallbackWorker>();
}
