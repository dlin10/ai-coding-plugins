using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.DictionaryPairKeyAndValue;

/// <summary>An object a dictionary holds as a key.</summary>
public sealed class Badge
{
    public int Mark;
}

/// <summary>An object a dictionary holds as a value.</summary>
public sealed class Score
{
    public int Points;
}

/// <summary>A singleton holding a dictionary keyed by one source type with values of another, filled at construction.</summary>
public sealed class Roster
{
    public readonly Dictionary<Badge, Score> Entries = new();

    public Roster() => Entries.Add(new Badge(), new Score());
}

/// <summary>Deconstructs each pair of the singleton's dictionary, writes a field of the key and increments a field of the value. The
/// key is the object the dictionary holds as a key and the value the one it holds as a value, each apart, so the write lands on the
/// key alone and the increment on the value alone (question 30, closed in the second run of phase 5b).</summary>
[ApiController]
[Route("cases/dictionary-pair-key-and-value")]
public sealed class RosterController : ControllerBase
{
    private readonly Roster _roster;

    public RosterController(Roster roster) => _roster = roster;

    [HttpPost]
    public void Post()
    {
        foreach (var (badge, score) in _roster.Entries)
        {
            badge.Mark = 1;
            score.Points++;
        }
    }
}

/// <summary>The same writes and increments, in a worker that runs once.</summary>
public sealed class RosterWorker : BackgroundService
{
    private readonly Roster _roster;

    public RosterWorker(Roster roster) => _roster = roster;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var (badge, score) in _roster.Entries)
        {
            badge.Mark = 2;
            score.Points++;
        }

        return Task.CompletedTask;
    }
}

public static class DictionaryPairKeyAndValueCase
{
    public static IServiceCollection AddDictionaryPairKeyAndValue(this IServiceCollection services) =>
        services.AddSingleton<Roster>()
                .AddHostedService<RosterWorker>();
}
