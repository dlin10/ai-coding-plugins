namespace Demo.Web.Cases.ReaderWriterLockSlim;

/// <summary>A read lock excludes a write lock but not another read lock: the first field is read and written under the modes
/// meant for them, while the second is written by two holders of the read lock, who may hold it at the same time.</summary>
public sealed class Catalogue
{
    private readonly System.Threading.ReaderWriterLockSlim _lock = new();
    private string? _entry;
    private string? _shared;

    public string? Read()
    {
        _lock.EnterReadLock();
        try
        {
            return _entry;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Write(string value)
    {
        _lock.EnterWriteLock();
        try
        {
            _entry = value;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void WriteUnderReadLock(string value)
    {
        _lock.EnterReadLock();
        try
        {
            _shared = value;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
}

public sealed class ReadingCatalogueWorker : BackgroundService
{
    private readonly Catalogue _catalogue;

    public ReadingCatalogueWorker(Catalogue catalogue) => _catalogue = catalogue;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _catalogue.Read();
        _catalogue.WriteUnderReadLock("reader");
        return Task.CompletedTask;
    }
}

public sealed class WritingCatalogueWorker : BackgroundService
{
    private readonly Catalogue _catalogue;

    public WritingCatalogueWorker(Catalogue catalogue) => _catalogue = catalogue;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _catalogue.Write("writer");
        _catalogue.WriteUnderReadLock("writer");
        return Task.CompletedTask;
    }
}

public static class ReaderWriterLockSlimCase
{
    public static IServiceCollection AddReaderWriterLockSlim(this IServiceCollection services) =>
        services.AddSingleton<Catalogue>()
                .AddHostedService<ReadingCatalogueWorker>()
                .AddHostedService<WritingCatalogueWorker>();
}
