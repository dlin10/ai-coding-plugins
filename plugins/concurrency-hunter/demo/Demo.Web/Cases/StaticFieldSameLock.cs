using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.StaticFieldSameLock;

/// <summary>Hard negative: the same static as the unlocked case, but every access holds one static lock.</summary>
[ApiController]
[Route("cases/static-field-same-lock")]
public sealed class GuardedVisitorController : ControllerBase
{
    private static readonly object Gate = new();
    private static string? _lastVisitor;

    [HttpPost]
    public void Post(string name)
    {
        lock (Gate)
        {
            _lastVisitor = name;
        }
    }

    [HttpGet]
    public string? Get()
    {
        lock (Gate)
        {
            return _lastVisitor;
        }
    }
}
