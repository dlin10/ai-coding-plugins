using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ForeachOverListElementWrite;

/// <summary>An object a list holds.</summary>
public sealed class Tally
{
    public int Count;
}

/// <summary>A singleton holding a list, filled at construction.</summary>
public sealed class Ledger
{
    public readonly List<Tally> Tallies = new();

    public Ledger() => Tallies.Add(new Tally());
}

/// <summary>Increments a field of each element in a <c>foreach</c> over the singleton's list. The loop's variable is the object the
/// list holds, as it is over an array, so the increment is a read-modify-write of that object's field (question 30, closed in the
/// second run of phase 5b).</summary>
[ApiController]
[Route("cases/foreach-over-list-element-write")]
public sealed class LedgerController : ControllerBase
{
    private readonly Ledger _ledger;

    public LedgerController(Ledger ledger) => _ledger = ledger;

    [HttpPost]
    public void Post()
    {
        foreach (var tally in _ledger.Tallies)
            tally.Count++;
    }
}

/// <summary>The same increments, in a worker that runs once.</summary>
public sealed class LedgerWorker : BackgroundService
{
    private readonly Ledger _ledger;

    public LedgerWorker(Ledger ledger) => _ledger = ledger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var tally in _ledger.Tallies)
            tally.Count++;
        return Task.CompletedTask;
    }
}

public static class ForeachOverListElementWriteCase
{
    public static IServiceCollection AddForeachOverListElementWrite(this IServiceCollection services) =>
        services.AddSingleton<Ledger>()
                .AddHostedService<LedgerWorker>();
}
