namespace Demo.Web.Cases.StructDeconstructedThroughInterface;

public interface IReading
{
    void Deconstruct(out int count, out int value);
}

public struct Reading : IReading
{
    public int Count;
    public int Value;

    public void Deconstruct(out int count, out int value)
    {
        count = Count++;
        value = Value;
    }
}

/// <summary>A deconstruction through an interface calls the interface's <c>Deconstruct</c>, which dispatches to the struct's own on
/// the box the singleton holds: its increment writes the box, which both workers do.</summary>
public sealed class Panel
{
    private readonly IReading _reading = new Reading();

    public int Take()
    {
        var (count, value) = _reading;
        return count + value;
    }
}

public sealed class FirstWorker : BackgroundService
{
    private readonly Panel _panel;
    public FirstWorker(Panel panel) => _panel = panel;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _panel.Take();
        return Task.CompletedTask;
    }
}

public sealed class SecondWorker : BackgroundService
{
    private readonly Panel _panel;
    public SecondWorker(Panel panel) => _panel = panel;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _panel.Take();
        return Task.CompletedTask;
    }
}

public static class StructDeconstructedThroughInterfaceCase
{
    public static IServiceCollection AddStructDeconstructedThroughInterface(this IServiceCollection services) =>
        services.AddSingleton<Panel>()
                .AddHostedService<FirstWorker>()
                .AddHostedService<SecondWorker>();
}
