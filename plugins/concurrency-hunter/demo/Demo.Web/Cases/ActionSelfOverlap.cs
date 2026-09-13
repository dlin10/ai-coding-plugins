using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ActionSelfOverlap;

public sealed class ThemeSettings
{
    public string? Theme { get; set; }
}

/// <summary>One action and nothing else: two concurrent PUTs are the whole race.</summary>
[ApiController]
[Route("cases/action-self-overlap")]
public sealed class ThemeController : ControllerBase
{
    private readonly ThemeSettings _settings;

    public ThemeController(ThemeSettings settings) => _settings = settings;

    [HttpPut]
    public void Put(string theme) => _settings.Theme = theme;
}

public static class ActionSelfOverlapCase
{
    public static IServiceCollection AddActionSelfOverlap(this IServiceCollection services) =>
        services.AddSingleton<ThemeSettings>();
}
