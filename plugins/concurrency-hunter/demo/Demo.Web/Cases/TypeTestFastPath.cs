using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.TypeTestFastPath;

public sealed class Tally
{
    public Tally() => Items = new List<int> { 1 };

    public List<int> Items { get; }
    public int Hits;
}

public sealed class Score
{
    public Score() => Items = new List<int> { 1 };

    public List<int> Items { get; }
    public int Points;
}

public abstract class Batch
{
    public abstract void Apply(Action<int> each);
}

public sealed class SingleBatch : Batch
{
    public override void Apply(Action<int> each) => each(0);
}

public static class Batches
{
    public static void EachIs(IEnumerable<int> items, Action<int> each)
    {
        if (items is Batch batch)
        {
            batch.Apply(each);
            return;
        }

        foreach (var item in items)
            each(item);
    }

    public static void EachAs(IEnumerable<int> items, Action<int> each)
    {
        var batch = items as Batch;
        if (batch != null)
        {
            batch.Apply(each);
            return;
        }

        foreach (var item in items)
            each(item);
    }
}

[ApiController]
[Route("cases/type-test-fast-path")]
public sealed class BatchController : ControllerBase
{
    private readonly Tally _tally;
    private readonly Score _score;

    public BatchController(Tally tally, Score score)
    {
        _tally = tally;
        _score = score;
    }

    [HttpPost("is")]
    public void PostIs() => Batches.EachIs(_tally.Items, x => _tally.Hits++);

    [HttpPost("as")]
    public void PostAs() => Batches.EachAs(_score.Items, x => _score.Points++);
}

public static class TypeTestFastPathCase
{
    public static IServiceCollection AddTypeTestFastPath(this IServiceCollection services) =>
        services.AddSingleton<Tally>().AddSingleton<Score>();
}
