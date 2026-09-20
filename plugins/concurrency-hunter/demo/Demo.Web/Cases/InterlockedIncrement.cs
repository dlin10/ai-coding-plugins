using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.InterlockedIncrement;

/// <summary>Every POST bumps one static counter through <see cref="Interlocked.Increment(ref int)"/>: the whole
/// read-modify-write happens on one location atomically, so the overlapping actions are not a race.</summary>
[ApiController]
[Route("cases/interlocked-increment")]
public sealed class TicketController : ControllerBase
{
    private static int _issued;

    [HttpPost]
    public void Post() => Interlocked.Increment(ref _issued);
}
