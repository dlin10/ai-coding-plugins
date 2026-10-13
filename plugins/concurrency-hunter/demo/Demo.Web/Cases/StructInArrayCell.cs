namespace Demo.Web.Cases.StructInArrayCell;

public struct Slot
{
    public int Hits;
}

/// <summary>A struct array keeps its structs in its cells: one worker increments cell 0's field directly, the other
/// through a span over the same array, and both reach the same storage.</summary>
public sealed class Board
{
    private readonly Slot[] _slots = new Slot[4];

    public void Hit() => _slots[0].Hits++;

    public void HitThroughSpan()
    {
        Span<Slot> span = _slots;
        span[0].Hits++;
    }
}

public sealed class HitWorker : BackgroundService
{
    private readonly Board _board;
    public HitWorker(Board board) => _board = board;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _board.Hit();
        return Task.CompletedTask;
    }
}

public sealed class SpanWorker : BackgroundService
{
    private readonly Board _board;
    public SpanWorker(Board board) => _board = board;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _board.HitThroughSpan();
        return Task.CompletedTask;
    }
}

public static class StructInArrayCellCase
{
    public static IServiceCollection AddStructInArrayCell(this IServiceCollection services) =>
        services.AddSingleton<Board>()
                .AddHostedService<HitWorker>()
                .AddHostedService<SpanWorker>();
}
