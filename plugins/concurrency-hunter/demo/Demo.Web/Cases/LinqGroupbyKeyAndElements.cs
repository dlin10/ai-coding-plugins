using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.LinqGroupbyKeyAndElements;

/// <summary>What an item is grouped by.</summary>
public sealed class Bin
{
    public int Count;
}

/// <summary>An object the stock holds, with the bin it belongs to, set by its own constructor.</summary>
public sealed class Item
{
    public readonly Bin Group;
    public int Value;

    public Item(Bin group) => Group = group;
}

/// <summary>A singleton holding two items, each with a bin of its own, created at construction.</summary>
public sealed class Stock
{
    public readonly List<Item> Items = new();

    public Stock()
    {
        for (var i = 0; i < 2; i++)
            Items.Add(new Item(new Bin()));
    }
}

/// <summary>A group's key is what the key selector returned and its elements are the source's own, so both increments land on objects
/// the singleton holds.</summary>
[ApiController]
[Route("cases/linq-groupby-key-and-elements")]
public sealed class StockController : ControllerBase
{
    private readonly Stock _stock;

    public StockController(Stock stock) => _stock = stock;

    [HttpPost]
    public void Post()
    {
        foreach (var g in _stock.Items.GroupBy(x => x.Group))
        {
            g.Key.Count++;
            foreach (var x in g) x.Value++;
        }
    }
}

public static class LinqGroupbyKeyAndElementsCase
{
    public static IServiceCollection AddLinqGroupbyKeyAndElements(this IServiceCollection services) =>
        services.AddSingleton<Stock>();
}
