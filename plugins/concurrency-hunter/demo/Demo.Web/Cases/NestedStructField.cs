namespace Demo.Web.Cases.NestedStructField;

public struct Inner
{
    public int Count;
}

public struct Outer
{
    public Inner Inner;
}

/// <summary>A struct inside a struct inside the singleton's field: the path runs through both structs to the
/// counter two workers increment.</summary>
public sealed class Gauge
{
    private Outer _outer;

    public void Add() => _outer.Inner.Count++;
}

public sealed class FirstWorker : BackgroundService
{
    private readonly Gauge _gauge;
    public FirstWorker(Gauge gauge) => _gauge = gauge;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _gauge.Add();
        return Task.CompletedTask;
    }
}

public sealed class SecondWorker : BackgroundService
{
    private readonly Gauge _gauge;
    public SecondWorker(Gauge gauge) => _gauge = gauge;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _gauge.Add();
        return Task.CompletedTask;
    }
}

public static class NestedStructFieldCase
{
    public static IServiceCollection AddNestedStructField(this IServiceCollection services) =>
        services.AddSingleton<Gauge>()
                .AddHostedService<FirstWorker>()
                .AddHostedService<SecondWorker>();
}
