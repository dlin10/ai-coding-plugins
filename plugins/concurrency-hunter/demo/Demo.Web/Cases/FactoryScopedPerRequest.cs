using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.FactoryScopedPerRequest;

public sealed class RequestTag
{
    public string? Id { get; set; }
}

/// <summary>A scoped service registered through a factory lambda is still one instance per request: the two
/// actions never write the same object.</summary>
[ApiController]
[Route("cases/factory-scoped-per-request")]
public sealed class TagController : ControllerBase
{
    private readonly RequestTag _tag;

    public TagController(RequestTag tag) => _tag = tag;

    [HttpPost("first")]
    public void First() => _tag.Id = "first";

    [HttpPost("second")]
    public void Second() => _tag.Id = "second";
}

public static class FactoryScopedPerRequestCase
{
    public static IServiceCollection AddFactoryScopedPerRequest(this IServiceCollection services) =>
        services.AddScoped(_ => new RequestTag());
}
