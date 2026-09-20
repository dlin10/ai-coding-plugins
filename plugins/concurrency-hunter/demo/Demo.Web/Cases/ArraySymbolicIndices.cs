namespace Demo.Web.Cases.ArraySymbolicIndices;

/// <summary>Two workers write a cell each, at indices that come from sources of their own. Nothing proves the two indices differ,
/// so nothing proves the two cells differ, and the pair stands.</summary>
public sealed class Rack
{
    private const int Slots = 8;

    private readonly string?[] _slots = new string?[Slots];

    public void SetFromFirst(int index) => _slots[index] = "first";

    public void SetFromSecond(int index) => _slots[index] = "second";
}

public sealed class FirstRackWorker : BackgroundService
{
    private readonly Rack _rack;

    public FirstRackWorker(Rack rack) => _rack = rack;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _rack.SetFromFirst(Environment.ProcessorCount);
        return Task.CompletedTask;
    }
}

public sealed class SecondRackWorker : BackgroundService
{
    private readonly Rack _rack;

    public SecondRackWorker(Rack rack) => _rack = rack;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _rack.SetFromSecond(Environment.TickCount);
        return Task.CompletedTask;
    }
}

public static class ArraySymbolicIndicesCase
{
    public static IServiceCollection AddArraySymbolicIndices(this IServiceCollection services) =>
        services.AddSingleton<Rack>()
                .AddHostedService<FirstRackWorker>()
                .AddHostedService<SecondRackWorker>();
}
