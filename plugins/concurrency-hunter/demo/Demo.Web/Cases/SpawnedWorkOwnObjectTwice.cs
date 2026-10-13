using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.SpawnedWorkOwnObjectTwice;

public sealed class Tally
{
    public int Count;
}

/// <summary>The request's own object, handed to two tasks it starts: the two tasks of one request overlap each
/// other, though no other request ever reaches the object.</summary>
[ApiController]
[Route("cases/spawned-work-own-object-twice")]
public sealed class TallyController : ControllerBase
{
    [HttpPost]
    public async Task Post()
    {
        var tally = new Tally();
        var first = Task.Run(() => { tally.Count++; });
        var second = Task.Run(() => { tally.Count++; });
        await Task.WhenAll(first, second);
    }
}
