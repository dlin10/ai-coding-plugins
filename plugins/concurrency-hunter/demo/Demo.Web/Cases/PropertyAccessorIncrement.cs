using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.PropertyAccessorIncrement;

/// <summary>A singleton whose level is a property with accessor bodies over one field.</summary>
public sealed class Gauge
{
    private int _level;

    public int Level { get { return _level; } set { _level = value; } }
}

/// <summary>Raises the level by one. An increment of a property the lowering calls is its long form, the getter, then the setter
/// with what the getter returned plus one, so the setter's write of the field depends on the getter's read of it and the two are
/// one read-modify-write (question 33, closed in the third run of phase 5b).</summary>
[ApiController]
[Route("cases/property-accessor-increment")]
public sealed class GaugeController : ControllerBase
{
    private readonly Gauge _gauge;

    public GaugeController(Gauge gauge) => _gauge = gauge;

    [HttpPost]
    public void Post() => _gauge.Level++;
}

/// <summary>The same increment, in a worker that runs once.</summary>
public sealed class GaugeWorker : BackgroundService
{
    private readonly Gauge _gauge;

    public GaugeWorker(Gauge gauge) => _gauge = gauge;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _gauge.Level++;
        return Task.CompletedTask;
    }
}

public static class PropertyAccessorIncrementCase
{
    public static IServiceCollection AddPropertyAccessorIncrement(this IServiceCollection services) =>
        services.AddSingleton<Gauge>()
                .AddHostedService<GaugeWorker>();
}
