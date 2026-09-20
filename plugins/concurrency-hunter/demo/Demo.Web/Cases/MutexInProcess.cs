namespace Demo.Web.Cases.MutexInProcess;

/// <summary>Two workers take the same <see cref="Mutex"/> before writing and release it in `finally`: inside one
/// process a mutex excludes exactly like a lock.</summary>
public sealed class Turnstile
{
    public Mutex Gate { get; } = new();

    public string? LastPass { get; set; }
}

public sealed class FirstPassWorker : BackgroundService
{
    private readonly Turnstile _turnstile;

    public FirstPassWorker(Turnstile turnstile) => _turnstile = turnstile;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _turnstile.Gate.WaitOne();
        try
        {
            _turnstile.LastPass = "first";
        }
        finally
        {
            _turnstile.Gate.ReleaseMutex();
        }

        return Task.CompletedTask;
    }
}

public sealed class SecondPassWorker : BackgroundService
{
    private readonly Turnstile _turnstile;

    public SecondPassWorker(Turnstile turnstile) => _turnstile = turnstile;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _turnstile.Gate.WaitOne();
        try
        {
            _turnstile.LastPass = "second";
        }
        finally
        {
            _turnstile.Gate.ReleaseMutex();
        }

        return Task.CompletedTask;
    }
}

public static class MutexInProcessCase
{
    public static IServiceCollection AddMutexInProcess(this IServiceCollection services) =>
        services.AddSingleton<Turnstile>()
                .AddHostedService<FirstPassWorker>()
                .AddHostedService<SecondPassWorker>();
}
