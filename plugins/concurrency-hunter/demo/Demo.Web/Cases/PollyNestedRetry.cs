using Polly;
using Polly.Retry;

namespace Demo.Web.Cases.PollyNestedRetry;

/// <summary>Seeds the way a database seeder does under a retry policy: the synchronous <c>Execute</c> runs a delegate that blocks
/// on <c>SeedAsync()</c>, and <c>SeedAsync()</c> awaits its own asynchronous retry policy. The delegate waits for the seeding on
/// every path, so the work of one attempt of the outer retry is finished before the next attempt starts.</summary>
public sealed class NestedRetryWorker : BackgroundService
{
    private readonly RetryPolicy _retry = Policy.Handle<InvalidOperationException>().Retry(2);
    private readonly AsyncRetryPolicy _seed = Policy.Handle<InvalidOperationException>().RetryAsync(2);
    // The workers only write these fields; the writes are the case, so the unread-field warning is off.
#pragma warning disable CS0414
    private int _seeded;
#pragma warning restore CS0414

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _retry.Execute(() => SeedAsync().Wait());
        return Task.CompletedTask;
    }

    private async Task SeedAsync()
    {
        await _seed.ExecuteAsync(async () =>
        {
            _seeded = 1;
            await Task.Yield();
            _seeded = 2;
        });
    }
}

/// <summary>The same nesting, but the delegate of the outer retry starts <c>SeedAsync()</c> and leaves it running: the next attempt
/// starts its own seeding while the seeding of the attempt before is still going.</summary>
public sealed class UnwaitedNestedRetryWorker : BackgroundService
{
    private readonly RetryPolicy _retry = Policy.Handle<InvalidOperationException>().Retry(2);
    private readonly AsyncRetryPolicy _seed = Policy.Handle<InvalidOperationException>().RetryAsync(2);
    // The workers only write these fields; the writes are the case, so the unread-field warning is off.
#pragma warning disable CS0414
    private int _left;
#pragma warning restore CS0414

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _retry.Execute(() => { var detached = SeedAsync(); });
        return Task.CompletedTask;
    }

    private async Task SeedAsync()
    {
        await _seed.ExecuteAsync(async () =>
        {
            _left = 1;
            await Task.Yield();
            _left = 2;
        });
    }
}

public static class PollyNestedRetryCase
{
    public static IServiceCollection AddPollyNestedRetry(this IServiceCollection services) =>
        services.AddHostedService<NestedRetryWorker>()
                .AddHostedService<UnwaitedNestedRetryWorker>();
}
