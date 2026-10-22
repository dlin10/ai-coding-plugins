using PlanForge.Jobs;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Assertions about the jobs a registry holds for one Run.</summary>
internal static class JobAssert
{
    /// <summary>
    /// The registry started no job for the Run — not even one that already ended: none runs, and none
    /// finished, since a finished job leaves its file in the Run's <c>jobs</c> folder, which nothing
    /// but a job creates.
    /// </summary>
    /// <param name="registry">The registry a job of the Run would have started in.</param>
    /// <param name="runPath">The Run's folder.</param>
    public static void NoJob(JobRegistry registry, string runPath)
    {
        Assert.Empty(registry.Running(runPath));
        Assert.False(Directory.Exists(Path.Combine(runPath, "jobs")), "a job left its file in the Run's jobs folder");
    }
}
