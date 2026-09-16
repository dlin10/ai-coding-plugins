using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ConstructionOtherState;

public static class Tracing
{
    public static string? LastSource;
    public static string? LastCulture;
}

public static class Defaults
{
    public static readonly string Culture;

    static Defaults()
    {
        Culture = "en";
        Tracing.LastCulture = "en";
    }
}

/// <summary>Constructions that write state other than the object or type they produce: a lazily resolved
/// singleton's constructor and a type initializer each run once, in an execution that may overlap every
/// root, so their writes to another type's statics race with actions. Their writes to their own object and
/// statics do not (ADR 0006).</summary>
public sealed class Clock
{
    public Clock() => Tracing.LastSource = "clock";

    public string Zone { get; } = "UTC";
}

[ApiController]
[Route("cases/construction-other-state")]
public sealed class StartupController : ControllerBase
{
    private readonly Clock _clock;

    public StartupController(Clock clock) => _clock = clock;

    [HttpGet("zone")]
    public string Zone() => _clock.Zone;

    [HttpGet("source")]
    public string? Source() => Tracing.LastSource;

    [HttpGet("culture")]
    public string Culture() => Defaults.Culture;

    [HttpPut("culture")]
    public void SetCulture(string culture) => Tracing.LastCulture = culture;
}

public static class ConstructionOtherStateCase
{
    public static IServiceCollection AddConstructionOtherState(this IServiceCollection services) =>
        services.AddSingleton<Clock>();
}
