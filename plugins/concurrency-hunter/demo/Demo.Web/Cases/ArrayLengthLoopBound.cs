using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ArrayLengthLoopBound;

public sealed class SlotBoard
{
    public const int Size = 8;

    public int[] Slots { get; } = new int[Size];
}

[ApiController]
[Route("cases/array-length-loop-bound")]
public sealed class SlotController : ControllerBase
{
    private readonly SlotBoard _board;

    public SlotController(SlotBoard board) => _board = board;

    [HttpPost]
    public void Post(int i)
    {
        if (i < _board.Slots.Length)
            _board.Slots[i]++;
    }
}

public sealed class SlotWorker : BackgroundService
{
    private readonly SlotBoard _board;

    public SlotWorker(SlotBoard board) => _board = board;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var i = 0; i < _board.Slots.Length; i++)
            _board.Slots[i]++;
        return Task.CompletedTask;
    }
}

public static class ArrayLengthLoopBoundCase
{
    public static IServiceCollection AddArrayLengthLoopBound(this IServiceCollection services) =>
        services.AddSingleton<SlotBoard>()
                .AddHostedService<SlotWorker>();
}
