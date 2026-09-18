using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.TimerOneShot;

/// <summary>A one-shot <see cref="Timer"/>: its single callback cannot overlap itself on the counter, but its
/// write of the phase overlaps an action's read.</summary>
public sealed class Warmup
{
    private readonly Timer _timer;
    private int _runs;

    public Warmup() => _timer = new Timer(OnTick, null, 0, Timeout.Infinite);

    public string? Phase { get; private set; }

    private void OnTick(object? state)
    {
        _runs++;
        Phase = "warm";
    }
}

[ApiController]
[Route("cases/timer-one-shot")]
public sealed class WarmupController : ControllerBase
{
    private readonly Warmup _warmup;

    public WarmupController(Warmup warmup) => _warmup = warmup;

    [HttpGet]
    public string? Get() => _warmup.Phase;
}

public static class TimerOneShotCase
{
    public static IServiceCollection AddTimerOneShot(this IServiceCollection services) =>
        services.AddSingleton<Warmup>();
}
