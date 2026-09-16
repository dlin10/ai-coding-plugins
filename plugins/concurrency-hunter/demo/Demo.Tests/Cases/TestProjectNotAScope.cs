using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Demo.Tests.Cases.TestProjectNotAScope;

/// <summary>Negative: a test project is not a process scope. It references the Worker application, so if it
/// were one, its worker would be paired with that application's worker on the shared-library static.</summary>
public sealed class ReplayWorker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Demo.Domain.Cases.SharedLibraryStatic.LastSync.Source = "replay";
        return Task.CompletedTask;
    }
}

public sealed class ReplayWorkerTests
{
    [Fact]
    public void Replay_worker_can_be_registered()
    {
        var services = new ServiceCollection();

        services.AddHostedService<ReplayWorker>();

        Assert.Single(services);
    }
}
