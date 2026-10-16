namespace Demo.Web.Cases.MetadataBaseNullableFilledByUnseenWrite;

public interface IBump
{
    void Bump();
}

public static class Counter
{
    public static int Hits;
}

public struct Mark : IBump
{
    public void Bump() { Counter.Hits++; }
}

/// <summary>Reflection may fill the nullable field inherited from a metadata base. Both workers then box it and mutate the
/// static counter through the box.</summary>
public sealed class Store : System.Runtime.CompilerServices.StrongBox<Mark?>
{
    public void Refresh()
    {
        typeof(Store).GetField("Value")!.SetValue(this, new Mark());
        object? boxed = Value;
        if (boxed is IBump bump)
            bump.Bump();
    }
}

public sealed class FirstWorker : BackgroundService
{
    private readonly Store _store;
    public FirstWorker(Store store) => _store = store;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _store.Refresh();
        return Task.CompletedTask;
    }
}

public sealed class SecondWorker : BackgroundService
{
    private readonly Store _store;
    public SecondWorker(Store store) => _store = store;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _store.Refresh();
        return Task.CompletedTask;
    }
}

public static class MetadataBaseNullableFilledByUnseenWriteCase
{
    public static IServiceCollection AddMetadataBaseNullableFilledByUnseenWrite(this IServiceCollection services) =>
        services.AddSingleton<Store>()
                .AddHostedService<FirstWorker>()
                .AddHostedService<SecondWorker>();
}
