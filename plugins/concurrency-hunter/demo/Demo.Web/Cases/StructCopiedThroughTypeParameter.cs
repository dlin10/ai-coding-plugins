namespace Demo.Web.Cases.StructCopiedThroughTypeParameter;

public sealed class Tally
{
    public int Count;
}

public interface ITouch
{
    void Touch();
}

public struct Handle : ITouch
{
    public Tally Tally;

    public void Touch() => Tally.Count++;
}

/// <summary>A generic helper takes the singleton's struct as <c>in T</c>, a type parameter constrained to <c>struct</c>: the interface
/// member it calls is not <c>readonly</c>, so it runs on a defensive copy, whose reference field still holds the shared tally, and
/// the increment races in both workers.</summary>
public sealed class Desk
{
    private Handle _handle;

    public Desk(Tally tally) => _handle = new Handle { Tally = tally };

    public void Use() => Touch(in _handle);

    private static void Touch<T>(in T handle) where T : struct, ITouch => handle.Touch();
}

public sealed class FirstWorker : BackgroundService
{
    private readonly Desk _desk;
    public FirstWorker(Desk desk) => _desk = desk;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _desk.Use();
        return Task.CompletedTask;
    }
}

public sealed class SecondWorker : BackgroundService
{
    private readonly Desk _desk;
    public SecondWorker(Desk desk) => _desk = desk;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _desk.Use();
        return Task.CompletedTask;
    }
}

public static class StructCopiedThroughTypeParameterCase
{
    public static IServiceCollection AddStructCopiedThroughTypeParameter(this IServiceCollection services) =>
        services.AddSingleton<Tally>()
                .AddSingleton<Desk>()
                .AddHostedService<FirstWorker>()
                .AddHostedService<SecondWorker>();
}
