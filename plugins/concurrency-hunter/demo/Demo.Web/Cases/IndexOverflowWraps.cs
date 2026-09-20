namespace Demo.Web.Cases.IndexOverflowWraps;

/// <summary>Two workers write the cell a cast to <see cref="byte"/> names. Read as algebra the two indices differ by a whole
/// turn of the type and could never be equal; read as the language runs them the second wraps onto the first, so the two
/// always write one cell. The solver decides it in the width of the type, which is why the conversion is modelled rather than
/// simplified away (TD-043, ADR 0004).</summary>
public sealed class Frame
{
    private const int Slots = 256;

    private readonly string?[] _slots = new string?[Slots];

    public void WriteDirect(int index) => _slots[(byte)index] = "direct";

    public void WriteWrapped(int index) => _slots[(byte)(index + 256)] = "wrapped";
}

public sealed class DirectFrameWorker : BackgroundService
{
    private readonly Frame _frame;

    public DirectFrameWorker(Frame frame) => _frame = frame;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _frame.WriteDirect(Environment.ProcessorCount);
        return Task.CompletedTask;
    }
}

public sealed class WrappedFrameWorker : BackgroundService
{
    private readonly Frame _frame;

    public WrappedFrameWorker(Frame frame) => _frame = frame;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _frame.WriteWrapped(Environment.TickCount);
        return Task.CompletedTask;
    }
}

public static class IndexOverflowWrapsCase
{
    public static IServiceCollection AddIndexOverflowWraps(this IServiceCollection services) =>
        services.AddSingleton<Frame>()
                .AddHostedService<DirectFrameWorker>()
                .AddHostedService<WrappedFrameWorker>();
}
