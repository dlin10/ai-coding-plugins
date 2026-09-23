namespace Demo.Web.Cases.IndexGuardAtCallSite;

public sealed class Frame
{
    private readonly int[] _disjoint = new int[16];
    private readonly int[] _twoLevels = new int[16];
    private readonly int[] _overlapping = new int[16];

    public int LowIndex = 3;
    public int HighIndex = 4;
    public int LowOverlappingIndex = 4;
    public int HighOverlappingIndex = 4;

    public void WriteDisjoint(int index) => _disjoint[index] = 1;
    public void EnterTwoLevels(int index) => WriteTwoLevels(index);
    private void WriteTwoLevels(int index) => _twoLevels[index] = 1;
    public void WriteOverlapping(int index) => _overlapping[index] = 1;
}

public sealed class LowWorker : BackgroundService
{
    private readonly Frame _frame;
    public LowWorker(Frame frame) => _frame = frame;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var index = _frame.LowIndex;
        if (index < 4) _frame.WriteDisjoint(index);
        if (index < 4) _frame.EnterTwoLevels(index);
        var overlapping = _frame.LowOverlappingIndex;
        if (overlapping < 6) _frame.WriteOverlapping(overlapping);
        return Task.CompletedTask;
    }
}

public sealed class HighWorker : BackgroundService
{
    private readonly Frame _frame;
    public HighWorker(Frame frame) => _frame = frame;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var index = _frame.HighIndex;
        if (index >= 4) _frame.WriteDisjoint(index);
        if (index >= 4) _frame.EnterTwoLevels(index);
        var overlapping = _frame.HighOverlappingIndex;
        if (overlapping >= 4) _frame.WriteOverlapping(overlapping);
        return Task.CompletedTask;
    }
}

public static class IndexGuardAtCallSiteCase
{
    public static IServiceCollection AddIndexGuardAtCallSite(this IServiceCollection services) =>
        services.AddSingleton<Frame>()
                .AddHostedService<LowWorker>()
                .AddHostedService<HighWorker>();
}
