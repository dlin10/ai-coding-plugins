namespace Demo.Web.Cases.ReceiverSensitivity;

/// <summary>Hard negative: one method body, two receivers. The shared summary of <see cref="Mark"/> must be
/// instantiated per receiver, not merged into one resource.</summary>
public sealed class Tally
{
    private string? _markedBy;

    public void Mark(string by) => _markedBy = by;
}

public sealed class FirstTallyWorker : BackgroundService
{
    private readonly Tally _tally = new();

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _tally.Mark("first");
        return Task.CompletedTask;
    }
}

public sealed class SecondTallyWorker : BackgroundService
{
    private readonly Tally _tally = new();

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _tally.Mark("second");
        return Task.CompletedTask;
    }
}

public static class ReceiverSensitivityCase
{
    public static IServiceCollection AddReceiverSensitivity(this IServiceCollection services) =>
        services.AddHostedService<FirstTallyWorker>()
                .AddHostedService<SecondTallyWorker>();
}
