namespace Demo.Web.Cases.SystemThreadingLock;

/// <summary><see cref="Lock"/> under all three of its forms: `Enter` with `Exit`, the scope `EnterScope` hands back, and the
/// `lock` statement over the lock itself. All three are one section on one object, so the writes under them exclude each other.
/// The second field is the misuse the type invites: a cast to `object` turns the same statement into a monitor, which is the
/// other mechanism on that object and excludes nobody holding the lock.</summary>
public sealed class Roster
{
    private readonly Lock _gate = new();
    private string? _onDuty;
    private string? _standby;

    public void SetWithEnterAndExit(string name)
    {
        _gate.Enter();
        try
        {
            _onDuty = name;
        }
        finally
        {
            _gate.Exit();
        }
    }

    public void SetWithEnterScope(string name)
    {
        using (_gate.EnterScope())
            _onDuty = name;
    }

    public void SetWithLockStatement(string name)
    {
        lock (_gate)
            _onDuty = name;
    }

    public void SetUnderTheLockItself(string name)
    {
        lock (_gate)
            _standby = name;
    }

    public void SetUnderAMonitorOnTheLock(string name)
    {
        // The warning is the case: converting the lock to `object` locks its monitor instead of the lock.
#pragma warning disable CS9216
        lock ((object)_gate)
#pragma warning restore CS9216
        {
            _standby = name;
        }
    }
}

public sealed class EnterExitWorker : BackgroundService
{
    private readonly Roster _roster;

    public EnterExitWorker(Roster roster) => _roster = roster;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _roster.SetWithEnterAndExit("enter-exit");
        _roster.SetUnderTheLockItself("lock-statement");
        return Task.CompletedTask;
    }
}

public sealed class EnterScopeWorker : BackgroundService
{
    private readonly Roster _roster;

    public EnterScopeWorker(Roster roster) => _roster = roster;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _roster.SetWithEnterScope("scope");
        _roster.SetWithLockStatement("lock-statement");
        _roster.SetUnderAMonitorOnTheLock("monitor");
        return Task.CompletedTask;
    }
}

public static class SystemThreadingLockCase
{
    public static IServiceCollection AddSystemThreadingLock(this IServiceCollection services) =>
        services.AddSingleton<Roster>()
                .AddHostedService<EnterExitWorker>()
                .AddHostedService<EnterScopeWorker>();
}
