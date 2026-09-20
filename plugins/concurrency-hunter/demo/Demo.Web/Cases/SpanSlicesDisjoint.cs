namespace Demo.Web.Cases.SpanSlicesDisjoint;

/// <summary>Two workers write through two slices of one buffer. The slices share an underlying region, but their
/// offsets and lengths do not overlap, so no byte is written twice.</summary>
public sealed class Frame
{
    private const int Half = 8;

    private readonly byte[] _buffer = new byte[Half * 2];

    public void FillLow()
    {
        var low = _buffer.AsSpan(0, Half);
        for (var i = 0; i < low.Length; i++)
            low[i] = 1;
    }

    public void FillHigh()
    {
        var high = _buffer.AsSpan(Half, Half);
        for (var i = 0; i < high.Length; i++)
            high[i] = 2;
    }
}

public sealed class LowSliceWorker : BackgroundService
{
    private readonly Frame _frame;

    public LowSliceWorker(Frame frame) => _frame = frame;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _frame.FillLow();
        return Task.CompletedTask;
    }
}

public sealed class HighSliceWorker : BackgroundService
{
    private readonly Frame _frame;

    public HighSliceWorker(Frame frame) => _frame = frame;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _frame.FillHigh();
        return Task.CompletedTask;
    }
}

public static class SpanSlicesDisjointCase
{
    public static IServiceCollection AddSpanSlicesDisjoint(this IServiceCollection services) =>
        services.AddSingleton<Frame>()
                .AddHostedService<LowSliceWorker>()
                .AddHostedService<HighSliceWorker>();
}
