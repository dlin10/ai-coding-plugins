namespace Demo.Web.Cases.StructMutatingMethod;

public struct Reading
{
    private int Count;

    public void Increment() { Count++; }
}

/// <summary>A mutating method called on the singleton's struct field runs on that field's storage, not on a copy:
/// its <c>this</c> is the field, so two workers calling it lose updates.</summary>
public sealed class Meter
{
    private Reading _reading;

    public void Record() => _reading.Increment();
}

public sealed class FirstWorker : BackgroundService
{
    private readonly Meter _meter;
    public FirstWorker(Meter meter) => _meter = meter;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _meter.Record();
        return Task.CompletedTask;
    }
}

public sealed class SecondWorker : BackgroundService
{
    private readonly Meter _meter;
    public SecondWorker(Meter meter) => _meter = meter;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _meter.Record();
        return Task.CompletedTask;
    }
}

public static class StructMutatingMethodCase
{
    public static IServiceCollection AddStructMutatingMethod(this IServiceCollection services) =>
        services.AddSingleton<Meter>()
                .AddHostedService<FirstWorker>()
                .AddHostedService<SecondWorker>();
}
