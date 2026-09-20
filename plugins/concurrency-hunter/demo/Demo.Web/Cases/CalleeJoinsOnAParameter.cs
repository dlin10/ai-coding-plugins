namespace Demo.Web.Cases.CalleeJoinsOnAParameter;

/// <summary>A callee that waits on the handle it was handed waits for its caller too, so the write after the call is ordered
/// after the spawned work — whether the handle reached the callee through a local or was passed inline.</summary>
public sealed class HandoffWorker : BackgroundService
{
    private string? _fromLocal;
    private string? _fromInline;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var handle = Task.Run(() => { _fromLocal = "spawned"; });
        Await(handle);
        _fromLocal = "parent";

        Await(Task.Run(() => { _fromInline = "spawned"; }));
        _fromInline = "parent";
        return Task.CompletedTask;
    }

    private static void Await(Task work) => work.Wait();

    /// <summary>Reads the two fields so that the compiler keeps them; nothing calls it, so it is no root and no access.</summary>
    public string? Snapshot => _fromLocal ?? _fromInline;
}

public static class CalleeJoinsOnAParameterCase
{
    public static IServiceCollection AddCalleeJoinsOnAParameter(this IServiceCollection services) =>
        services.AddHostedService<HandoffWorker>();
}
