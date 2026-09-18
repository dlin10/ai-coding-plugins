using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ThreadingTimerVsAction;

/// <summary>A periodic <see cref="Timer"/> owned by a singleton writes a property that an action reads; the
/// callback also overlaps itself.</summary>
public sealed class Beat
{
    private readonly Timer _timer;

    public Beat() => _timer = new Timer(OnTick, null, 0, 1000);

    public string? LastTick { get; private set; }

    private void OnTick(object? state) => LastTick = "tick";
}

[ApiController]
[Route("cases/threading-timer-vs-action")]
public sealed class BeatController : ControllerBase
{
    private readonly Beat _beat;

    public BeatController(Beat beat) => _beat = beat;

    [HttpGet]
    public string? Get() => _beat.LastTick;
}

public static class ThreadingTimerVsActionCase
{
    public static IServiceCollection AddThreadingTimerVsAction(this IServiceCollection services) =>
        services.AddSingleton<Beat>();
}
