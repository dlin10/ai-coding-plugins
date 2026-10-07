using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.AwaitedFreshResult;

public sealed class Tally
{
    public int Hits;
}

/// <summary>Hard negative: an async factory returns a new object per call, so each awaiting request writes its own one,
/// as the synchronous twin <c>factory-distinct-call-sites</c> does.</summary>
public sealed class TallyFactory
{
    public async Task<Tally> CreateAsync()
    {
        await Task.Yield();
        return new Tally();
    }
}

[ApiController]
[Route("cases/awaited-fresh-result")]
public sealed class TallyController(TallyFactory factory) : ControllerBase
{
    private readonly TallyFactory _factory = factory;

    [HttpPost]
    public async Task Post()
    {
        var tally = await _factory.CreateAsync();
        tally.Hits++;
    }
}

public static class AwaitedFreshResultCase
{
    public static IServiceCollection AddAwaitedFreshResult(this IServiceCollection services) => services.AddSingleton<TallyFactory>();
}
