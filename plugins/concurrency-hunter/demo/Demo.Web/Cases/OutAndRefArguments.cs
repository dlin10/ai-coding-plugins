namespace Demo.Web.Cases.OutAndRefArguments;

public sealed class Cells
{
    public int OutCell;
    public int RefCell;
    public void Assign(out int value) => value = 1;
    public void Increment(ref int value) => value++;
}

public sealed class RefWorker : BackgroundService
{
    private readonly Cells _cells;
    public RefWorker(Cells cells) => _cells = cells;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _cells.Assign(out _cells.OutCell);
        _cells.Increment(ref _cells.RefCell);
        return Task.CompletedTask;
    }
}

public sealed class DirectWorker : BackgroundService
{
    private readonly Cells _cells;
    public DirectWorker(Cells cells) => _cells = cells;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _cells.OutCell = 2;
        _cells.RefCell = 2;
        return Task.CompletedTask;
    }
}

public static class OutAndRefArgumentsCase
{
    public static IServiceCollection AddOutAndRefArguments(this IServiceCollection services) =>
        services.AddSingleton<Cells>()
                .AddHostedService<RefWorker>()
                .AddHostedService<DirectWorker>();
}
