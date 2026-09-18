namespace Demo.Web.Cases.ContinueWith;

/// <summary>A continuation writes a field that its antecedent and the parent write too: the continuation starts
/// after its antecedent, but the parent writes while both may run.</summary>
public sealed class StageWorker : BackgroundService
{
    public string? Stage { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var chain = Task.Run(First).ContinueWith(Second);
        Stage = "parent";
        await chain;
    }

    private void First() => Stage = "first";

    private void Second(Task antecedent) => Stage = "second";
}

public static class ContinueWithCase
{
    public static IServiceCollection AddContinueWith(this IServiceCollection services) =>
        services.AddHostedService<StageWorker>();
}
