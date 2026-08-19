using Microsoft.Extensions.Hosting;

namespace PlanForge.Jobs;

internal sealed class JobRegistryHostedService(JobRegistry registry) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => registry.CloseAsync();
}
