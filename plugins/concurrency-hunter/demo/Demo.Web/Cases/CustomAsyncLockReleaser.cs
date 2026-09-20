namespace Demo.Web.Cases.CustomAsyncLockReleaser;

/// <summary>A hand-written lock over a <see cref="SemaphoreSlim"/> of capacity one, whose scope object releases the semaphore
/// when it is disposed. Nothing about the wrapper is recognized: the entry and the exit both reduce to the semaphore, so the
/// must-hold analysis that proves a `lock` proves this too, on the semaphore's region and never on the wrapper's (ADR 0009). The
/// scope is taken synchronously because the analysis does not yet follow an async method's result to the object it returns.</summary>
public sealed class AsyncLock
{
    private readonly SemaphoreSlim _semaphore;

    public AsyncLock(SemaphoreSlim semaphore) => _semaphore = semaphore;

    public Releaser Enter()
    {
        _semaphore.Wait();
        return new Releaser(_semaphore);
    }
}

public sealed class Releaser : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    public Releaser(SemaphoreSlim semaphore) => _semaphore = semaphore;

    public void Dispose() => _semaphore.Release();
}

public sealed class Ledger
{
    // The semaphore is the singleton's own field, where its identity is one object per process; an object the analysis reaches
    // only through another object's constructor is not yet named that precisely.
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly AsyncLock _lock;
    private string? _entry;

    public Ledger() => _lock = new AsyncLock(_semaphore);

    public void Append(string entry)
    {
        using (_lock.Enter())
            _entry = entry;
    }
}

public sealed class FirstLedgerWorker : BackgroundService
{
    private readonly Ledger _ledger;

    public FirstLedgerWorker(Ledger ledger) => _ledger = ledger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ledger.Append("first");
        return Task.CompletedTask;
    }
}

public sealed class SecondLedgerWorker : BackgroundService
{
    private readonly Ledger _ledger;

    public SecondLedgerWorker(Ledger ledger) => _ledger = ledger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ledger.Append("second");
        return Task.CompletedTask;
    }
}

public static class CustomAsyncLockReleaserCase
{
    public static IServiceCollection AddCustomAsyncLockReleaser(this IServiceCollection services) =>
        services.AddSingleton<Ledger>()
                .AddHostedService<FirstLedgerWorker>()
                .AddHostedService<SecondLedgerWorker>();
}
