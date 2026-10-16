namespace Demo.Web.Cases.LocalValueWrittenThroughRef;

public sealed class Tally { public int Count; }

public sealed class Desk
{
    private readonly Tally _shared;
    public Desk(Tally shared) => _shared = shared;
    public void Use()
    {
        Tally value = null!;
        ref Tally alias = ref value;
        alias = _shared;
        alias.Count++;
    }
    public void Publish() => _shared.Count++;
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

public static class LocalValueWrittenThroughRefCase
{
    public static IServiceCollection AddLocalValueWrittenThroughRef(this IServiceCollection services) =>
        services.AddSingleton<Tally>()
                .AddSingleton<Desk>()
                .AddHostedService<FirstWorker>()
                .AddHostedService<SecondWorker>();
}
