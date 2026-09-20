namespace Demo.Web.Cases.UnsupportedGuardKept;

/// <summary>The same shape as mutually exclusive paths, but the guard is a string predicate the solver does not model.
/// An unsupported predicate is abstracted away, not believed, so the candidate survives as a finding whose uncertainty
/// names the predicate.</summary>
public sealed class NodeOptions
{
    public string Name { get; } = "xenon";
}

public sealed class NodeState
{
    public string? Owner { get; set; }
}

public sealed class NamedNodeWorker : BackgroundService
{
    private readonly NodeOptions _options;
    private readonly NodeState _state;

    public NamedNodeWorker(NodeOptions options, NodeState state)
    {
        _options = options;
        _state = state;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Name.StartsWith("x", StringComparison.Ordinal))
            _state.Owner = "named";

        return Task.CompletedTask;
    }
}

public sealed class OtherNodeWorker : BackgroundService
{
    private readonly NodeOptions _options;
    private readonly NodeState _state;

    public OtherNodeWorker(NodeOptions options, NodeState state)
    {
        _options = options;
        _state = state;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Name.StartsWith("x", StringComparison.Ordinal))
            _state.Owner = "other";

        return Task.CompletedTask;
    }
}

public static class UnsupportedGuardKeptCase
{
    public static IServiceCollection AddUnsupportedGuardKept(this IServiceCollection services) =>
        services.AddSingleton<NodeOptions>()
                .AddSingleton<NodeState>()
                .AddHostedService<NamedNodeWorker>()
                .AddHostedService<OtherNodeWorker>();
}
