using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.HostedStartVsAction;

public sealed class WarmupState
{
    public string? Stage { get; set; }
}

/// <summary>A hosted-service startup write that overlaps a controller action reading the same singleton.</summary>
public sealed class WarmupService : IHostedService
{
    private readonly WarmupState _state;

    public WarmupService(WarmupState state) => _state = state;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _state.Stage = "started";
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[ApiController]
[Route("cases/hosted-start-vs-action")]
public sealed class WarmupController : ControllerBase
{
    private readonly WarmupState _state;

    public WarmupController(WarmupState state) => _state = state;

    [HttpGet]
    public string? Get() => _state.Stage;
}

public static class HostedStartVsActionCase
{
    public static IServiceCollection AddHostedStartVsAction(this IServiceCollection services) =>
        services.AddSingleton<WarmupState>()
                .AddHostedService<WarmupService>();
}
