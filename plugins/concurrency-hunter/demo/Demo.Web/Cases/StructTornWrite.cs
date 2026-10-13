using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.StructTornWrite;

public struct Range
{
    public long Lo;
    public long Hi;
}

/// <summary>A worker stores a whole struct into the singleton's field: that write is a write of every field, so it
/// races with a read of one field and with a copy of the whole.</summary>
public sealed class Window
{
    private Range _range;

    public void Shift(long n) => _range = new Range { Lo = n, Hi = n + 10 };

    public long Get() => _range.Lo;

    public long Span()
    {
        var r = _range;
        return r.Hi - r.Lo;
    }
}

public sealed class ShiftWorker : BackgroundService
{
    private readonly Window _window;
    public ShiftWorker(Window window) => _window = window;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _window.Shift(42);
        return Task.CompletedTask;
    }
}

[ApiController]
[Route("cases/struct-torn-write")]
public sealed class WindowController : ControllerBase
{
    private readonly Window _window;

    public WindowController(Window window) => _window = window;

    [HttpGet("lo")]
    public long Get() => _window.Get();

    [HttpGet("span")]
    public long Span() => _window.Span();
}

public static class StructTornWriteCase
{
    public static IServiceCollection AddStructTornWrite(this IServiceCollection services) =>
        services.AddSingleton<Window>()
                .AddHostedService<ShiftWorker>();
}
