using Demo.Application.Cases.RmwThreeLayers;
using Demo.Domain.Cases.RmwThreeLayers;
using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.RmwThreeLayers;

/// <summary>The root of three layers: Web → Application → Domain, with both services singletons.</summary>
[ApiController]
[Route("cases/rmw-three-layers")]
public sealed class OrdersController : ControllerBase
{
    private readonly OrderService _orders;

    public OrdersController(OrderService orders) => _orders = orders;

    [HttpPost]
    public void Post(int quantity) => _orders.Place(quantity);
}

public static class RmwThreeLayersCase
{
    public static IServiceCollection AddRmwThreeLayers(this IServiceCollection services) =>
        services.AddSingleton<Inventory>()
                .AddSingleton<OrderService>();
}
