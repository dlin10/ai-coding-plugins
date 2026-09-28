using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ComparerCreateRunsAtCompare;

/// <summary>A singleton keeping a comparer whose comparison counts its runs. The comparer holds the comparison: it runs wherever a
/// member of the comparer is called, not where the comparer is created (a holder, ADR 0012).</summary>
public sealed class Ranking
{
    public int Hits;
    public Comparer<int> Order;

    public Ranking() => Order = Comparer<int>.Create((a, b) => { Hits++; return a.CompareTo(b); });
}

/// <summary>Compares through the singleton's comparer, which runs its comparison in this action's execution.</summary>
[ApiController]
[Route("cases/comparer-create-runs-at-compare")]
public sealed class RankingController : ControllerBase
{
    private readonly Ranking _ranking;

    public RankingController(Ranking ranking) => _ranking = ranking;

    [HttpPost]
    public int Post() => _ranking.Order.Compare(1, 2);
}

public static class ComparerCreateRunsAtCompareCase
{
    public static IServiceCollection AddComparerCreateRunsAtCompare(this IServiceCollection services) =>
        services.AddSingleton<Ranking>();
}
