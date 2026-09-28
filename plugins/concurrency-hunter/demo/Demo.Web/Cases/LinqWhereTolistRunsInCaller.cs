using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.LinqWhereTolistRunsInCaller;

/// <summary>A singleton holding a list, filled at construction, and two counters.</summary>
public sealed class Screen
{
    public readonly List<int> Items = new();
    public int Hits;
    public int Scans;

    public Screen() => Items.Add(1);
}

/// <summary><c>ToList</c> enumerates the <c>Where</c> where it is called, so the predicate runs in this action's execution and two
/// requests race on its counter.</summary>
[ApiController]
[Route("cases/linq-where-tolist-runs-in-caller")]
public sealed class ScreenController : ControllerBase
{
    private readonly Screen _screen;

    public ScreenController(Screen screen) => _screen = screen;

    [HttpPost]
    public void Post() => _screen.Items.Where(x => { _screen.Hits++; return true; }).ToList();
}

/// <summary>Runs its own predicate once, in its own execution: nothing else touches its counter.</summary>
public sealed class ScreenWorker : BackgroundService
{
    private readonly Screen _screen;

    public ScreenWorker(Screen screen) => _screen = screen;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _screen.Items.Where(x => { _screen.Scans++; return true; }).ToList();
        return Task.CompletedTask;
    }
}

public static class LinqWhereTolistRunsInCallerCase
{
    public static IServiceCollection AddLinqWhereTolistRunsInCaller(this IServiceCollection services) =>
        services.AddSingleton<Screen>()
                .AddHostedService<ScreenWorker>();
}
