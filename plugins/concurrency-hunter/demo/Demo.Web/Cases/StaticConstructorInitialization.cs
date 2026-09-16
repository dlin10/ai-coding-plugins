using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.StaticConstructorInitialization;

/// <summary>Hard negative: a static written only by its own type initializer, which runs once before any
/// read, and read by an action (ADR 0006).</summary>
public static class RegionDefaults
{
    internal static string DefaultRegion;

    static RegionDefaults()
    {
        DefaultRegion = "eu";
    }
}

[ApiController]
[Route("cases/static-constructor-initialization")]
public sealed class RegionController : ControllerBase
{
    [HttpGet]
    public string Get() => RegionDefaults.DefaultRegion;
}

public static class StaticConstructorInitializationCase
{
    public static IServiceCollection AddStaticConstructorInitialization(this IServiceCollection services) =>
        services;
}
