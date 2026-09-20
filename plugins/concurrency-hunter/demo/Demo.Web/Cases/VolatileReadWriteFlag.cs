using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.VolatileReadWriteFlag;

/// <summary>A stop flag published with <see cref="Volatile.Write(ref bool, bool)"/> and observed with
/// <see cref="Volatile.Read(ref bool)"/>: both sides are atomic on that one location, so the action and the worker do
/// not race.</summary>
public sealed class ShutdownFlag
{
    private bool _stop;

    public void Request() => Volatile.Write(ref _stop, true);

    public bool IsRequested() => Volatile.Read(ref _stop);
}

[ApiController]
[Route("cases/volatile-read-write-flag")]
public sealed class ShutdownController : ControllerBase
{
    private readonly ShutdownFlag _flag;

    public ShutdownController(ShutdownFlag flag) => _flag = flag;

    [HttpPost]
    public void Post() => _flag.Request();
}

public sealed class ShutdownWatcher : BackgroundService
{
    private readonly ShutdownFlag _flag;

    public ShutdownWatcher(ShutdownFlag flag) => _flag = flag;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!_flag.IsRequested())
            await Task.Delay(100, stoppingToken);
    }
}

public static class VolatileReadWriteFlagCase
{
    public static IServiceCollection AddVolatileReadWriteFlag(this IServiceCollection services) =>
        services.AddSingleton<ShutdownFlag>()
                .AddHostedService<ShutdownWatcher>();
}
