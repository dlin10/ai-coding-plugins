using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.StaleReadWithoutDependency;

/// <summary>A read and a write of one field in one method, where the written value does not depend on the
/// read: a plain data race, not a read-modify-write.</summary>
public sealed class Headline
{
    private string? _text;

    public string? Replace(string text)
    {
        var previous = _text;
        _text = text;
        return previous;
    }
}

[ApiController]
[Route("cases/stale-read-without-dependency")]
public sealed class HeadlineController : ControllerBase
{
    private readonly Headline _headline;

    public HeadlineController(Headline headline) => _headline = headline;

    [HttpPut]
    public string? Put(string text) => _headline.Replace(text);
}

public static class StaleReadWithoutDependencyCase
{
    public static IServiceCollection AddStaleReadWithoutDependency(this IServiceCollection services) =>
        services.AddSingleton<Headline>();
}
