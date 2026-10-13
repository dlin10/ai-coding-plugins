using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.SpawnedWorkObjectPublished;

public sealed class Tally
{
    public int Count;
}

public sealed class Registry
{
    public Tally? Last;
}

/// <summary>The request's object, stored by its work into a singleton: a shared root reaches it, so it is not
/// confined to the request, and the work of two requests meets on it and on the singleton's slot.</summary>
[ApiController]
[Route("cases/spawned-work-object-published")]
public sealed class TallyController : ControllerBase
{
    private readonly Registry _registry;

    public TallyController(Registry registry) => _registry = registry;

    [HttpPost]
    public async Task Post()
    {
        var tally = new Tally();
        await Task.Run(() =>
        {
            tally.Count++;
            _registry.Last = tally;
        });
    }
}

public static class SpawnedWorkObjectPublishedCase
{
    public static IServiceCollection AddSpawnedWorkObjectPublished(this IServiceCollection services) =>
        services.AddSingleton<Registry>();
}
