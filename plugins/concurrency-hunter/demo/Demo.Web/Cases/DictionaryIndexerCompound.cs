using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.DictionaryIndexerCompound;

/// <summary>A singleton holding a dictionary of counters, its one key put in at construction.</summary>
public sealed class Tally
{
    public Dictionary<string, int> Counts { get; } = new();

    public Tally() => Counts["hits"] = 0;
}

/// <summary>Adds one to the counter under its key. A compound assignment through a dictionary's indexer is its long form, the
/// indexer's read and its write of what was read plus one, so it makes what that long form makes: the table members of ADR 0010
/// for the read and the write of the cell, and the compound operation the write that depends on the read is (question 33,
/// closed in the third run of phase 5b).</summary>
[ApiController]
[Route("cases/dictionary-indexer-compound")]
public sealed class TallyController : ControllerBase
{
    private readonly Tally _tally;

    public TallyController(Tally tally) => _tally = tally;

    [HttpPost]
    public void Post() => _tally.Counts["hits"] += 1;
}

/// <summary>The same compound assignment, in a worker that runs once.</summary>
public sealed class TallyWorker : BackgroundService
{
    private readonly Tally _tally;

    public TallyWorker(Tally tally) => _tally = tally;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _tally.Counts["hits"] += 1;
        return Task.CompletedTask;
    }
}

public static class DictionaryIndexerCompoundCase
{
    public static IServiceCollection AddDictionaryIndexerCompound(this IServiceCollection services) =>
        services.AddSingleton<Tally>()
                .AddHostedService<TallyWorker>();
}
