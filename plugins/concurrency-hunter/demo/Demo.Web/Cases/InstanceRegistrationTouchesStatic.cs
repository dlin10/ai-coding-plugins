using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.InstanceRegistrationTouchesStatic;

public sealed class SeedList
{
    public static string? Origin;

    public SeedList() => Origin = "startup";
}

/// <summary>An instance registration runs its constructor during startup, before any root: the static it
/// writes there does not race with the action that reads it.</summary>
[ApiController]
[Route("cases/instance-registration-touches-static")]
public sealed class SeedController : ControllerBase
{
    [HttpGet]
    public string? Get() => SeedList.Origin;
}

public static class InstanceRegistrationTouchesStaticCase
{
    public static IServiceCollection AddInstanceRegistrationTouchesStatic(this IServiceCollection services) =>
        services.AddSingleton(new SeedList());
}
