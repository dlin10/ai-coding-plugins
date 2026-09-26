using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.GapMaterialityOrder;

public sealed class Slot
{
    public int Value;
}

/// <summary>A singleton that reaches two regions, itself and its slot, handed to one library call from three actions.</summary>
public sealed class Board
{
    public readonly Slot Slot = new();
}

/// <summary>A singleton of one region, handed to another library call from one action.</summary>
public sealed class Tally
{
    public int Count;
}

/// <summary>Two semantic gaps: <c>Console.WriteLine(object)</c> reaches three roots and two regions, <c>Console.Write(object)</c> one root
/// and one region, so the first comes first in coverage (TD-039a).</summary>
[ApiController]
[Route("cases/gap-materiality-order")]
public sealed class BoardController : ControllerBase
{
    private readonly Board _board;
    private readonly Tally _tally;

    public BoardController(Board board, Tally tally)
    {
        _board = board;
        _tally = tally;
    }

    [HttpGet]
    public void Get() => Console.WriteLine(_board);

    [HttpPost]
    public void Post() => Console.WriteLine(_board);

    [HttpPut]
    public void Put() => Console.WriteLine(_board);

    [HttpDelete]
    public void Delete() => Console.Write(_tally);
}

public static class GapMaterialityOrderCase
{
    public static IServiceCollection AddGapMaterialityOrder(this IServiceCollection services) =>
        services.AddSingleton<Board>()
                .AddSingleton<Tally>();
}
