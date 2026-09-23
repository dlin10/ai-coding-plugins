namespace Demo.Web.Cases.ConstantArgumentCells;

public sealed class Frame
{
    private readonly int[] _slots = new int[8];
    public void Write(int index) => _slots[index] = 1;
}

public sealed class FirstWorker : BackgroundService
{
    private readonly Frame _frame;
    public FirstWorker(Frame frame) => _frame = frame;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _frame.Write(3);
        return Task.CompletedTask;
    }
}

public sealed class SecondWorker : BackgroundService
{
    private readonly Frame _frame;
    public SecondWorker(Frame frame) => _frame = frame;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _frame.Write(4);
        return Task.CompletedTask;
    }
}

public static class ConstantArgumentCellsCase
{
    public static IServiceCollection AddConstantArgumentCells(this IServiceCollection services) =>
        services.AddSingleton<Frame>()
                .AddHostedService<FirstWorker>()
                .AddHostedService<SecondWorker>();
}
