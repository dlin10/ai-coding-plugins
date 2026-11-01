namespace Demo.Web.Cases.OwnPassJoin;

/// <summary>Hard negative: each loop pass awaits its helper and the helper awaits the task it just started, so writes from
/// different passes cannot overlap.</summary>
public sealed class HandoffWorker : BackgroundService
{
    private int _value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var pass = 0; pass < 2; pass++)
            await WritePass();
    }

    private async Task WritePass()
    {
        var own = Task.Run(Write);
        await own;
    }

    private void Write() => _value = 1;

    public int Snapshot => _value;
}

/// <summary>Hard negative: the seeding helper is called twice in a row and blocks on the async call it starts, so the second
/// call's tail starts after the first one's has finished.</summary>
public sealed class SeedTwiceWorker : BackgroundService
{
    public int Seeded { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Seed();
        Seed();
        return Task.CompletedTask;
    }

    private void Seed() => SeedAsync().Wait();

    private async Task SeedAsync()
    {
        await Task.Yield();
        Seeded = 12;
    }
}

/// <summary>Hard negative: a retry loop blocks on each attempt and catches what the wait rethrows; a wait ends with an exception
/// only after the attempt's task has finished, so the next attempt's tail starts after it.</summary>
public sealed class SeedRetryWorker : BackgroundService
{
    public int Seeded { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                SeedAsync().Wait();
                break;
            }
            catch (AggregateException)
            {
            }
        }

        return Task.CompletedTask;
    }

    private async Task SeedAsync()
    {
        await Task.Yield();
        Seeded = 12;
    }
}

public static class OwnPassJoinCase
{
    public static IServiceCollection AddOwnPassJoin(this IServiceCollection services) =>
        services.AddHostedService<HandoffWorker>()
                .AddHostedService<SeedTwiceWorker>()
                .AddHostedService<SeedRetryWorker>();
}
