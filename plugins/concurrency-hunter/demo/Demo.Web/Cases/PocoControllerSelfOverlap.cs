using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.PocoControllerSelfOverlap;

public sealed class ShelfState
{
    public string? Label { get; set; }
}

/// <summary>A controller action can overlap itself even when its controller has no MVC base class.</summary>
[Route("cases/poco-controller-self-overlap")]
public sealed class ShelfController
{
    private readonly ShelfState _state;

    public ShelfController(ShelfState state) => _state = state;

    [HttpPut]
    public void Put(string label) => _state.Label = label;
}

public static class PocoControllerSelfOverlapCase
{
    public static IServiceCollection AddPocoControllerSelfOverlap(this IServiceCollection services) =>
        services.AddSingleton<ShelfState>();
}
