using Demo.Domain.Cases.SharedLibraryStatic;

namespace Demo.Worker.Cases.SharedLibraryStatic;

/// <summary>A worker in another application writes shared-library static state independently of the web app.</summary>
public sealed class SyncWorker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LastSync.Source = "worker";
        return Task.CompletedTask;
    }
}
