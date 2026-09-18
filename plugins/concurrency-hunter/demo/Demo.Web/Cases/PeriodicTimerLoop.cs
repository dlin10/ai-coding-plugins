using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.PeriodicTimerLoop;

public sealed class TickSnapshot
{
    public string? Last { get; set; }
}

/// <summary>A <see cref="PeriodicTimer"/> loop is part of <c>ExecuteAsync</c>: its iterations run one after
/// another, but its write of the snapshot overlaps an action's read.</summary>
public sealed class TickWorker : BackgroundService
{
    private readonly TickSnapshot _snapshot;
    private int _iterations;

    public TickWorker(TickSnapshot snapshot) => _snapshot = snapshot;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (true)
        {
            await timer.WaitForNextTickAsync(stoppingToken);
            _iterations++;
            _snapshot.Last = "tick";
        }
    }
}

[ApiController]
[Route("cases/periodic-timer-loop")]
public sealed class TickController : ControllerBase
{
    private readonly TickSnapshot _snapshot;

    public TickController(TickSnapshot snapshot) => _snapshot = snapshot;

    [HttpGet]
    public string? Get() => _snapshot.Last;
}

public static class PeriodicTimerLoopCase
{
    public static IServiceCollection AddPeriodicTimerLoop(this IServiceCollection services) =>
        services.AddSingleton<TickSnapshot>()
                .AddHostedService<TickWorker>();
}
