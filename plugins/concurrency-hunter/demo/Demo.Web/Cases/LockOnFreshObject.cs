namespace Demo.Web.Cases.LockOnFreshObject;

/// <summary>The lock object is allocated by the call that takes it, so every caller locks an object of its own and excludes
/// nobody.</summary>
public sealed class Marker
{
    private string? _state;

    public void Set(string state)
    {
        lock (new object())
            _state = state;
    }
}

public sealed class FirstMarkerWorker : BackgroundService
{
    private readonly Marker _marker;

    public FirstMarkerWorker(Marker marker) => _marker = marker;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _marker.Set("first");
        return Task.CompletedTask;
    }
}

public sealed class SecondMarkerWorker : BackgroundService
{
    private readonly Marker _marker;

    public SecondMarkerWorker(Marker marker) => _marker = marker;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _marker.Set("second");
        return Task.CompletedTask;
    }
}

public static class LockOnFreshObjectCase
{
    public static IServiceCollection AddLockOnFreshObject(this IServiceCollection services) =>
        services.AddSingleton<Marker>()
                .AddHostedService<FirstMarkerWorker>()
                .AddHostedService<SecondMarkerWorker>();
}
