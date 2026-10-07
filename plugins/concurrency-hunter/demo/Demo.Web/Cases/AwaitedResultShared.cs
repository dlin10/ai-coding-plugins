using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.AwaitedResultShared;

public sealed class Tally
{
    public int Hits;
}

/// <summary>An async method that returns the object its singleton holds: every awaiting caller gets the same one.</summary>
public sealed class TallyStore
{
    private readonly Tally _tally = new();

    public async Task<Tally> GetAsync()
    {
        await Task.Yield();
        return _tally;
    }
}

[ApiController]
[Route("cases/awaited-result-shared")]
public sealed class TallyController(TallyStore store) : ControllerBase
{
    private readonly TallyStore _store = store;

    [HttpPost]
    public async Task Post()
    {
        var tally = await _store.GetAsync();
        tally.Hits++;
    }
}

public static class AwaitedResultSharedCase
{
    public static IServiceCollection AddAwaitedResultShared(this IServiceCollection services) => services.AddSingleton<TallyStore>();
}
