using System.Collections;
using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.EnumeratorMethodRunsAtMovenext;

public sealed class Item
{
    public int Seen;
}

public sealed class Shelf : IEnumerable<Item>
{
    private readonly List<Item> _items;

    public Shelf() => _items = new List<Item> { new Item() };

    public IEnumerator<Item> GetEnumerator()
    {
        foreach (var item in _items)
        {
            item.Seen++;
            yield return item;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

[ApiController]
[Route("cases/enumerator-method-runs-at-movenext")]
public sealed class ShelfController : ControllerBase
{
    private readonly Shelf _shelf;

    public ShelfController(Shelf shelf) => _shelf = shelf;

    [HttpPost]
    public void Post()
    {
        foreach (var _ in _shelf) { }
    }
}

public static class EnumeratorMethodRunsAtMovenextCase
{
    public static IServiceCollection AddEnumeratorMethodRunsAtMovenext(this IServiceCollection services) =>
        services.AddSingleton<Shelf>();
}
