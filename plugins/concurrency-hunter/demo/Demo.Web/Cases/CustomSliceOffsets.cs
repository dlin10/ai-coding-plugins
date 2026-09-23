namespace Demo.Web.Cases.CustomSliceOffsets;

public sealed class Frame
{
    private readonly int[] _proven = new int[16];
    private readonly int[] _mutable = new int[16];
    public readonly ProvenWindow Proven;
    public readonly MutableWindow Mutable;

    public Frame()
    {
        Proven = new ProvenWindow(_proven, 0);
        Mutable = new MutableWindow(_mutable, 0);
    }
}

public sealed class ProvenWindow
{
    public readonly int[] Data;
    public readonly int Offset;
    public ProvenWindow(int[] data, int offset) { Data = data; Offset = offset; }
    public ProvenWindow Slice(int start, int length) => new(Data, Offset + start);
    public ref int this[int index] => ref Data[Offset + index];
}

public sealed class MutableWindow
{
    public readonly int[] Data;
    public int Offset;
    public MutableWindow(int[] data, int offset) { Data = data; Offset = offset; }
    public MutableWindow Slice(int start, int length) => new(Data, Offset + start);
    public ref int this[int index] => ref Data[Offset + index];
}

public sealed class FirstWorker : BackgroundService
{
    private readonly Frame _frame;
    public FirstWorker(Frame frame) => _frame = frame;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _frame.Proven.Slice(0, 8)[0] = 1;
        _frame.Mutable.Slice(0, 8)[0] = 1;
        return Task.CompletedTask;
    }
}

public sealed class SecondWorker : BackgroundService
{
    private readonly Frame _frame;
    public SecondWorker(Frame frame) => _frame = frame;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _frame.Proven.Slice(8, 8)[0] = 2;
        _frame.Mutable.Slice(8, 8)[0] = 2;
        return Task.CompletedTask;
    }
}

public static class CustomSliceOffsetsCase
{
    public static IServiceCollection AddCustomSliceOffsets(this IServiceCollection services) =>
        services.AddSingleton<Frame>()
                .AddHostedService<FirstWorker>()
                .AddHostedService<SecondWorker>();
}
