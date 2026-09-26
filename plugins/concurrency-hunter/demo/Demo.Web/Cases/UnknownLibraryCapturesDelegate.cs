using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.UnknownLibraryCapturesDelegate;

/// <summary>A singleton the delegate writes and an action reads.</summary>
public sealed class Shutdown
{
    public bool Requested;
}

/// <summary>Hands a lambda that writes the singleton to <c>CancellationToken.Register</c>, a member the library table does not describe
/// and no recognizer takes for a spawn or a timer. The lambda runs in the unknown call of that delegate, whenever the token likes and as
/// often: it meets the action's read, and the call is the semantic gap that decides the overlap. Handed once by a worker that runs once,
/// it runs one call at a time and does not meet itself (TD-034, TD-039, ADR 0011).</summary>
public sealed class ShutdownWatcher : BackgroundService
{
    private readonly Shutdown _shutdown;

    public ShutdownWatcher(Shutdown shutdown) => _shutdown = shutdown;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stoppingToken.Register(() => _shutdown.Requested = true);
        return Task.CompletedTask;
    }
}

[ApiController]
[Route("cases/unknown-library-captures-delegate")]
public sealed class ShutdownController : ControllerBase
{
    private readonly Shutdown _shutdown;

    public ShutdownController(Shutdown shutdown) => _shutdown = shutdown;

    [HttpGet]
    public bool Get() => _shutdown.Requested;
}

public static class UnknownLibraryCapturesDelegateCase
{
    public static IServiceCollection AddUnknownLibraryCapturesDelegate(this IServiceCollection services) =>
        services.AddSingleton<Shutdown>()
                .AddHostedService<ShutdownWatcher>();
}
