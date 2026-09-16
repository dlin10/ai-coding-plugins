using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.NonActionPublicMethod;

public sealed class AuditTrail
{
    public string? LastEntry { get; set; }
}

/// <summary>Negative: a public controller method marked NonAction is not an endpoint, so its write to the
/// singleton has no root.</summary>
[ApiController]
[Route("cases/non-action-public-method")]
public sealed class AuditController : ControllerBase
{
    private readonly AuditTrail _trail;

    public AuditController(AuditTrail trail) => _trail = trail;

    [NonAction]
    public void Record(string entry) => _trail.LastEntry = entry;
}

public static class NonActionPublicMethodCase
{
    public static IServiceCollection AddNonActionPublicMethod(this IServiceCollection services) =>
        services.AddSingleton<AuditTrail>();
}
