namespace Demo.Web.Cases.WhenAllSynchronousPrefix;

/// <summary>Two async methods passed to <see cref="Task.WhenAll(Task[])"/> write one field before their first
/// await, one after the other in the caller, and another field after it, where they overlap.</summary>
public sealed class PrefixWorker : BackgroundService
{
    public string? Prefix { get; private set; }
    public string? Suffix { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(StepA(), StepB());
    }

    private async Task StepA()
    {
        Prefix = "a";
        await Task.Yield();
        Suffix = "a";
    }

    private async Task StepB()
    {
        Prefix = "b";
        await Task.Yield();
        Suffix = "b";
    }
}

public static class WhenAllSynchronousPrefixCase
{
    public static IServiceCollection AddWhenAllSynchronousPrefix(this IServiceCollection services) =>
        services.AddHostedService<PrefixWorker>();
}
