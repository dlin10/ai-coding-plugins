using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.DelegateField;

/// <summary>The write is reached only through a delegate stored in a field.</summary>
public sealed class VisitTracker
{
    private readonly Action<string> _onVisit;
    private string? _lastPath;

    public VisitTracker() => _onVisit = Remember;

    public void Visit(string path) => _onVisit(path);

    private void Remember(string path) => _lastPath = path;
}

[ApiController]
[Route("cases/delegate-field")]
public sealed class TrackerController : ControllerBase
{
    private readonly VisitTracker _tracker;

    public TrackerController(VisitTracker tracker) => _tracker = tracker;

    [HttpPost]
    public void Post(string path) => _tracker.Visit(path);
}

public static class DelegateFieldCase
{
    public static IServiceCollection AddDelegateField(this IServiceCollection services) =>
        services.AddSingleton<VisitTracker>();
}
