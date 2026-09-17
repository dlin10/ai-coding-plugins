using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.LocatorScopedViaCreateScope;

public sealed class JobState
{
    public string? Step { get; set; }
}

/// <summary>A scoped service resolved from a scope the worker creates is its own instance, never the
/// request's: the action and the worker write two different objects.</summary>
[ApiController]
[Route("cases/locator-scoped-via-create-scope")]
public sealed class JobController : ControllerBase
{
    private readonly JobState _state;

    public JobController(JobState state) => _state = state;

    [HttpPost]
    public void Post() => _state.Step = "request";
}

public sealed class JobWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;

    public JobWorker(IServiceScopeFactory scopes) => _scopes = scopes;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<JobState>().Step = "worker";
        return Task.CompletedTask;
    }
}

public static class LocatorScopedViaCreateScopeCase
{
    public static IServiceCollection AddLocatorScopedViaCreateScope(this IServiceCollection services) =>
        services.AddScoped<JobState>()
                .AddHostedService<JobWorker>();
}
