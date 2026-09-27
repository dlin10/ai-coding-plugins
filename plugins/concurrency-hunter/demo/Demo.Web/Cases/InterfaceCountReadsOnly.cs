using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.InterfaceCountReadsOnly;

/// <summary>An object the list holds.</summary>
public sealed class Item
{
    public int Value;
}

/// <summary>A singleton holding a list filled at construction, which it exposes only as a read-only collection.</summary>
public sealed class Shelf
{
    public IReadOnlyCollection<Item> Items { get; }

    public Shelf() => Items = new List<Item> { new Item() };
}

/// <summary>Counts the list through the interface it is exposed as. The call names an interface member, and the object the heap knows
/// the receiver to be is a list, so it is the list's own <c>Count</c>: a read of the list's structure and nothing else, which no other
/// read makes a pair with (question 31, closed in the third run of phase 5b).</summary>
[ApiController]
[Route("cases/interface-count-reads-only")]
public sealed class ShelfController : ControllerBase
{
    private readonly Shelf _shelf;

    public ShelfController(Shelf shelf) => _shelf = shelf;

    [HttpPost]
    public int Post() => _shelf.Items.Count;
}

/// <summary>The same count, in a worker that runs once.</summary>
public sealed class ShelfWorker : BackgroundService
{
    private readonly Shelf _shelf;

    public ShelfWorker(Shelf shelf) => _shelf = shelf;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = _shelf.Items.Count;
        return Task.CompletedTask;
    }
}

public static class InterfaceCountReadsOnlyCase
{
    public static IServiceCollection AddInterfaceCountReadsOnly(this IServiceCollection services) =>
        services.AddSingleton<Shelf>()
                .AddHostedService<ShelfWorker>();
}
