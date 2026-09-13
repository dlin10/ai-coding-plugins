namespace Demo.Web.Cases.FactoryReturnsSharedStatic;

public sealed class Box
{
    public string? Value { get; set; }
}

/// <summary>Looks like a factory, returns one static object: every caller shares it.</summary>
public static class SharedBoxes
{
    private static readonly Box Instance = new();

    public static Box Get() => Instance;
}

public sealed class FirstBoxWorker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SharedBoxes.Get().Value = "first";
        return Task.CompletedTask;
    }
}

public sealed class SecondBoxWorker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SharedBoxes.Get().Value = "second";
        return Task.CompletedTask;
    }
}

public static class FactoryReturnsSharedStaticCase
{
    public static IServiceCollection AddFactoryReturnsSharedStatic(this IServiceCollection services) =>
        services.AddHostedService<FirstBoxWorker>()
                .AddHostedService<SecondBoxWorker>();
}
