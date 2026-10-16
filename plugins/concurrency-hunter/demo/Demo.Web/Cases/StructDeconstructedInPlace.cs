namespace Demo.Web.Cases.StructDeconstructedInPlace;

public struct Reading
{
    public int Count;
    public int Value;

    public void Deconstruct(out int count, out int value)
    {
        count = Count++;
        value = Value;
    }
}

/// <summary>A deconstruction calls the struct's own <c>Deconstruct</c> on the singleton's field itself, as any member call does: its
/// increment writes the field, which both workers do.</summary>
public sealed class Meter
{
    private Reading _reading = new Reading();

    public int Take()
    {
        var (count, value) = _reading;
        return count + value;
    }
}

public sealed class FirstWorker : BackgroundService
{
    private readonly Meter _meter;
    public FirstWorker(Meter meter) => _meter = meter;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _meter.Take();
        return Task.CompletedTask;
    }
}

public sealed class SecondWorker : BackgroundService
{
    private readonly Meter _meter;
    public SecondWorker(Meter meter) => _meter = meter;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _meter.Take();
        return Task.CompletedTask;
    }
}

public static class StructDeconstructedInPlaceCase
{
    public static IServiceCollection AddStructDeconstructedInPlace(this IServiceCollection services) =>
        services.AddSingleton<Meter>()
                .AddHostedService<FirstWorker>()
                .AddHostedService<SecondWorker>();
}
