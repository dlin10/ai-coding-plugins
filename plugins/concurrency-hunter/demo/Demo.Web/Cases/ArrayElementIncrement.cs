using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ArrayElementIncrement;

/// <summary>A singleton holding an array of counters.</summary>
public sealed class SlotBoard
{
    public const int Size = 8;

    public int[] Slots { get; } = new int[Size];
}

/// <summary>Increments the counter at the index the request names. An increment of an array element is its long form, a read of the
/// element and a write of it plus one, so the write depends on the read of its own cell and the two are one read-modify-write
/// (question 33, closed in the third run of phase 5b).</summary>
[ApiController]
[Route("cases/array-element-increment")]
public sealed class SlotController : ControllerBase
{
    private readonly SlotBoard _board;

    public SlotController(SlotBoard board) => _board = board;

    [HttpPost]
    public void Post(int i) => _board.Slots[i]++;
}

/// <summary>Increments every counter once, in a worker that runs once.</summary>
public sealed class SlotWorker : BackgroundService
{
    private readonly SlotBoard _board;

    public SlotWorker(SlotBoard board) => _board = board;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var i = 0; i < SlotBoard.Size; i++)
            _board.Slots[i]++;
        return Task.CompletedTask;
    }
}

public static class ArrayElementIncrementCase
{
    public static IServiceCollection AddArrayElementIncrement(this IServiceCollection services) =>
        services.AddSingleton<SlotBoard>()
                .AddHostedService<SlotWorker>();
}
