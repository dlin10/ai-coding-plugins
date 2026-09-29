using System.Collections;
using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ExplicitEnumeratorRunsBody;

public sealed class Item
{
    public int Seen;
}

public sealed class Store
{
    public Store() => Items = new List<Item> { new Item() };

    public List<Item> Items { get; }
}

public static class Feed
{
    public static IEnumerable<Item> Touch(List<Item> items)
    {
        foreach (var item in items)
        {
            item.Seen++;
            yield return item;
        }
    }
}

public sealed class Crate : IEnumerable<Item>
{
    private readonly List<Item> _items;

    public Crate() => _items = new List<Item>();

    public IEnumerator<Item> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

[ApiController]
[Route("cases/explicit-enumerator-runs-body")]
public sealed class FeedController : ControllerBase
{
    private readonly Store _store;
    private readonly Crate _crate;

    public FeedController(Store store, Crate crate)
    {
        _store = store;
        _crate = crate;
    }

    [HttpPost]
    public void Post()
    {
        using var walk = Feed.Touch(_store.Items).GetEnumerator();
        while (walk.MoveNext()) { }
    }

    [HttpGet]
    public int Get()
    {
        var count = 0;
        foreach (var _ in _crate)
            count++;
        return count;
    }
}

public static class ExplicitEnumeratorRunsBodyCase
{
    public static IServiceCollection AddExplicitEnumeratorRunsBody(this IServiceCollection services) =>
        services.AddSingleton<Store>().AddSingleton<Crate>();
}
