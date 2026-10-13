namespace Demo.Web.Cases.StructCopyNotShared;

public struct Entry
{
    public int Hits;
}

/// <summary>Hard negative: each worker copies the struct out of the singleton's array cell and field into a local
/// and increments the copy. Taking the copy reads the storage; the increment touches only the worker's own copy.</summary>
public sealed class Archive
{
    private readonly Entry[] _entries = new Entry[1];
    private Entry _last;

    public Archive()
    {
        _entries[0] = new Entry();
        _last = new Entry();
    }

    public void Touch()
    {
        var e = _entries[0];
        e.Hits++;
        var l = _last;
        l.Hits++;
    }
}

public sealed class FirstWorker : BackgroundService
{
    private readonly Archive _archive;
    public FirstWorker(Archive archive) => _archive = archive;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _archive.Touch();
        return Task.CompletedTask;
    }
}

public sealed class SecondWorker : BackgroundService
{
    private readonly Archive _archive;
    public SecondWorker(Archive archive) => _archive = archive;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _archive.Touch();
        return Task.CompletedTask;
    }
}

public static class StructCopyNotSharedCase
{
    public static IServiceCollection AddStructCopyNotShared(this IServiceCollection services) =>
        services.AddSingleton<Archive>()
                .AddHostedService<FirstWorker>()
                .AddHostedService<SecondWorker>();
}
