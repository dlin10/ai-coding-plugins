using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PlanForge.Jobs;
using PlanForge.Mcp;
using PlanForge.Repo;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests.Jobs;

public sealed class StatusJobTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-status-job-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Status_reports_the_active_job_then_null_after_terminal_completion()
    {
        var ct = CancellationToken.None;
        var run = await NewRun("status");
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new JobRegistry();
        var start = registry.Start(run.Path, "plan.review", _ => gate.Task);

        var active = JsonNode.Parse(await ForgeTools.Status(registry, SessionRoots.None, _workspace, run.RunId, ct))!;

        Assert.Equal(start.JobId, active["activeJob"]!["jobId"]!.GetValue<string>());
        Assert.Equal("plan.review", active["activeJob"]!["act"]!.GetValue<string>());
        Assert.Equal("running", active["activeJob"]!["state"]!.GetValue<string>());

        gate.SetResult("done");
        await registry.WaitAsync(run.Path, start.JobId, TimeSpan.FromSeconds(1), ct);
        var terminal = JsonNode.Parse(await ForgeTools.Status(registry, SessionRoots.None, _workspace, run.RunId, ct))!;

        Assert.Null(terminal["activeJob"]);
    }

    [Fact]
    public async Task Status_never_reports_a_terminal_record_as_active_during_handoff()
    {
        var ct = CancellationToken.None;
        var run = await NewRun("handoff");
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementAttempting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementRelease = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new JobRegistry();
        registry.Start(run.Path, "plan.review", _ =>
        {
            firstStarted.SetResult();
            return firstRelease.Task;
        });

        var replacementTask = Task.Run(async () =>
        {
            await firstStarted.Task;
            while (true)
            {
                var candidate = registry.Start(run.Path, "plan.review", _ => replacementRelease.Task);
                if (candidate.Started)
                    return candidate;
                replacementAttempting.TrySetResult();
                await Task.Yield();
            }
        });
        await replacementAttempting.Task;

        var observations = new ConcurrentBag<JsonNode>();
        var statusTask = Task.Run(async () =>
        {
            for (var index = 0; index < 10; index++)
                observations.Add(JsonNode.Parse(await ForgeTools.Status(registry, SessionRoots.None, _workspace, run.RunId, ct))!);
        });

        firstRelease.SetResult("first");
        var replacement = await replacementTask;
        await statusTask;

        Assert.NotEmpty(observations);
        Assert.All(observations, status =>
        {
            var active = status["activeJob"];
            Assert.True(active is null || active["state"]!.GetValue<string>() == "running");
        });

        replacementRelease.SetResult("replacement");
        await registry.WaitAsync(run.Path, replacement.JobId, TimeSpan.FromSeconds(1), ct);
    }

    private async Task<RunDirectory> NewRun(string runId)
    {
        var ct = CancellationToken.None;
        Directory.CreateDirectory(_workspace);
        var git = new GitClient(_workspace);
        await git.OutputAsync(["init", "-q"], ct);
        await git.OutputAsync(["config", "user.email", "tests@example.invalid"], ct);
        await git.OutputAsync(["config", "user.name", "PlanForge Tests"], ct);
        await File.WriteAllTextAsync(Path.Combine(_workspace, "tracked.txt"), "tracked\n", ct);
        await git.OutputAsync(["add", "tracked.txt"], ct);
        await git.OutputAsync(["commit", "-qm", "initial"], ct);

        var run = RunDirectory.Create(_workspace, runId);
        run.WriteState(new RunState(runId, _workspace, "Text", DateTimeOffset.Now, 0, 5, BaselineHead: "HEAD"));
        return run;
    }
}
