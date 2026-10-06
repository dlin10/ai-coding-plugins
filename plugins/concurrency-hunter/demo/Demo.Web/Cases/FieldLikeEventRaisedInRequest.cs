using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.FieldLikeEventRaisedInRequest;

/// <summary>A field-like event raised only inside the request that owns the ticker.</summary>
public sealed class Ticker
{
    public event Action? Tick;

    public void Fire() => Tick?.Invoke();
}

/// <summary>Hard negative: the same subscription and increment as the worker case, but each request gets its own scoped ticker and
/// counter and raises the event itself, so the increment never meets another request's read.</summary>
public sealed class TickCounter
{
    private int _ticks;

    public TickCounter(Ticker ticker) => ticker.Tick += OnTick;

    private void OnTick() => _ticks++;

    public int Read() => _ticks;
}

[ApiController]
[Route("cases/field-like-event-raised-in-request")]
public sealed class TickController(Ticker ticker, TickCounter counter) : ControllerBase
{
    [HttpGet]
    public int Get()
    {
        ticker.Fire();
        return counter.Read();
    }
}

public static class FieldLikeEventRaisedInRequestCase
{
    public static IServiceCollection AddFieldLikeEventRaisedInRequest(this IServiceCollection services) =>
        services.AddScoped<Ticker>()
                .AddScoped<TickCounter>();
}
