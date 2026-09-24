using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.JsonSerializeReadsDeep;

public sealed class Limits
{
    public int Max;
}

/// <summary>A singleton whose nested object the serializer reads with every other field it reaches: a deep read of the known
/// call, not an opaque call that touches nothing.</summary>
public sealed class Settings
{
    public readonly Limits Limits = new();
}

public sealed class LimitsWorker : BackgroundService
{
    private readonly Settings _settings;

    public LimitsWorker(Settings settings) => _settings = settings;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _settings.Limits.Max = 100;
        return Task.CompletedTask;
    }
}

[ApiController]
[Route("cases/json-serialize-reads-deep")]
public sealed class SettingsController : ControllerBase
{
    private readonly Settings _settings;

    public SettingsController(Settings settings) => _settings = settings;

    [HttpGet]
    public string Get() => JsonSerializer.Serialize(_settings);
}

public static class JsonSerializeReadsDeepCase
{
    public static IServiceCollection AddJsonSerializeReadsDeep(this IServiceCollection services) =>
        services.AddSingleton<Settings>()
                .AddHostedService<LimitsWorker>();
}
