using PlanForge.Jobs;
using Xunit;

namespace PlanForge.Tests.Jobs;

public sealed class JobRegistryTests
{
    [Fact]
    public async Task Second_start_returns_the_active_job()
    {
        using var workspace = new TestWorkspace();
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new JobRegistry();

        var first = registry.Start(workspace.RunPath, "plan", _ => gate.Task);
        var second = registry.Start(workspace.RunPath, "plan", _ => Task.FromResult("loser"));

        Assert.True(first.Started);
        Assert.False(second.Started);
        Assert.Equal(first.JobId, second.JobId);
        gate.SetResult("winner");
        await registry.WaitAsync(workspace.RunPath, first.JobId, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Start_returns_a_running_snapshot_even_if_the_delegate_finishes_immediately()
    {
        using var workspace = new TestWorkspace();
        var registry = new JobRegistry();

        var start = registry.Start(workspace.RunPath, "plan", _ => Task.FromResult("done"));

        Assert.True(start.Started);
        Assert.Equal(JobState.Running, start.Record.State);
        await registry.WaitAsync(workspace.RunPath, start.JobId, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Wait_returns_on_completion_without_waiting_for_the_timeout()
    {
        using var workspace = new TestWorkspace();
        var registry = new JobRegistry();
        var start = registry.Start(workspace.RunPath, "plan", _ => Task.FromResult("done"));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await registry.WaitAsync(workspace.RunPath, start.JobId, TimeSpan.FromSeconds(10));

        Assert.Equal(JobState.Completed, result?.State);
        Assert.Equal("done", result?.ResultPayload);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Running_jobs_are_not_visible_to_a_new_registry()
    {
        using var workspace = new TestWorkspace();
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new JobRegistry();
        var start = registry.Start(workspace.RunPath, "plan", _ => gate.Task);

        Assert.False(Directory.Exists(Path.Combine(workspace.RunPath, "jobs")));
        Assert.Null(new JobRegistry().Get(workspace.RunPath, start.JobId));

        gate.SetResult("done");
        await registry.WaitAsync(workspace.RunPath, start.JobId, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Wait_returns_at_the_timeout_while_the_job_is_running()
    {
        using var workspace = new TestWorkspace();
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new JobRegistry();
        var start = registry.Start(workspace.RunPath, "plan", _ => gate.Task);

        var result = await registry.WaitAsync(workspace.RunPath, start.JobId, TimeSpan.FromMilliseconds(25));

        Assert.Equal(JobState.Running, result?.State);
        gate.SetResult("done");
        await registry.WaitAsync(workspace.RunPath, start.JobId, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Completed_job_round_trips_through_the_file()
    {
        using var workspace = new TestWorkspace();
        var registry = new JobRegistry();
        var start = registry.Start(workspace.RunPath, "plan", _ => Task.FromResult("payload"));
        var completed = await registry.WaitAsync(workspace.RunPath, start.JobId, TimeSpan.FromSeconds(1));

        var roundTripped = new JobRegistry().Get(workspace.RunPath);

        Assert.Equal(completed, roundTripped);
    }

    [Fact]
    public async Task Jobs_are_scoped_by_the_canonical_run_path()
    {
        using var firstWorkspace = new TestWorkspace();
        using var secondWorkspace = new TestWorkspace();
        var registry = new JobRegistry();
        var first = registry.Start(firstWorkspace.RunPath, "plan", _ => Task.FromResult("first"));
        var second = registry.Start(secondWorkspace.RunPath, "plan", _ => Task.FromResult("second"));

        Assert.NotEqual(first.JobId, second.JobId);
        Assert.Equal(first.JobId, registry.Get(firstWorkspace.RunPath)?.Id);
        Assert.Equal(second.JobId, registry.Get(secondWorkspace.RunPath)?.Id);
        await registry.WaitAsync(firstWorkspace.RunPath, first.JobId, TimeSpan.FromSeconds(1));
        await registry.WaitAsync(secondWorkspace.RunPath, second.JobId, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Run_path_casing_uses_one_registry_key()
    {
        using var workspace = new TestWorkspace();
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new JobRegistry();
        var first = registry.Start(workspace.RunPath, "plan", _ => gate.Task);
        var second = registry.Start(workspace.RunPath.ToUpperInvariant(), "plan", _ => Task.FromResult("loser"));

        Assert.False(second.Started);
        Assert.Equal(first.JobId, second.JobId);
        gate.SetResult("done");
        await registry.WaitAsync(workspace.RunPath, first.JobId, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Old_terminal_job_remains_fetchable_when_a_replacement_is_active()
    {
        using var workspace = new TestWorkspace();
        var replacementGate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new JobRegistry();
        var first = registry.Start(workspace.RunPath, "plan", _ => Task.FromResult("first"));
        var firstResult = await registry.WaitAsync(workspace.RunPath, first.JobId, TimeSpan.FromSeconds(1));
        var replacement = registry.Start(workspace.RunPath, "plan", _ => replacementGate.Task);

        Assert.Equal(JobState.Completed, firstResult?.State);
        Assert.Equal(firstResult, registry.Get(workspace.RunPath, first.JobId));
        Assert.Equal(firstResult, await registry.WaitAsync(workspace.RunPath, first.JobId, TimeSpan.FromSeconds(1)));

        replacementGate.SetResult("replacement");
        await registry.WaitAsync(workspace.RunPath, replacement.JobId, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Persistence_failure_still_completes_and_records_a_warning()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.RunPath, "jobs"), "not a directory");
        var registry = new JobRegistry();
        var start = registry.Start(workspace.RunPath, "plan", _ => Task.FromResult("done"));

        var completed = await registry.WaitAsync(workspace.RunPath, start.JobId, TimeSpan.FromSeconds(1));

        Assert.Equal(JobState.Completed, completed?.State);
        Assert.Equal(completed, registry.Get(workspace.RunPath, start.JobId));
        Assert.Contains("job.persistence", File.ReadAllText(Path.Combine(workspace.RunPath, "forge.log")),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void Job_id_cannot_escape_the_jobs_folder()
    {
        using var workspace = new TestWorkspace();
        var registry = new JobRegistry();

        Assert.Throws<ArgumentException>(() => registry.Get(workspace.RunPath, "../outside"));
        Assert.False(File.Exists(Path.Combine(workspace.RunPath, "outside.json")));
    }

    [Fact]
    public async Task Failing_delegate_is_recorded_as_failed()
    {
        using var workspace = new TestWorkspace();
        var registry = new JobRegistry();
        var start = registry.Start(workspace.RunPath, "plan", _ => throw new InvalidOperationException("boom"));

        var result = await registry.WaitAsync(workspace.RunPath, start.JobId, TimeSpan.FromSeconds(1));

        Assert.Equal(JobState.Failed, result?.State);
        Assert.Equal("boom", result?.Error);
    }

    [Fact]
    public async Task Completion_handoff_keeps_the_replacement_active()
    {
        using var workspace = new TestWorkspace();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementAttempting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementRelease = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new JobRegistry();
        var first = registry.Start(workspace.RunPath, "plan", _ =>
        {
            firstStarted.SetResult();
            return firstRelease.Task;
        });
        var replacementTask = Task.Run(async () =>
        {
            await firstStarted.Task;
            replacementAttempting.SetResult();
            while (true)
            {
                var candidate = registry.Start(workspace.RunPath, "plan", _ => replacementRelease.Task);
                if (candidate.Started)
                    return candidate;
                await Task.Yield();
            }
        });

        await replacementAttempting.Task;
        firstRelease.SetResult("first");
        var replacement = await replacementTask;
        var firstResult = await registry.WaitAsync(workspace.RunPath, first.JobId, TimeSpan.FromSeconds(1));

        Assert.Equal(JobState.Completed, firstResult?.State);
        Assert.True(replacement.Started);
        Assert.Equal(replacement.JobId, registry.Get(workspace.RunPath)?.Id);
        Assert.Equal(JobState.Running, registry.Get(workspace.RunPath)?.State);
        replacementRelease.SetResult("replacement");
        await registry.WaitAsync(workspace.RunPath, replacement.JobId, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Start_racing_close_is_terminal_or_refused_when_close_returns()
    {
        using var workspace = new TestWorkspace();
        var registry = new JobRegistry();
        var startTask = Task.Run(() => registry.Start(workspace.RunPath, "plan", _ => Task.FromResult("done")));

        await registry.CloseAsync();
        JobStartResult? start = null;
        try
        {
            start = await startTask;
        }
        catch (InvalidOperationException)
        {
        }

        if (start is { Started: true })
        {
            Assert.True(registry.Get(workspace.RunPath)?.State != JobState.Running);
            Assert.True((await registry.WaitAsync(workspace.RunPath, start.JobId, TimeSpan.Zero))?.State != JobState.Running);
        }
        else
        {
            Assert.Null(registry.Get(workspace.RunPath));
        }
    }

    [Fact]
    public async Task Close_forces_an_ignoring_delegate_and_ignores_its_late_completion()
    {
        using var workspace = new TestWorkspace();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new JobRegistry();
        var start = registry.Start(workspace.RunPath, "plan", _ => release.Task);
        await registry.CloseAsync();
        var forced = registry.Get(workspace.RunPath);
        var filePath = Directory.EnumerateFiles(Path.Combine(workspace.RunPath, "jobs"), "*.json").Single();
        var afterClose = File.ReadAllText(filePath);

        Assert.Equal(JobState.Failed, forced?.State);
        Assert.Null(forced?.ResultPayload);
        Assert.NotNull(forced?.Error);
        Assert.DoesNotContain("\"state\":\"Running\"", afterClose);
        Assert.Equal(afterClose, File.ReadAllText(filePath));

        release.SetResult("late");
        await Task.Delay(25);

        Assert.Equal(forced, registry.Get(workspace.RunPath));
        Assert.Equal(afterClose, File.ReadAllText(filePath));
        Assert.True((await registry.WaitAsync(workspace.RunPath, start.JobId, TimeSpan.Zero))?.State != JobState.Running);
    }

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "planforge-jobs-" + Guid.NewGuid().ToString("N"));

        public string RunPath { get; }

        public TestWorkspace()
        {
            RunPath = Path.Combine(_root, ".forge", "run");
            Directory.CreateDirectory(RunPath);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
