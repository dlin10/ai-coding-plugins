using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.FromServicesActionParameter;

public sealed class RelayState
{
    public string? Target { get; set; }
}

/// <summary>An action parameter marked FromServices can expose singleton state to overlapping requests.</summary>
[ApiController]
[Route("cases/from-services-action-parameter")]
public sealed class RelayController : ControllerBase
{
    [HttpPost]
    public void Post([FromServices] RelayState relay, string target) => relay.Target = target;
}

public static class FromServicesActionParameterCase
{
    public static IServiceCollection AddFromServicesActionParameter(this IServiceCollection services) =>
        services.AddSingleton<RelayState>();
}
