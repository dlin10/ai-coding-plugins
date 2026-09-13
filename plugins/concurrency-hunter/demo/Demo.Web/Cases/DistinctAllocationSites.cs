namespace Demo.Web.Cases.DistinctAllocationSites;

public sealed class Box
{
    public string? Value { get; set; }
}

/// <summary>Hard negative: same type, same field name, two allocation sites; each worker writes only its own.</summary>
public sealed class Pair
{
    public Pair()
    {
        Left = new Box();
        Right = new Box();
    }

    public Box Left { get; }
    public Box Right { get; }
}

public sealed class LeftWorker : BackgroundService
{
    private readonly Pair _pair;

    public LeftWorker(Pair pair) => _pair = pair;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _pair.Left.Value = "left";
        return Task.CompletedTask;
    }
}

public sealed class RightWorker : BackgroundService
{
    private readonly Pair _pair;

    public RightWorker(Pair pair) => _pair = pair;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _pair.Right.Value = "right";
        return Task.CompletedTask;
    }
}

public static class DistinctAllocationSitesCase
{
    public static IServiceCollection AddDistinctAllocationSites(this IServiceCollection services) =>
        services.AddSingleton<Pair>()
                .AddHostedService<LeftWorker>()
                .AddHostedService<RightWorker>();
}
