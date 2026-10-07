using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.CompletionSourceHandoff;

public sealed class Tally
{
    public int Hits;
}

public sealed class Handoff
{
    public TaskCompletionSource<Tally> Source { get; } = new();
}

/// <summary>Creates an object, hands it through the singleton's <see cref="TaskCompletionSource{TResult}"/> and keeps
/// writing it. The source orders nothing: the action that awaits its task writes the same object at the same time.</summary>
/// <param name="handoff">The singleton whose completion source hands the object over.</param>
public sealed class TallyWorker(Handoff handoff) : BackgroundService
{
    private readonly Handoff _handoff = handoff;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tally = new Tally();
        _handoff.Source.SetResult(tally);
        while (!stoppingToken.IsCancellationRequested)
        {
            tally.Hits++;
            await Task.Delay(1000, stoppingToken);
        }
    }
}

[ApiController]
[Route("cases/completion-source-handoff")]
public sealed class TallyController(Handoff handoff) : ControllerBase
{
    private readonly Handoff _handoff = handoff;

    [HttpPost]
    public async Task Post()
    {
        var tally = await _handoff.Source.Task;
        tally.Hits++;
    }
}

public static class CompletionSourceHandoffCase
{
    public static IServiceCollection AddCompletionSourceHandoff(this IServiceCollection services) =>
        services.AddSingleton<Handoff>()
                .AddHostedService<TallyWorker>();
}
