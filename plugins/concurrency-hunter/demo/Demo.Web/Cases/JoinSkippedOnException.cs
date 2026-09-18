namespace Demo.Web.Cases.JoinSkippedOnException;

/// <summary>The await of the handle is skipped when the call before it throws, so the parent's write after the
/// <c>try</c> is not ordered after the task.</summary>
public sealed class OutcomeWorker : BackgroundService
{
    public string? Outcome { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var task = Task.Run(() => { Outcome = "spawned"; });
        try
        {
            Risky();
            await task;
        }
        catch (InvalidOperationException)
        {
        }

        Outcome = "parent";
    }

    private static void Risky() => throw new InvalidOperationException("The step failed.");
}

public static class JoinSkippedOnExceptionCase
{
    public static IServiceCollection AddJoinSkippedOnException(this IServiceCollection services) =>
        services.AddHostedService<OutcomeWorker>();
}
