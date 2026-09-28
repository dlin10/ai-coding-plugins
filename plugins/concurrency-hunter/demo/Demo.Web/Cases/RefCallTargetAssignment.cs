using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.RefCallTargetAssignment;

public sealed class Counter
{
    public int Count;
}

public static class Cells
{
    public static ref int At(ref int value) => ref value;
}

[ApiController]
[Route("cases/ref-call-target-assignment")]
public sealed class RefCallTargetController : ControllerBase
{
    private readonly Counter _counter;

    public RefCallTargetController(Counter counter) => _counter = counter;

    [HttpPost]
    public void Post()
    {
        var x = 0;
        Cells.At(ref x) = 1;
        _counter.Count++;
    }
}

public sealed class RefCallTargetWorker : BackgroundService
{
    private readonly Counter _counter;

    public RefCallTargetWorker(Counter counter) => _counter = counter;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var x = 0;
        Cells.At(ref x) = 1;
        _counter.Count++;
        return Task.CompletedTask;
    }
}

public static class RefCallTargetAssignmentCase
{
    public static IServiceCollection AddRefCallTargetAssignment(this IServiceCollection services) =>
        services.AddSingleton<Counter>()
                .AddHostedService<RefCallTargetWorker>();
}
