using Polly;
using Polly.Retry;

namespace Demo.Web.Cases.PollyRetryAttempts;

/// <summary>Runs a retry policy's synchronous <c>Execute</c> once per attempt, in its own execution. The first call starts its
/// work and waits for it on every path, so the work of one attempt does not meet the work of the next. The second call's attempt
/// starts its work and may throw before it waits, so the work of an attempt that failed is still running when the next attempt
/// starts its own.</summary>
public sealed class SyncRetryWorker : BackgroundService
{
    private readonly RetryPolicy _retry = Policy.Handle<InvalidOperationException>().Retry(2);
    // The workers only write these fields; the writes are the case, so the unread-field warning is off.
#pragma warning disable CS0414
    private int _joined;
    private int _left;
#pragma warning restore CS0414

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _retry.Execute(() => Task.Run(() => { _joined = 1; }).Wait());
        _retry.Execute(() =>
        {
            var write = Task.Run(() => { _left = 1; });
            if (Environment.TickCount % 2 == 0)
            {
                throw new InvalidOperationException();
            }

            write.Wait();
        });
        return Task.CompletedTask;
    }
}

/// <summary>The same two calls through a retry policy's asynchronous <c>ExecuteAsync</c>, awaited one after the other: the first
/// attempt awaits its work on every path, the second may throw before it awaits.</summary>
public sealed class AsyncRetryWorker : BackgroundService
{
    private readonly AsyncRetryPolicy _retry = Policy.Handle<InvalidOperationException>().RetryAsync(2);
    // The workers only write these fields; the writes are the case, so the unread-field warning is off.
#pragma warning disable CS0414
    private int _joined;
    private int _left;
#pragma warning restore CS0414

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _retry.ExecuteAsync(async () => await Task.Run(() => { _joined = 1; }));
        await _retry.ExecuteAsync(async () =>
        {
            var write = Task.Run(() => { _left = 1; });
            if (Environment.TickCount % 2 == 0)
            {
                throw new InvalidOperationException();
            }

            await write;
        });
    }
}

public static class PollyRetryAttemptsCase
{
    public static IServiceCollection AddPollyRetryAttempts(this IServiceCollection services) =>
        services.AddHostedService<SyncRetryWorker>()
                .AddHostedService<AsyncRetryWorker>();
}
