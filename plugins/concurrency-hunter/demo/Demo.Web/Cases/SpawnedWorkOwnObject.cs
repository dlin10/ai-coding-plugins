using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.SpawnedWorkOwnObject;

public sealed class Tally
{
    public int Count;
}

/// <summary>Hard negative (ADR 0020): the request hands its own object to one task, awaits it and reads the object
/// after. Each request makes its own object, and within one request the await orders the two.</summary>
[ApiController]
[Route("cases/spawned-work-own-object")]
public sealed class TallyController : ControllerBase
{
    [HttpPost]
    public async Task<int> Post()
    {
        var tally = new Tally();
        await Task.Run(() => { tally.Count++; });
        return tally.Count;
    }
}
