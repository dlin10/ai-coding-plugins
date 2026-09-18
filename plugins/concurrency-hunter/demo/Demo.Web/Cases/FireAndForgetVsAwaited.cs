namespace Demo.Web.Cases.FireAndForgetVsAwaited;

/// <summary>A dropped async call writes a field after its first await and overlaps the parent's later write; an
/// awaited one writes another field and is ordered before it.</summary>
public sealed class DropWorker : BackgroundService
{
    public string? Dropped { get; private set; }
    public string? Saved { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = DropAsync();
        await SaveAsync();
        Dropped = "parent";
        Saved = "parent";
    }

    private async Task DropAsync()
    {
        await Task.Yield();
        Dropped = "drop";
    }

    private async Task SaveAsync()
    {
        await Task.Yield();
        Saved = "save";
    }
}

public static class FireAndForgetVsAwaitedCase
{
    public static IServiceCollection AddFireAndForgetVsAwaited(this IServiceCollection services) =>
        services.AddHostedService<DropWorker>();
}
