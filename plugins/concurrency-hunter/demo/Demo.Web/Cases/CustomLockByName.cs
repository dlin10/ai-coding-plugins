namespace Demo.Web.Cases.CustomLockByName;

/// <summary>A type named like a lock whose Lock and Unlock synchronize nothing: both writers "under" it still
/// race.</summary>
public sealed class SimpleLock
{
    public void Lock()
    {
    }

    public void Unlock()
    {
    }
}

public sealed class Journal
{
    private readonly SimpleLock _lock = new();
    private string? _last;

    public void Append(string entry)
    {
        _lock.Lock();
        _last = entry;
        _lock.Unlock();
    }
}

public sealed class FirstJournalWorker : BackgroundService
{
    private readonly Journal _journal;

    public FirstJournalWorker(Journal journal) => _journal = journal;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _journal.Append("first");
        return Task.CompletedTask;
    }
}

public sealed class SecondJournalWorker : BackgroundService
{
    private readonly Journal _journal;

    public SecondJournalWorker(Journal journal) => _journal = journal;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _journal.Append("second");
        return Task.CompletedTask;
    }
}

public static class CustomLockByNameCase
{
    public static IServiceCollection AddCustomLockByName(this IServiceCollection services) =>
        services.AddSingleton<Journal>()
                .AddHostedService<FirstJournalWorker>()
                .AddHostedService<SecondJournalWorker>();
}
