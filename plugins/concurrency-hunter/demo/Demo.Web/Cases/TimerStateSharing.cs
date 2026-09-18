using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.TimerStateSharing;

public sealed class Gauge
{
    public int Level { get; set; }
}

/// <summary>The object passed to a periodic <see cref="Timer"/> as its state is the one the singleton exposes:
/// the callback writes it, an action reads it.</summary>
public sealed class GaugeBoard
{
    private readonly Timer _timer;

    public GaugeBoard() => _timer = new Timer(OnTick, Gauge, 0, 1000);

    public Gauge Gauge { get; } = new();

    private static void OnTick(object? state) => ((Gauge)state!).Level = 1;
}

[ApiController]
[Route("cases/timer-state-sharing")]
public sealed class GaugeController : ControllerBase
{
    private readonly GaugeBoard _board;

    public GaugeController(GaugeBoard board) => _board = board;

    [HttpGet]
    public int Get() => _board.Gauge.Level;
}

public static class TimerStateSharingCase
{
    public static IServiceCollection AddTimerStateSharing(this IServiceCollection services) =>
        services.AddSingleton<GaugeBoard>();
}
