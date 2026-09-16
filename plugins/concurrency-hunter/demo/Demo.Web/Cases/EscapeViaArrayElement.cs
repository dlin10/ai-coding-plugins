using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.EscapeViaArrayElement;

public sealed class Slot
{
    public string? Label { get; set; }
}

public sealed class SlotBoard
{
    public Slot?[] Slots { get; } = new Slot?[4];
}

/// <summary>A fresh object escapes by being stored in an array element of a singleton: the worker keeps
/// writing it while an action reads it through the array.</summary>
public sealed class SlotWorker : BackgroundService
{
    private readonly SlotBoard _board;

    public SlotWorker(SlotBoard board) => _board = board;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var slot = new Slot();
        _board.Slots[0] = slot;
        slot.Label = "worker";
        return Task.CompletedTask;
    }
}

[ApiController]
[Route("cases/escape-via-array-element")]
public sealed class SlotController : ControllerBase
{
    private readonly SlotBoard _board;

    public SlotController(SlotBoard board) => _board = board;

    [HttpGet]
    public string? Get() => _board.Slots[0]!.Label;
}

public static class EscapeViaArrayElementCase
{
    public static IServiceCollection AddEscapeViaArrayElement(this IServiceCollection services) =>
        services.AddSingleton<SlotBoard>()
                .AddHostedService<SlotWorker>();
}
