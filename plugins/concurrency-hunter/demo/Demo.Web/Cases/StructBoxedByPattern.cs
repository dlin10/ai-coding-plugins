using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.StructBoxedByPattern;

public interface IBump
{
    void Bump();
}

public static class Counter
{
    public static int Total;
}

public struct Probe : IBump
{
    public int Hits;

    public void Bump()
    {
        Hits++;
        Counter.Total++;
    }
}

/// <summary>A pattern's interface designation boxes a copy of the singleton's struct: the member writes the box, which only the
/// worker that made it reaches, and the static counter, which both workers write.</summary>
public sealed class Gauge
{
    private Probe _probe = new Probe();

    public void Use()
    {
        if (_probe is IBump bump)
            bump.Bump();
    }
}

public sealed class FirstWorker : BackgroundService
{
    private readonly Gauge _gauge;
    public FirstWorker(Gauge gauge) => _gauge = gauge;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _gauge.Use();
        return Task.CompletedTask;
    }
}

public sealed class SecondWorker : BackgroundService
{
    private readonly Gauge _gauge;
    public SecondWorker(Gauge gauge) => _gauge = gauge;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _gauge.Use();
        return Task.CompletedTask;
    }
}

public interface IMark
{
    void Mark();
}

public struct Tag : IMark
{
    public int Seen;

    public void Mark() => Seen++;
}

/// <summary>Two tasks of one request box the captured struct by a pattern and call the member on their own boxes: neither writes
/// the capture cell.</summary>
[ApiController]
[Route("cases/struct-boxed-by-pattern")]
public sealed class MarkController : ControllerBase
{
    [HttpPost]
    public async Task Post()
    {
        var tag = new Tag();
        var first = Task.Run(() => { if (tag is IMark boxed) boxed.Mark(); });
        var second = Task.Run(() => { if (tag is IMark boxed) boxed.Mark(); });
        await first;
        await second;
    }
}

public static class StructBoxedByPatternCase
{
    public static IServiceCollection AddStructBoxedByPattern(this IServiceCollection services) =>
        services.AddSingleton<Gauge>()
                .AddHostedService<FirstWorker>()
                .AddHostedService<SecondWorker>();
}
