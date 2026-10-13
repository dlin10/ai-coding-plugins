using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.CapturedStructCopied;

public struct State
{
    public int Count;
}

/// <summary>Hard negative: two tasks of one request copy the captured struct into their own locals and increment
/// the copies. Both only read the capture cell.</summary>
[ApiController]
[Route("cases/captured-struct-copied")]
public sealed class StateController : ControllerBase
{
    [HttpPost]
    public async Task Post()
    {
        var state = new State();
        var first = Task.Run(() => { var c = state; c.Count++; });
        var second = Task.Run(() => { var c = state; c.Count++; });
        await Task.WhenAll(first, second);
    }
}
