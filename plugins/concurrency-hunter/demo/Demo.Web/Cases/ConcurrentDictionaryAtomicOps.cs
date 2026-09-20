using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ConcurrentDictionaryAtomicOps;

/// <summary>`GetOrAdd`, `AddOrUpdate` and `TryUpdate` are each atomic on their slot, so an action and a worker calling
/// them on the same <see cref="ConcurrentDictionary{TKey, TValue}"/> neither corrupt the map nor lose an
/// update.</summary>
public sealed class VoteBox
{
    public ConcurrentDictionary<string, int> Votes { get; } = new();
}

[ApiController]
[Route("cases/concurrent-dictionary-atomic-ops")]
public sealed class VoteController : ControllerBase
{
    private readonly VoteBox _box;

    public VoteController(VoteBox box) => _box = box;

    [HttpPost]
    public void Post(string option)
    {
        _box.Votes.GetOrAdd(option, 0);
        _box.Votes.AddOrUpdate(option, 1, (_, count) => count + 1);
    }
}

public sealed class VoteResetWorker : BackgroundService
{
    private readonly VoteBox _box;

    public VoteResetWorker(VoteBox box) => _box = box;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _box.Votes.TryUpdate("quorum", 0, 1);
        return Task.CompletedTask;
    }
}

public static class ConcurrentDictionaryAtomicOpsCase
{
    public static IServiceCollection AddConcurrentDictionaryAtomicOps(this IServiceCollection services) =>
        services.AddSingleton<VoteBox>()
                .AddHostedService<VoteResetWorker>();
}
