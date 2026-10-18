using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.KeptCallbackStaysUnknown;

/// <summary>A singleton the callback writes and the other action reads.</summary>
public sealed class Aborts
{
    public bool Seen;
}

/// <summary>Registers a callback that writes the singleton on <c>HttpContext.RequestAborted</c>. The token is a member of
/// <c>System.Private.CoreLib</c>, which no run generates, so the registration stays an unknown call: the framework keeps the callback and
/// runs it whenever it likes, in an execution no root names, and it meets the read of <c>Seen</c> in <c>Get</c> (open-world rule).</summary>
[ApiController]
[Route("cases/kept-callback-stays-unknown")]
public sealed class AbortsController : ControllerBase
{
    private readonly Aborts _aborts;

    public AbortsController(Aborts aborts) => _aborts = aborts;

    [HttpPost]
    public void Post() => HttpContext.RequestAborted.Register(() => _aborts.Seen = true);

    [HttpGet]
    public bool Get() => _aborts.Seen;
}

public static class KeptCallbackStaysUnknownCase
{
    public static IServiceCollection AddKeptCallbackStaysUnknown(this IServiceCollection services) => services.AddSingleton<Aborts>();
}
