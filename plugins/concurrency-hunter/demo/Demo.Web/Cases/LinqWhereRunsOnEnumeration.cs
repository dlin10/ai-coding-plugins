using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.LinqWhereRunsOnEnumeration;

/// <summary>A singleton keeping a <c>Where</c> over its list. Nothing runs where the sequence is made: its predicate runs wherever the
/// sequence is enumerated (<c>iterator</c>, ADR 0011).</summary>
public sealed class Filter
{
    public readonly List<int> Items;
    public readonly IEnumerable<int> Matching;
    public int Hits;

    public Filter()
    {
        Items = new List<int> { 1 };
        Matching = Items.Where(x => { Hits++; return true; });
    }
}

/// <summary>Enumerates the singleton's sequence in each request.</summary>
[ApiController]
[Route("cases/linq-where-runs-on-enumeration")]
public sealed class FilterController : ControllerBase
{
    private readonly Filter _filter;

    public FilterController(Filter filter) => _filter = filter;

    [HttpPost]
    public void Post()
    {
        foreach (var _ in _filter.Matching) { }
    }
}

/// <summary>Enumerates the same sequence once.</summary>
public sealed class FilterWorker : BackgroundService
{
    private readonly Filter _filter;

    public FilterWorker(Filter filter) => _filter = filter;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var _ in _filter.Matching) { }
        return Task.CompletedTask;
    }
}

public static class LinqWhereRunsOnEnumerationCase
{
    public static IServiceCollection AddLinqWhereRunsOnEnumeration(this IServiceCollection services) =>
        services.AddSingleton<Filter>()
                .AddHostedService<FilterWorker>();
}
