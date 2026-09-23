namespace Demo.Web.Cases.IndexFromFieldGuarded;

public sealed class Frame
{
    private readonly int[] _slots = new int[16];
    private int _lowIndex = 3;
    private int _highIndex = 9;

    public void WriteLow()
    {
        var index = _lowIndex;
        if (index < 4) _slots[index] = 1;
    }

    public void WriteHigh()
    {
        var index = _highIndex;
        if (index >= 4) _slots[index] = 1;
    }
}

public sealed class LowWorker : BackgroundService
{
    private readonly Frame _frame;
    public LowWorker(Frame frame) => _frame = frame;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _frame.WriteLow();
        return Task.CompletedTask;
    }
}

public sealed class HighWorker : BackgroundService
{
    private readonly Frame _frame;
    public HighWorker(Frame frame) => _frame = frame;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _frame.WriteHigh();
        return Task.CompletedTask;
    }
}

public static class IndexFromFieldGuardedCase
{
    public static IServiceCollection AddIndexFromFieldGuarded(this IServiceCollection services) =>
        services.AddSingleton<Frame>()
                .AddHostedService<LowWorker>()
                .AddHostedService<HighWorker>();
}
