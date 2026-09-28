using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.LinqTakeKeepsElements;

/// <summary>An object the shelf's list holds.</summary>
public sealed class Slot
{
    public int Value;
}

/// <summary>A singleton holding two slots, created at construction.</summary>
public sealed class Shelf
{
    public readonly List<Slot> Items = new();

    public Shelf()
    {
        for (var i = 0; i < 2; i++)
            Items.Add(new Slot());
    }
}

/// <summary><c>Take</c> yields the list's own elements, so the write through the loop variable lands on a slot the list holds; <c>Take</c>
/// reads its source deep where the sequence is enumerated, in this action.</summary>
[ApiController]
[Route("cases/linq-take-keeps-elements")]
public sealed class ShelfController : ControllerBase
{
    private readonly Shelf _shelf;

    public ShelfController(Shelf shelf) => _shelf = shelf;

    [HttpPost]
    public void Post()
    {
        foreach (var item in _shelf.Items.Take(1)) item.Value = 1;
    }
}

/// <summary>Reads the first slot once.</summary>
public sealed class ShelfWorker : BackgroundService
{
    private readonly Shelf _shelf;

    public ShelfWorker(Shelf shelf) => _shelf = shelf;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var value = _shelf.Items[0].Value;
        return value > 0 ? Task.CompletedTask : Task.CompletedTask;
    }
}

public static class LinqTakeKeepsElementsCase
{
    public static IServiceCollection AddLinqTakeKeepsElements(this IServiceCollection services) =>
        services.AddSingleton<Shelf>()
                .AddHostedService<ShelfWorker>();
}
