using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.CapturedStruct;

public struct State
{
    public int Count;
}

/// <summary>A captured struct lives in its capture cell: two tasks of one request increment its field in place.</summary>
[ApiController]
[Route("cases/captured-struct")]
public sealed class StateController : ControllerBase
{
    [HttpPost]
    public async Task Post()
    {
        State state = default;
        var first = Task.Run(() => { state.Count++; });
        var second = Task.Run(() => { state.Count++; });
        await Task.WhenAll(first, second);
    }
}
