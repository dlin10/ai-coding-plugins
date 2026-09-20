namespace Demo.Web.Cases.SemaphoreSlimNotAMutex;

/// <summary>Three ways a <see cref="SemaphoreSlim"/> falls short of a mutex: a capacity nothing proves, a capacity of two that
/// lets two in, and a release the guarded code can skip by throwing.</summary>
public sealed class Gatehouse
{
    private readonly SemaphoreSlim _unknown;
    private readonly SemaphoreSlim _two = new(2, 2);
    private readonly SemaphoreSlim _one = new(1, 1);
    private string? _unknownCapacity;
    private string? _capacityTwo;
    private string? _releaseNotInFinally;

    public Gatehouse(int capacity) => _unknown = new SemaphoreSlim(capacity, capacity);

    public void SetUnknownCapacity(string value)
    {
        _unknown.Wait();
        try
        {
            _unknownCapacity = value;
        }
        finally
        {
            _unknown.Release();
        }
    }

    public void SetCapacityTwo(string value)
    {
        _two.Wait();
        try
        {
            _capacityTwo = value;
        }
        finally
        {
            _two.Release();
        }
    }

    public void SetWithoutFinally(string value)
    {
        _one.Wait();
        _releaseNotInFinally = value;
        _one.Release();
    }
}

public sealed class FirstGateWorker : BackgroundService
{
    private readonly Gatehouse _gatehouse;

    public FirstGateWorker(Gatehouse gatehouse) => _gatehouse = gatehouse;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _gatehouse.SetUnknownCapacity("first");
        _gatehouse.SetCapacityTwo("first");
        _gatehouse.SetWithoutFinally("first");
        return Task.CompletedTask;
    }
}

public sealed class SecondGateWorker : BackgroundService
{
    private readonly Gatehouse _gatehouse;

    public SecondGateWorker(Gatehouse gatehouse) => _gatehouse = gatehouse;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _gatehouse.SetUnknownCapacity("second");
        _gatehouse.SetCapacityTwo("second");
        _gatehouse.SetWithoutFinally("second");
        return Task.CompletedTask;
    }
}

public static class SemaphoreSlimNotAMutexCase
{
    public static IServiceCollection AddSemaphoreSlimNotAMutex(this IServiceCollection services) =>
        services.AddSingleton(_ => new Gatehouse(4))
                .AddHostedService<FirstGateWorker>()
                .AddHostedService<SecondGateWorker>();
}
