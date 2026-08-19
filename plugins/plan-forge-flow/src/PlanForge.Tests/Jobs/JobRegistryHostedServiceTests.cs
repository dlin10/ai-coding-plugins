using PlanForge.Jobs;
using Xunit;

namespace PlanForge.Tests.Jobs;

public sealed class JobRegistryHostedServiceTests
{
    [Fact]
    public async Task StopAsync_reaps_a_canceling_job_and_releases_its_slot()
    {
        var runPath = Path.Combine(Path.GetTempPath(), "planforge-hosted-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runPath);
        try
        {
            var registry = new JobRegistry();
            var start = registry.Start(runPath, "plan.review", async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return "unreachable";
            });
            var service = new JobRegistryHostedService(registry);

            await service.StopAsync(CancellationToken.None);

            var terminal = registry.Get(runPath, start.JobId);
            Assert.NotNull(terminal);
            Assert.Equal(JobState.Failed, terminal!.State);
            Assert.Equal(JobState.Failed, registry.Get(runPath)!.State);
        }
        finally
        {
            try { Directory.Delete(runPath, true); }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException) { }
        }

        // An outright server kill still orphans the vendor process; this hosted-service path does
        // not cover that named cost documented in docs/adr/0006.
    }
}
