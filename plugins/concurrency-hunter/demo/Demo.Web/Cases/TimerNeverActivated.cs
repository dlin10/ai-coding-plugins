using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.TimerNeverActivated;

/// <summary>A <see cref="Timer"/> created with an infinite due time and period and never changed: its callback
/// never runs, so its write cannot meet the action's read.</summary>
public sealed class Standby
{
    private readonly Timer _timer;

    public Standby() => _timer = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);

    public string? State { get; private set; }

    private void OnTick(object? state) => State = "awake";
}

[ApiController]
[Route("cases/timer-never-activated")]
public sealed class StandbyController : ControllerBase
{
    private readonly Standby _standby;

    public StandbyController(Standby standby) => _standby = standby;

    [HttpGet]
    public string? Get() => _standby.State;
}

public static class TimerNeverActivatedCase
{
    public static IServiceCollection AddTimerNeverActivated(this IServiceCollection services) =>
        services.AddSingleton<Standby>();
}
