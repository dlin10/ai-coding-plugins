namespace Demo.Web.Cases.StructReadThroughWrittenRefParameter;

public sealed class Tally { public int Count; }

public struct S { public Tally Ref; }

public sealed class Desk
{
    public S Slot;
    private readonly Tally _shared;
    public Desk(Tally shared) => _shared = shared;
    public void Use() => ReadThrough(ref Slot);
    private static void ReadThrough(ref S p)
    {
        p = new S();
        var x = p;
        x.Ref!.Count++;
    }
    public void Publish()
    {
        Slot = new S { Ref = _shared };
        _shared.Count++;
    }
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
        _desk.Publish();
        return Task.CompletedTask;
    }
}

public static class StructReadThroughWrittenRefParameterCase
{
    public static IServiceCollection AddStructReadThroughWrittenRefParameter(this IServiceCollection services) =>
        services.AddSingleton<Tally>()
                .AddSingleton<Desk>()
                .AddHostedService<FirstWorker>()
                .AddHostedService<SecondWorker>();
}
