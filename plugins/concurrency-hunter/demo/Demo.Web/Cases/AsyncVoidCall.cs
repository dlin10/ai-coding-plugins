namespace Demo.Web.Cases.AsyncVoidCall;

/// <summary>An <c>async void</c> method writes a field after its first await; nothing can join it, so the
/// parent's write overlaps.</summary>
public sealed class NoticeWorker : BackgroundService
{
    public string? Notice { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Notify();
        Notice = "parent";
        return Task.CompletedTask;
    }

    private async void Notify()
    {
        await Task.Yield();
        Notice = "notify";
    }
}

public static class AsyncVoidCallCase
{
    public static IServiceCollection AddAsyncVoidCall(this IServiceCollection services) =>
        services.AddHostedService<NoticeWorker>();
}
