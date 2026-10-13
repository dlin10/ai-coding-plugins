using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.SpawnedWorkInALoop;

public sealed class Tally
{
    public int Count;
}

/// <summary>One spawn site in a loop starts its work three times in one request: the work overlaps itself on the
/// request's own object.</summary>
[ApiController]
[Route("cases/spawned-work-in-a-loop")]
public sealed class TallyController : ControllerBase
{
    [HttpPost]
    public async Task Post()
    {
        var tally = new Tally();
        var tasks = new Task[3];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() => { tally.Count++; });
        }

        await Task.WhenAll(tasks);
    }
}
