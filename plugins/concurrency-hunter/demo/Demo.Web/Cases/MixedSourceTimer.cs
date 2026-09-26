using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Demo.Web.Cases.MixedSourceTimer;

/// <summary>A singleton the timer callback writes and an action reads, and that keeps the timer the cache hands out.</summary>
public sealed class Clock
{
    public int Ticks;
    public System.Timers.Timer? Made;
}

/// <summary>Subscribes to one of two timers: one it creates and never starts, or one the cache's factory hands back and the clock keeps.
/// The first alone never calls back; the second may be any timer, started anywhere, so the callback runs and may run again, and the
/// cache call is a semantic gap that decides the overlap of every pair the callback is in (TD-034, TD-039, TD-061).</summary>
public sealed class ClockWorker : BackgroundService
{
    private readonly Clock _clock;
    private readonly IMemoryCache _cache;

    public ClockWorker(Clock clock, IMemoryCache cache)
    {
        _clock = clock;
        _cache = cache;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var known = new System.Timers.Timer();
        var made = _cache.GetOrCreate("clock", _ => new System.Timers.Timer(1000))!;
        _clock.Made = made;
        (stoppingToken.IsCancellationRequested ? known : made).Elapsed += (_, _) => _clock.Ticks = Environment.TickCount;
        return Task.CompletedTask;
    }
}

[ApiController]
[Route("cases/mixed-source-timer")]
public sealed class ClockController : ControllerBase
{
    private readonly Clock _clock;

    public ClockController(Clock clock) => _clock = clock;

    [HttpGet]
    public int Get() => _clock.Ticks;
}

public static class MixedSourceTimerCase
{
    public static IServiceCollection AddMixedSourceTimer(this IServiceCollection services) =>
        services.AddMemoryCache()
                .AddSingleton<Clock>()
                .AddHostedService<ClockWorker>();
}
