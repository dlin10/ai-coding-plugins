using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Demo.Web.Cases.ProjectModelKeptObject;

public sealed class Settings
{
    public int Version;
}

[ApiController]
[Route("cases/project-model-kept-object")]
public sealed class SettingsController(IMemoryCache cache) : ControllerBase
{
    private readonly IMemoryCache _cache = cache;

    [HttpPost]
    public void Post()
    {
        var settings = _cache.GetOrCreate("settings", _ => new Settings(), createOptions: null)!;
        settings.Version++;
    }
}

public static class ProjectModelKeptObjectCase
{
    public static IServiceCollection AddProjectModelKeptObject(this IServiceCollection services) => services.AddMemoryCache();
}
