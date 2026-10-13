using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.StaticStructField;

public struct Stats
{
    public int Hits;
}

/// <summary>A static struct is static storage: two overlapping POSTs increment its field and lose updates.</summary>
[ApiController]
[Route("cases/static-struct-field")]
public sealed class StatsController : ControllerBase
{
    private static Stats s_stats;

    [HttpPost]
    public void Post() => s_stats.Hits++;
}
