using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ConstructorLeaksThis;

public static class BeaconRegistry
{
    public static Beacon? Last;
}

/// <summary>A singleton constructor publishes this in a static before it finishes, so its writes to the
/// registry slot and to its own property race with an action that reads them (ADR 0006).</summary>
public sealed class Beacon
{
    public Beacon()
    {
        BeaconRegistry.Last = this;
        Status = "starting";
    }

    public string? Status { get; set; }
}

[ApiController]
[Route("cases/constructor-leaks-this")]
public sealed class BeaconController : ControllerBase
{
    [HttpGet]
    public string? Get([FromServices] Beacon beacon) => BeaconRegistry.Last!.Status;
}

public static class ConstructorLeaksThisCase
{
    public static IServiceCollection AddConstructorLeaksThis(this IServiceCollection services) =>
        services.AddSingleton<Beacon>();
}
