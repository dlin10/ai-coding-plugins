namespace Demo.Web.Cases.ArrayDisjointConstantIndices;

/// <summary>Two workers write two constant cells of one array: different cells are different resources, and constants
/// settle that without asking a solver.</summary>
public sealed class Shelf
{
    private readonly string?[] _slots = new string?[2];

    public void SetFirst() => _slots[0] = "front";

    public void SetSecond() => _slots[1] = "back";
}

public sealed class FrontShelfWorker : BackgroundService
{
    private readonly Shelf _shelf;

    public FrontShelfWorker(Shelf shelf) => _shelf = shelf;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _shelf.SetFirst();
        return Task.CompletedTask;
    }
}

public sealed class BackShelfWorker : BackgroundService
{
    private readonly Shelf _shelf;

    public BackShelfWorker(Shelf shelf) => _shelf = shelf;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _shelf.SetSecond();
        return Task.CompletedTask;
    }
}

public static class ArrayDisjointConstantIndicesCase
{
    public static IServiceCollection AddArrayDisjointConstantIndices(this IServiceCollection services) =>
        services.AddSingleton<Shelf>()
                .AddHostedService<FrontShelfWorker>()
                .AddHostedService<BackShelfWorker>();
}
