using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.RmwSingletonCounter;

public sealed class HitStats
{
    public int Hits { get; set; }
}

/// <summary>The textbook lost update: two concurrent POSTs read the same count and both write it plus one.</summary>
[ApiController]
[Route("cases/rmw-singleton-counter")]
public sealed class HitsController : ControllerBase
{
    private readonly HitStats _stats;

    public HitsController(HitStats stats) => _stats = stats;

    [HttpPost]
    public void Post() => _stats.Hits++;
}

public static class RmwSingletonCounterCase
{
    public static IServiceCollection AddRmwSingletonCounter(this IServiceCollection services) =>
        services.AddSingleton<HitStats>();
}
