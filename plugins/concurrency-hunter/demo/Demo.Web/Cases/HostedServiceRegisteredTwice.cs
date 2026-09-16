namespace Demo.Web.Cases.HostedServiceRegisteredTwice;

/// <summary>Negative: a hosted service registered twice still runs as one instance, because
/// AddHostedService keeps a single registration, so its write to its own property does not overlap
/// itself.</summary>
public sealed class HeartbeatWorker : BackgroundService
{
    public string? LastBeat { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LastBeat = "beat";
        return Task.CompletedTask;
    }
}

public static class HostedServiceRegisteredTwiceCase
{
    public static IServiceCollection AddHostedServiceRegisteredTwice(this IServiceCollection services) =>
        services.AddHostedService<HeartbeatWorker>()
                .AddHostedService<HeartbeatWorker>();
}
