using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.StructWithCopiesCapturedVariable;

public struct State
{
    public int Count;
}

/// <summary>Hard negative: two tasks of one request take a <c>with</c> of the captured struct. The clone is a copy whose field the
/// initializer sets; both tasks only read the capture cell.</summary>
[ApiController]
[Route("cases/struct-with-copies-captured-variable")]
public sealed class StateController : ControllerBase
{
    [HttpPost]
    public async Task<int> Post()
    {
        var state = new State();
        var first = Task.Run(() => (state with { Count = 1 }).Count);
        var second = Task.Run(() => (state with { Count = 2 }).Count);
        return (await first) + (await second);
    }
}
