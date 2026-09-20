using System.Collections.Concurrent;

namespace Demo.Web.Cases.KeyEqualityComparer;

/// <summary>Which keys name one cell is the collection's comparer's business, not the key text's. Under the default comparer
/// <c>"a"</c> and <c>"b"</c> are two cells and the two sequences never meet; under one that ignores case <c>"a"</c> and
/// <c>"A"</c> are one cell; under a comparer the analysis cannot read no two keys are proven apart, so the pair stands with
/// that recorded as an uncertainty (ADR 0010).</summary>
public sealed class Boards
{
    public ConcurrentDictionary<string, int> Ordinal { get; } = new();

    public ConcurrentDictionary<string, int> IgnoringCase { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, int> Custom { get; } = new(new FirstLetterComparer());

    public void BumpOrdinalFirst()
    {
        if (Ordinal.TryGetValue("a", out var value))
            Ordinal["a"] = value + 1;
    }

    public void BumpOrdinalSecond()
    {
        if (Ordinal.TryGetValue("b", out var value))
            Ordinal["b"] = value + 1;
    }

    public void BumpIgnoringCaseFirst()
    {
        if (IgnoringCase.TryGetValue("a", out var value))
            IgnoringCase["a"] = value + 1;
    }

    public void BumpIgnoringCaseSecond()
    {
        if (IgnoringCase.TryGetValue("A", out var value))
            IgnoringCase["A"] = value + 1;
    }

    public void BumpCustomFirst()
    {
        if (Custom.TryGetValue("a", out var value))
            Custom["a"] = value + 1;
    }

    public void BumpCustomSecond()
    {
        if (Custom.TryGetValue("b", out var value))
            Custom["b"] = value + 1;
    }
}

/// <summary>A comparer of its own: what it makes equal is not read from the source.</summary>
public sealed class FirstLetterComparer : IEqualityComparer<string>
{
    public bool Equals(string? first, string? second) => first?[..1] == second?[..1];

    public int GetHashCode(string value) => value[..1].GetHashCode(StringComparison.Ordinal);
}

public sealed class FirstBoardWorker : BackgroundService
{
    private readonly Boards _boards;

    public FirstBoardWorker(Boards boards) => _boards = boards;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _boards.BumpOrdinalFirst();
        _boards.BumpIgnoringCaseFirst();
        _boards.BumpCustomFirst();
        return Task.CompletedTask;
    }
}

public sealed class SecondBoardWorker : BackgroundService
{
    private readonly Boards _boards;

    public SecondBoardWorker(Boards boards) => _boards = boards;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _boards.BumpOrdinalSecond();
        _boards.BumpIgnoringCaseSecond();
        _boards.BumpCustomSecond();
        return Task.CompletedTask;
    }
}

public static class KeyEqualityComparerCase
{
    public static IServiceCollection AddKeyEqualityComparer(this IServiceCollection services) =>
        services.AddSingleton<Boards>()
                .AddHostedService<FirstBoardWorker>()
                .AddHostedService<SecondBoardWorker>();
}
