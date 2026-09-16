using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.RecursiveSummary;

/// <summary>A recursive method writes a singleton field on every level: its summary must reach a fixed point
/// and still report the write, which races with itself across requests.</summary>
public sealed class DepthProbe
{
    private int _lastDepth;

    public void Walk(int depth)
    {
        _lastDepth = depth;
        if (depth > 0)
        {
            Walk(depth - 1);
        }
    }
}

[ApiController]
[Route("cases/recursive-summary")]
public sealed class ProbeController : ControllerBase
{
    private readonly DepthProbe _probe;

    public ProbeController(DepthProbe probe) => _probe = probe;

    [HttpPost]
    public void Post(int depth) => _probe.Walk(depth);
}

public static class RecursiveSummaryCase
{
    public static IServiceCollection AddRecursiveSummary(this IServiceCollection services) =>
        services.AddSingleton<DepthProbe>();
}
