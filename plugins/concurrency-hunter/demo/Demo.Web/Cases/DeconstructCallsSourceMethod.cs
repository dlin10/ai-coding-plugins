using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.DeconstructCallsSourceMethod;

public sealed class Reading
{
    public int Value;
    public int Stamp;

    public void Deconstruct(out int value, out int stamp)
    {
        value = Value;
        stamp = Stamp;
    }
}

public sealed class Sensor
{
    public Sensor() => Last = new Reading();

    public Reading Last { get; }
}

[ApiController]
[Route("cases/deconstruct-calls-source-method")]
public sealed class SensorController : ControllerBase
{
    private readonly Sensor _sensor;

    public SensorController(Sensor sensor) => _sensor = sensor;

    [HttpGet]
    public int Get()
    {
        var (value, _) = _sensor.Last;
        return value;
    }
}

public sealed class SensorWorker : BackgroundService
{
    private readonly Sensor _sensor;

    public SensorWorker(Sensor sensor) => _sensor = sensor;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _sensor.Last.Value = 1;
        return Task.CompletedTask;
    }
}

public static class DeconstructCallsSourceMethodCase
{
    public static IServiceCollection AddDeconstructCallsSourceMethod(this IServiceCollection services) =>
        services.AddSingleton<Sensor>().AddHostedService<SensorWorker>();
}
