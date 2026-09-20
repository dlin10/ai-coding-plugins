namespace Demo.Web.Cases.ArrayDisjointGuardedRanges;

/// <summary>Each worker walks the whole array but writes only under a guard that keeps it on its own half: the two
/// index ranges are provably disjoint, so no pair of cells can be the same.</summary>
public sealed class Register
{
    private const int Rows = 10;
    private const int Split = 5;

    private readonly string?[] _slots = new string?[Rows];

    public void FillLow()
    {
        for (var i = 0; i < Rows; i++)
            if (i < Split)
                _slots[i] = "low";
    }

    public void FillHigh()
    {
        for (var j = 0; j < Rows; j++)
            if (j >= Split)
                _slots[j] = "high";
    }
}

public sealed class LowRangeWorker : BackgroundService
{
    private readonly Register _register;

    public LowRangeWorker(Register register) => _register = register;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _register.FillLow();
        return Task.CompletedTask;
    }
}

public sealed class HighRangeWorker : BackgroundService
{
    private readonly Register _register;

    public HighRangeWorker(Register register) => _register = register;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _register.FillHigh();
        return Task.CompletedTask;
    }
}

public static class ArrayDisjointGuardedRangesCase
{
    public static IServiceCollection AddArrayDisjointGuardedRanges(this IServiceCollection services) =>
        services.AddSingleton<Register>()
                .AddHostedService<LowRangeWorker>()
                .AddHostedService<HighRangeWorker>();
}
