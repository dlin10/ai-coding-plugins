using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.CapturedCounter;

/// <summary>Two tasks of one request increment the variable they both capture: its cell is one location of the
/// request's closure, and the two tasks run concurrently.</summary>
[ApiController]
[Route("cases/captured-counter")]
public sealed class CounterController : ControllerBase
{
    [HttpPost]
    public async Task Post()
    {
        var count = 0;
        var first = Task.Run(() => { count++; });
        var second = Task.Run(() => { count++; });
        await Task.WhenAll(first, second);
    }
}
