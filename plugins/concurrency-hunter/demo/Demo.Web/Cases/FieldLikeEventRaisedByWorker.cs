using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.FieldLikeEventRaisedByWorker;

/// <summary>A field-like event: its storage is the delegate field the compiler declares, and raising it runs every handler that
/// field holds in the raising execution.</summary>
public sealed class Ticker
{
    public event Action? Tick;

    public void Fire() => Tick?.Invoke();
}

/// <summary>Subscribes its own counter to the singleton ticker, so the hosted worker that raises the event runs the increment while
/// an action reads the count.</summary>
public sealed class TickCounter
{
    private int _ticks;

    public TickCounter(Ticker ticker) => ticker.Tick += OnTick;

    private void OnTick() => _ticks++;

    public int Read() => _ticks;
}

public sealed class TickWorker(Ticker ticker, TickCounter counter) : BackgroundService
{
    // Kept so the container builds the counter, whose constructor subscribes, before the worker raises the event.
    private readonly TickCounter _counter = counter;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ticker.Fire();
            await Task.Delay(100, stoppingToken);
        }
    }
}

[ApiController]
[Route("cases/field-like-event-raised-by-worker")]
public sealed class TickController(TickCounter counter) : ControllerBase
{
    [HttpGet]
    public int Get() => counter.Read();
}

public static class FieldLikeEventRaisedByWorkerCase
{
    public static IServiceCollection AddFieldLikeEventRaisedByWorker(this IServiceCollection services) =>
        services.AddSingleton<Ticker>()
                .AddSingleton<TickCounter>()
                .AddHostedService<TickWorker>();
}
