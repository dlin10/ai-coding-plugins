using Demo.Domain.Cases.RmwThreeLayers;

namespace Demo.Application.Cases.RmwThreeLayers;

/// <summary>The middle of three layers: no state of its own, only the call through.</summary>
public sealed class OrderService
{
    private readonly Inventory _inventory;

    public OrderService(Inventory inventory) => _inventory = inventory;

    public void Place(int quantity) => _inventory.Reserve(quantity);
}
