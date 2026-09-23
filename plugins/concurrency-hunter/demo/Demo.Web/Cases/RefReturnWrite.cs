namespace Demo.Web.Cases.RefReturnWrite;

public sealed class Slots
{
    private readonly int[] _cells = new int[4];
    public ref int Cell() => ref _cells[0];
    public void Write() => _cells[0] = 2;
}

public sealed class RefWorker : BackgroundService
{
    private readonly Slots _slots;
    public RefWorker(Slots slots) => _slots = slots;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _slots.Cell() = 1;
        return Task.CompletedTask;
    }
}

public sealed class DirectWorker : BackgroundService
{
    private readonly Slots _slots;
    public DirectWorker(Slots slots) => _slots = slots;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _slots.Write();
        return Task.CompletedTask;
    }
}

public static class RefReturnWriteCase
{
    public static IServiceCollection AddRefReturnWrite(this IServiceCollection services) =>
        services.AddSingleton<Slots>()
                .AddHostedService<RefWorker>()
                .AddHostedService<DirectWorker>();
}
