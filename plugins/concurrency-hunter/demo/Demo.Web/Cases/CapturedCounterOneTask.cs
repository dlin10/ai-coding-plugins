using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.CapturedCounterOneTask;

/// <summary>Hard negative: one task of the request increments the captured variable and the request reads it after
/// the await; each request has its own cell.</summary>
[ApiController]
[Route("cases/captured-counter-one-task")]
public sealed class CounterController : ControllerBase
{
    [HttpPost]
    public async Task<int> Post()
    {
        var count = 0;
        await Task.Run(() => count++);
        return count;
    }
}
