namespace Demo.Web.Cases.ReadonlyStructDefensiveCopy;

public struct Ticker
{
    private int Count;

    public void Tick() { Count++; }
}

/// <summary>Hard negative: a mutating method called on a <c>readonly</c> struct field runs on a defensive copy C#
/// makes, so the field's storage is only read.</summary>
public sealed class Clock
{
    private readonly Ticker _ticker;

    public Clock() => _ticker = new Ticker();

    public void Tick() => _ticker.Tick();
}

public sealed class FirstWorker : BackgroundService
{
    private readonly Clock _clock;
    public FirstWorker(Clock clock) => _clock = clock;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _clock.Tick();
        return Task.CompletedTask;
    }
}

public sealed class SecondWorker : BackgroundService
{
    private readonly Clock _clock;
    public SecondWorker(Clock clock) => _clock = clock;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _clock.Tick();
        return Task.CompletedTask;
    }
}

public static class ReadonlyStructDefensiveCopyCase
{
    public static IServiceCollection AddReadonlyStructDefensiveCopy(this IServiceCollection services) =>
        services.AddSingleton<Clock>()
                .AddHostedService<FirstWorker>()
                .AddHostedService<SecondWorker>();
}
