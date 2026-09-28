using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.LinqPredicateRunsNow;

/// <summary>A singleton holding a list, filled at construction, and a counter its predicate increments.</summary>
public sealed class Tally
{
    public readonly List<int> Items = new();
    public int Hits;

    public Tally() => Items.Add(1);
}

/// <summary><c>Any</c> runs its predicate during the call, in this action's execution (<c>invoke-now</c>), so two requests race on
/// the counter.</summary>
[ApiController]
[Route("cases/linq-predicate-runs-now")]
public sealed class TallyController : ControllerBase
{
    private readonly Tally _tally;

    public TallyController(Tally tally) => _tally = tally;

    [HttpPost]
    public bool Post() => _tally.Items.Any(x => { _tally.Hits++; return false; });
}

public static class LinqPredicateRunsNowCase
{
    public static IServiceCollection AddLinqPredicateRunsNow(this IServiceCollection services) =>
        services.AddSingleton<Tally>();
}
