namespace Demo.Web.Cases.PairValueReadFromField;

public struct Entry
{
    public int Hits;
}

/// <summary>A pair hands out its value as a copy taken from the whole pair: reading <c>.Value</c> of the singleton's pair field
/// reads the field, which the other worker writes whole.</summary>
public sealed class Cache
{
    private KeyValuePair<int, Entry> _pair;

    public void Replace(int key) => _pair = new KeyValuePair<int, Entry>(key, new Entry { Hits = key });

    public int Peek() => _pair.Value.Hits;
}

public sealed class WritingWorker : BackgroundService
{
    private readonly Cache _cache;
    public WritingWorker(Cache cache) => _cache = cache;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _cache.Replace(1);
        return Task.CompletedTask;
    }
}

public sealed class ReadingWorker : BackgroundService
{
    private readonly Cache _cache;
    public ReadingWorker(Cache cache) => _cache = cache;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _cache.Peek();
        return Task.CompletedTask;
    }
}

public static class PairValueReadFromFieldCase
{
    public static IServiceCollection AddPairValueReadFromField(this IServiceCollection services) =>
        services.AddSingleton<Cache>()
                .AddHostedService<WritingWorker>()
                .AddHostedService<ReadingWorker>();
}
