using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.VolatileRmwNotAtomic;

/// <summary>`volatile` orders the read and the write but never fuses them: two overlapping POSTs can read the same
/// count and both store it plus one, so the increment still loses updates.</summary>
[ApiController]
[Route("cases/volatile-rmw-not-atomic")]
public sealed class HitCountController : ControllerBase
{
    private static volatile int _hits;

    [HttpPost]
    public void Post() => _hits++;
}
