namespace Demo.Web.Cases.DisposeAsyncConfigureAwait;

/// <summary>An awaited <see cref="Timer.DisposeAsync"/> waits for the callback through
/// <c>ConfigureAwait(false)</c> too, so the write after it is ordered.</summary>
public sealed class ConfiguredWorker : BackgroundService
{
    public string? LastTick { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = new Timer(OnTick, null, 0, Timeout.Infinite);
        await timer.DisposeAsync().ConfigureAwait(false);
        LastTick = "stopped";
    }

    private void OnTick(object? state) => LastTick = "tick";
}

public static class DisposeAsyncConfigureAwaitCase
{
    public static IServiceCollection AddDisposeAsyncConfigureAwait(this IServiceCollection services) =>
        services.AddHostedService<ConfiguredWorker>();
}
