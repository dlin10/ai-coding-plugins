using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.TimersTimerElapsed;

/// <summary>The <see cref="System.Timers.Timer.Elapsed"/> handler of an auto-resetting timer overlaps itself on a
/// counter and on a property that an action reads.</summary>
public sealed class Sampler
{
    private readonly System.Timers.Timer _timer = new(1000);
    private int _samples;

    public Sampler()
    {
        _timer.AutoReset = true;
        _timer.Elapsed += OnElapsed;
        _timer.Start();
    }

    public string? LastSample { get; private set; }

    private void OnElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        _samples++;
        LastSample = "sample";
    }
}

[ApiController]
[Route("cases/timers-timer-elapsed")]
public sealed class SamplerController : ControllerBase
{
    private readonly Sampler _sampler;

    public SamplerController(Sampler sampler) => _sampler = sampler;

    [HttpGet]
    public string? Get() => _sampler.LastSample;
}

public static class TimersTimerElapsedCase
{
    public static IServiceCollection AddTimersTimerElapsed(this IServiceCollection services) =>
        services.AddSingleton<Sampler>();
}
