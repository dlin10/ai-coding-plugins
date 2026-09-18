namespace Demo.Web.Cases.WhenAllSiblings;

/// <summary>Two tasks joined by <see cref="Task.WhenAll(Task[])"/> write one field and overlap each other; the
/// parent writes it after the join.</summary>
public sealed class SiblingsWorker : BackgroundService
{
    public string? Owner { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(Task.Run(WriteA), Task.Run(WriteB));
        Owner = "parent";
    }

    private void WriteA() => Owner = "a";

    private void WriteB() => Owner = "b";
}

public static class WhenAllSiblingsCase
{
    public static IServiceCollection AddWhenAllSiblings(this IServiceCollection services) =>
        services.AddHostedService<SiblingsWorker>();
}
