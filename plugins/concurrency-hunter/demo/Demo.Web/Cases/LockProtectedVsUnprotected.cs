namespace Demo.Web.Cases.LockProtectedVsUnprotected;

/// <summary>One writer takes the lock and the other does not: a lock only excludes those who take it, so the protected writer is
/// no safer than the one that ignores it.</summary>
public sealed class Logbook
{
    private readonly object _gate = new();
    private string? _entry;

    public void Append(string entry)
    {
        lock (_gate)
            _entry = entry;
    }

    public void Overwrite(string entry) => _entry = entry;
}

public sealed class AppendingWorker : BackgroundService
{
    private readonly Logbook _logbook;

    public AppendingWorker(Logbook logbook) => _logbook = logbook;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logbook.Append("appended");
        return Task.CompletedTask;
    }
}

public sealed class OverwritingWorker : BackgroundService
{
    private readonly Logbook _logbook;

    public OverwritingWorker(Logbook logbook) => _logbook = logbook;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logbook.Overwrite("overwritten");
        return Task.CompletedTask;
    }
}

public static class LockProtectedVsUnprotectedCase
{
    public static IServiceCollection AddLockProtectedVsUnprotected(this IServiceCollection services) =>
        services.AddSingleton<Logbook>()
                .AddHostedService<AppendingWorker>()
                .AddHostedService<OverwritingWorker>();
}
