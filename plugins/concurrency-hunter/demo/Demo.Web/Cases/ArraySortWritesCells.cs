using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ArraySortWritesCells;

public sealed class Item
{
    public int Rank;
}

/// <summary>The singleton's cells are sorted by requests and read by its worker.</summary>
public sealed class Board
{
    public readonly Item[] Cells = new Item[2];
    public int Compared;

    public Board()
    {
        Cells[0] = new Item();
        Cells[1] = new Item();
    }
}

[ApiController]
[Route("cases/array-sort-writes-cells")]
public sealed class BoardController : ControllerBase
{
    private readonly Board _board;

    public BoardController(Board board) => _board = board;

    [HttpPost]
    public void Post() => System.Array.Sort(_board.Cells, (a, b) => { _board.Compared++; return a.Rank - b.Rank; });
}

public sealed class BoardWorker : BackgroundService
{
    private readonly Board _board;

    public BoardWorker(Board board) => _board = board;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var item in _board.Cells) _ = item.Rank;
        return Task.CompletedTask;
    }
}

public static class ArraySortWritesCellsCase
{
    public static IServiceCollection AddArraySortWritesCells(this IServiceCollection services) =>
        services.AddSingleton<Board>().AddHostedService<BoardWorker>();
}
