using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ListElementFieldWrite;

/// <summary>An object a list holds.</summary>
public sealed class Slot
{
    public int Value;
}

/// <summary>A singleton holding a list, filled at construction.</summary>
public sealed class Board
{
    public readonly List<Slot> Items = new();

    public Board() => Items.Add(new Slot());
}

/// <summary>Writes a field of the first element of the singleton's list. What the indexer hands out is the object the list holds, as
/// an array's cell is, so the write lands on that object and meets every other write and read of it (question 30, closed in the
/// second run of phase 5b).</summary>
[ApiController]
[Route("cases/list-element-field-write")]
public sealed class BoardController : ControllerBase
{
    private readonly Board _board;

    public BoardController(Board board) => _board = board;

    [HttpPost]
    public void Post() => _board.Items[0].Value = 1;
}

/// <summary>Reads that field of the same element once.</summary>
public sealed class BoardWorker : BackgroundService
{
    private readonly Board _board;

    public BoardWorker(Board board) => _board = board;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = _board.Items[0].Value;
        return Task.CompletedTask;
    }
}

public static class ListElementFieldWriteCase
{
    public static IServiceCollection AddListElementFieldWrite(this IServiceCollection services) =>
        services.AddSingleton<Board>()
                .AddHostedService<BoardWorker>();
}
