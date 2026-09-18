namespace Demo.Web.Cases.EventWaitNotOrdering;

/// <summary>The task signals an event after its write and the parent waits for it before writing; event-based
/// waits give no order (TD-086), a known limitation.</summary>
public sealed class PayloadWorker : BackgroundService
{
    public string? Payload { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ready = new ManualResetEventSlim();
        Task.Run(() =>
        {
            Payload = "spawned";
            ready.Set();
        });
        ready.Wait();
        Payload = "parent";
        return Task.CompletedTask;
    }
}

public static class EventWaitNotOrderingCase
{
    public static IServiceCollection AddEventWaitNotOrdering(this IServiceCollection services) =>
        services.AddHostedService<PayloadWorker>();
}
