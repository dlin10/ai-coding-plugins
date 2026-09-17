using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.FactoryInterfaceDispatch;

public interface IClock
{
    void Tick();
}

public sealed class WallClock : IClock
{
    public string? LastTick { get; set; }

    public void Tick() => LastTick = "tick";
}

/// <summary>An interface singleton whose factory lambda returns the implementation: the interface call
/// dispatches to that implementation, and the action and the worker write one shared instance.</summary>
[ApiController]
[Route("cases/factory-interface-dispatch")]
public sealed class ClockController : ControllerBase
{
    private readonly IClock _clock;

    public ClockController(IClock clock) => _clock = clock;

    [HttpPost]
    public void Post() => _clock.Tick();
}

public sealed class ClockWorker : BackgroundService
{
    private readonly IClock _clock;

    public ClockWorker(IClock clock) => _clock = clock;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _clock.Tick();
        return Task.CompletedTask;
    }
}

public static class FactoryInterfaceDispatchCase
{
    public static IServiceCollection AddFactoryInterfaceDispatch(this IServiceCollection services) =>
        services.AddSingleton<IClock>(_ => new WallClock())
                .AddHostedService<ClockWorker>();
}
