using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ProjectModelOverridesBuiltIn;

public sealed class Payload
{
    public int Count;

    public Payload() => Count = 1;
}

[ApiController]
[Route("cases/project-model-overrides-built-in")]
public sealed class PayloadController : ControllerBase
{
    private readonly Payload _payload;

    public PayloadController(Payload payload) => _payload = payload;

    [HttpGet]
    public int Get() => _payload.Count;
}

public sealed class PayloadWorker : BackgroundService
{
    private readonly Payload _payload;

    public PayloadWorker(Payload payload) => _payload = payload;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_payload);
        return Task.CompletedTask;
    }
}

public static class ProjectModelOverridesBuiltInCase
{
    public static IServiceCollection AddProjectModelOverridesBuiltIn(this IServiceCollection services) =>
        services.AddSingleton<Payload>()
                .AddHostedService<PayloadWorker>();
}
