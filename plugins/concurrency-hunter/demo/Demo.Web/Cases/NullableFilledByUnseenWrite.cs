using System.Reflection;

namespace Demo.Web.Cases.NullableFilledByUnseenWrite;

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

/// <summary>Reflection fills the singleton's Nullable with a real value, beyond the bodies the analysis sees. Boxing it then
/// makes a box, and the interface member called on the box increments the static counter in both workers.</summary>
public sealed class Store
{
    private Mark? _state = null;

    public void Refresh()
    {
        typeof(Store).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, new Mark());
        object? boxed = _state;
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

public static class NullableFilledByUnseenWriteCase
{
    public static IServiceCollection AddNullableFilledByUnseenWrite(this IServiceCollection services) =>
        services.AddSingleton<Store>()
                .AddHostedService<FirstWorker>()
                .AddHostedService<SecondWorker>();
}
