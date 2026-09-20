using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ConcurrentDictionaryEnumerateWhileMutate;

/// <summary>Enumerating while another execution inserts, on both kinds of dictionary. On the thread-safe one every member is
/// atomic on the structure, which is exactly what its enumerator promises, so there is nothing to report; on the plain one
/// neither side is atomic and the structure the enumeration walks is the structure the insertion rewrites (ADR 0010).</summary>
public sealed class Feeds
{
    public ConcurrentDictionary<string, int> Safe { get; } = new();

    public Dictionary<string, int> Plain { get; } = new();
}

[ApiController]
[Route("cases/concurrent-dictionary-enumerate-while-mutate")]
public sealed class FeedController : ControllerBase
{
    private readonly Feeds _feeds;

    public FeedController(Feeds feeds) => _feeds = feeds;

    [HttpGet("safe")]
    public int GetSafe()
    {
        var total = 0;
        foreach (var entry in _feeds.Safe)
            total += entry.Value;
        return total;
    }

    [HttpGet("plain")]
    public int GetPlain()
    {
        var total = 0;
        foreach (var entry in _feeds.Plain)
            total += entry.Value;
        return total;
    }
}

public sealed class FeedWriterWorker : BackgroundService
{
    private readonly Feeds _feeds;

    public FeedWriterWorker(Feeds feeds) => _feeds = feeds;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _feeds.Safe["latest"] = 1;
        _feeds.Plain["latest"] = 1;
        return Task.CompletedTask;
    }
}

public static class ConcurrentDictionaryEnumerateWhileMutateCase
{
    public static IServiceCollection AddConcurrentDictionaryEnumerateWhileMutate(this IServiceCollection services) =>
        services.AddSingleton<Feeds>()
                .AddHostedService<FeedWriterWorker>();
}
