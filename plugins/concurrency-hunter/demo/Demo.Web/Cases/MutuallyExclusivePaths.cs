namespace Demo.Web.Cases.MutuallyExclusivePaths;

public enum Shard
{
    Left,
    Right
}

/// <summary>One shared options object that never changes after construction. Both workers read it, and each writes
/// only on the branch the other cannot take: the boolean guard and its negation, and two cases of one switch.</summary>
public sealed class RoleOptions
{
    public bool IsPrimary { get; } = true;

    public Shard Shard { get; } = Shard.Left;
}

public sealed class PathState
{
    public string? Boolean { get; set; }

    public string? EnumSwitch { get; set; }
}

public sealed class PrimaryPathWorker : BackgroundService
{
    private readonly RoleOptions _options;
    private readonly PathState _state;

    public PrimaryPathWorker(RoleOptions options, PathState state)
    {
        _options = options;
        _state = state;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.IsPrimary)
            _state.Boolean = "primary";

        switch (_options.Shard)
        {
            case Shard.Left:
                _state.EnumSwitch = "left";
                break;
        }

        return Task.CompletedTask;
    }
}

public sealed class SecondaryPathWorker : BackgroundService
{
    private readonly RoleOptions _options;
    private readonly PathState _state;

    public SecondaryPathWorker(RoleOptions options, PathState state)
    {
        _options = options;
        _state = state;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsPrimary)
            _state.Boolean = "secondary";

        switch (_options.Shard)
        {
            case Shard.Right:
                _state.EnumSwitch = "right";
                break;
        }

        return Task.CompletedTask;
    }
}

public static class MutuallyExclusivePathsCase
{
    public static IServiceCollection AddMutuallyExclusivePaths(this IServiceCollection services) =>
        services.AddSingleton<RoleOptions>()
                .AddSingleton<PathState>()
                .AddHostedService<PrimaryPathWorker>()
                .AddHostedService<SecondaryPathWorker>();
}
