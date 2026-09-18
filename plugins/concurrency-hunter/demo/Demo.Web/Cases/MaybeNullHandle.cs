namespace Demo.Web.Cases.MaybeNullHandle;

/// <summary>A handle that may still be null when it is awaited proves nothing: the write after the <c>try</c>
/// overlaps the work, while the same await of a handle that is always assigned orders it.</summary>
public sealed class HandoffWorker : BackgroundService
{
    public string? Dropped { get; private set; }
    public string? Taken { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task? dropped = null;
        if (stoppingToken.CanBeCanceled)
        {
            dropped = Task.Run(WriteDropped);
        }

        var taken = Task.Run(WriteTaken);

        try
        {
            await dropped!;
        }
        catch (NullReferenceException)
        {
        }

        try
        {
            await taken;
        }
        catch (NullReferenceException)
        {
        }

        Dropped = "parent";
        Taken = "parent";
    }

    private void WriteDropped() => Dropped = "dropped";

    private void WriteTaken() => Taken = "taken";
}

public static class MaybeNullHandleCase
{
    public static IServiceCollection AddMaybeNullHandle(this IServiceCollection services) =>
        services.AddHostedService<HandoffWorker>();
}
