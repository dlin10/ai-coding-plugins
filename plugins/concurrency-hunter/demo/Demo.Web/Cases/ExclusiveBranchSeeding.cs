namespace Demo.Web.Cases.ExclusiveBranchSeeding;

/// <summary>Options the seeding worker reads; they never change after construction.</summary>
public sealed class SeedingOptions
{
    public bool UnderOrchestrator { get; } = true;
}

/// <summary>Hard negative: the seeding helper is called from both branches of one <c>if</c>, as a database seeder runs either
/// directly or under a retry policy, so one run of the worker seeds once and the tail of the async call it waits for does not
/// overlap itself. The helper it calls twice in a row without waiting starts two tails, and they overlap.</summary>
public sealed class SeedingWorker : BackgroundService
{
    private readonly SeedingOptions _options;

    public SeedingWorker(SeedingOptions options) => _options = options;

    public int Seeded { get; private set; }

    public int Detached { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.UnderOrchestrator)
            Seed();
        else
            Seed();

        Detach();
        Detach();
        return Task.CompletedTask;
    }

    private void Seed() => SeedAsync().Wait();

    private async Task SeedAsync()
    {
        await Task.Yield();
        Seeded = 12;
    }

    private void Detach()
    {
        var detached = DetachAsync();
    }

    private async Task DetachAsync()
    {
        await Task.Yield();
        Detached = 12;
    }
}

public static class ExclusiveBranchSeedingCase
{
    public static IServiceCollection AddExclusiveBranchSeeding(this IServiceCollection services) =>
        services.AddSingleton<SeedingOptions>()
                .AddHostedService<SeedingWorker>();
}
