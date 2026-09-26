using Microsoft.Extensions.Caching.Memory;

namespace Demo.Web.Cases.OpaqueTaskSource;

public sealed class Work
{
    public int Count;

    public void Run() => Count = 1;
}

/// <summary>A worker that waits for a task a helper picks: the known spawn of the work, or a task the cache's factory hands back
/// for the work. Waiting for the spawn would order the two writes; the task may be the cache's, so the wait proves nothing, and the
/// cache call is a semantic gap that decides the pair's overlap (TD-034, TD-039, TD-108).</summary>
public sealed class Scheduler : BackgroundService
{
    private readonly Work _work;
    private readonly IMemoryCache _cache;

    public Scheduler(Work work, IMemoryCache cache)
    {
        _work = work;
        _cache = cache;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Pick(stoppingToken.IsCancellationRequested);
        _work.Count = 2;
    }

    private Task Pick(bool known) => known ? Task.Run(_work.Run) : _cache.GetOrCreateAsync(_work, _ => Task.FromResult(0));
}

public static class OpaqueTaskSourceCase
{
    public static IServiceCollection AddOpaqueTaskSource(this IServiceCollection services) =>
        services.AddMemoryCache()
                .AddSingleton<Work>()
                .AddHostedService<Scheduler>();
}
