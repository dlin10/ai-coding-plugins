using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.RefLocalSplitReadModifyWrite;

/// <summary>A singleton counter both sides increment through a reference.</summary>
public sealed class Counter
{
    public int Value;
}

/// <summary>Reads the counter through a <c>ref</c> local and writes it plus one through the same local in the next statement. The
/// write depends on the read of its own field, so the two are one read-modify-write, as the same split over the field itself is
/// (question 25, closed in the second run of phase 5b).</summary>
[ApiController]
[Route("cases/ref-local-split-read-modify-write")]
public sealed class CounterController : ControllerBase
{
    private readonly Counter _counter;

    public CounterController(Counter counter) => _counter = counter;

    [HttpPost]
    public void Post()
    {
        ref int value = ref _counter.Value;
        var old = value;
        value = old + 1;
    }
}

/// <summary>The same split read-modify-write, in a worker that runs once.</summary>
public sealed class CounterWorker : BackgroundService
{
    private readonly Counter _counter;

    public CounterWorker(Counter counter) => _counter = counter;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ref int value = ref _counter.Value;
        var old = value;
        value = old + 1;
        return Task.CompletedTask;
    }
}

public static class RefLocalSplitReadModifyWriteCase
{
    public static IServiceCollection AddRefLocalSplitReadModifyWrite(this IServiceCollection services) =>
        services.AddSingleton<Counter>()
                .AddHostedService<CounterWorker>();
}
