namespace Demo.Web.Cases.LockDifferentIdentity;

/// <summary>Both writers are under a lock, and neither excludes the other: two locks are two identities, so the writes still
/// race.</summary>
public sealed class Board
{
    private readonly object _a = new();
    private readonly object _b = new();
    private string? _owner;

    public void SetUnderA(string owner)
    {
        lock (_a)
            _owner = owner;
    }

    public void SetUnderB(string owner)
    {
        lock (_b)
            _owner = owner;
    }
}

public sealed class FirstBoardWorker : BackgroundService
{
    private readonly Board _board;

    public FirstBoardWorker(Board board) => _board = board;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _board.SetUnderA("first");
        return Task.CompletedTask;
    }
}

public sealed class SecondBoardWorker : BackgroundService
{
    private readonly Board _board;

    public SecondBoardWorker(Board board) => _board = board;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _board.SetUnderB("second");
        return Task.CompletedTask;
    }
}

public static class LockDifferentIdentityCase
{
    public static IServiceCollection AddLockDifferentIdentity(this IServiceCollection services) =>
        services.AddSingleton<Board>()
                .AddHostedService<FirstBoardWorker>()
                .AddHostedService<SecondBoardWorker>();
}
