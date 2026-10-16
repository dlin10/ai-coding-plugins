namespace Demo.Web.Cases.StructCellWrittenByRefForeach;

public struct Slot
{
    public int Hits;
}

/// <summary>A <c>ref foreach</c> over a span binds each cell of the array itself: the increment through the loop variable writes
/// the singleton's cells, which both workers do.</summary>
public sealed class Board
{
    private readonly Slot[] _slots = new Slot[4];

    public void HitAll()
    {
        Span<Slot> span = _slots;
        foreach (ref var slot in span)
            slot.Hits++;
    }
}

public sealed class FirstWorker : BackgroundService
{
    private readonly Board _board;
    public FirstWorker(Board board) => _board = board;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _board.HitAll();
        return Task.CompletedTask;
    }
}

public sealed class SecondWorker : BackgroundService
{
    private readonly Board _board;
    public SecondWorker(Board board) => _board = board;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _board.HitAll();
        return Task.CompletedTask;
    }
}

public static class StructCellWrittenByRefForeachCase
{
    public static IServiceCollection AddStructCellWrittenByRefForeach(this IServiceCollection services) =>
        services.AddSingleton<Board>()
                .AddHostedService<FirstWorker>()
                .AddHostedService<SecondWorker>();
}
