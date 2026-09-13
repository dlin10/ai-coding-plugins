namespace Demo.Domain.Cases.RmwThreeLayers;

/// <summary>The bottom of three layers: the lost update lives here, two projects away from the root.</summary>
public sealed class Inventory
{
    private int _reserved;

    public void Reserve(int quantity) => _reserved += quantity;
}
