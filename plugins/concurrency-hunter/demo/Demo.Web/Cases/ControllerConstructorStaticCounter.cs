using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ControllerConstructorStaticCounter;

/// <summary>A controller constructor runs once per request, inside each action, so its increment of a static
/// counter is a read-modify-write that races across requests (ADR 0006).</summary>
[ApiController]
[Route("cases/controller-constructor-static-counter")]
public sealed class CounterController : ControllerBase
{
    private static int _created;

    public CounterController() => _created++;

    [HttpGet]
    public string Get() => "ok";
}

public static class ControllerConstructorStaticCounterCase
{
    public static IServiceCollection AddControllerConstructorStaticCounter(this IServiceCollection services) =>
        services;
}
