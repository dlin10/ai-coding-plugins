using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.InterfaceAddAndCount;

/// <summary>An object the list holds.</summary>
public sealed class Item
{
    public int Value;
}

/// <summary>A singleton holding one list, which it exposes both as a collection to add to and as a read-only one to count.</summary>
public sealed class Drawer
{
    public ICollection<Item> Writable { get; }

    public IReadOnlyCollection<Item> Readable { get; }

    public Drawer()
    {
        var items = new List<Item>();
        Writable = items;
        Readable = items;
    }
}

/// <summary>Adds through one interface while the worker counts through the other. Both calls are decided by the list the heap knows
/// their receiver to be, so they are the list's own <c>Add</c> and <c>Count</c>, and meet on its structure and its cells as the same
/// members called on the list directly do (question 31, closed in the third run of phase 5b).</summary>
[ApiController]
[Route("cases/interface-add-and-count")]
public sealed class DrawerController : ControllerBase
{
    private readonly Drawer _drawer;

    public DrawerController(Drawer drawer) => _drawer = drawer;

    [HttpPost]
    public void Post() => _drawer.Writable.Add(new Item());
}

/// <summary>Counts the list, in a worker that runs once.</summary>
public sealed class DrawerWorker : BackgroundService
{
    private readonly Drawer _drawer;

    public DrawerWorker(Drawer drawer) => _drawer = drawer;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = _drawer.Readable.Count;
        return Task.CompletedTask;
    }
}

public static class InterfaceAddAndCountCase
{
    public static IServiceCollection AddInterfaceAddAndCount(this IServiceCollection services) =>
        services.AddSingleton<Drawer>()
                .AddHostedService<DrawerWorker>();
}
