using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.TaskRunResultShared;

public sealed class Tally
{
    public int Hits;
}

public sealed class Board
{
    public Tally Tally { get; } = new();
}

/// <summary>The work of <see cref="Task.Run{TResult}(Func{TResult})"/> returns the singleton's object, and the action
/// awaits it: every request writes the same object.</summary>
/// <param name="board">The singleton holding the shared object.</param>
[ApiController]
[Route("cases/task-run-result-shared")]
public sealed class BoardController(Board board) : ControllerBase
{
    private readonly Board _board = board;

    [HttpPost]
    public async Task Post()
    {
        var tally = await Task.Run(() => _board.Tally);
        tally.Hits++;
    }
}

public static class TaskRunResultSharedCase
{
    public static IServiceCollection AddTaskRunResultShared(this IServiceCollection services) => services.AddSingleton<Board>();
}
