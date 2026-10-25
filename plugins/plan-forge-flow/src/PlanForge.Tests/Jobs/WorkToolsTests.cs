using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PlanForge.Acts;
using PlanForge.Diagnostics;
using PlanForge.Evidence;
using PlanForge.Infrastructure;
using PlanForge.Jobs;
using PlanForge.Mcp;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests.Jobs;

public sealed class WorkToolsTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-work-tools-" + Guid.NewGuid().ToString("N"));

    public WorkToolsTests()
    {
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Start_poll_fetch_returns_the_one_call_build_payload()
    {
        var run = NewRun("cycle");
        var vendor = new RecordingVendor("claude");
        vendor.Enqueue(Built());
        var registry = new JobRegistry();

        var start = await StartBuild(registry, run, () => vendor);
        Assert.Equal("running", JsonNode.Parse(start)!["state"]!.GetValue<string>());
        var jobId = JsonNode.Parse(start)!["jobId"]!.GetValue<string>();

        var poll = JsonNode.Parse(await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, jobId,
            CancellationToken.None))!;
        var fetch = JsonNode.Parse(await ForgeTools.FetchWork(registry, SessionRoots.None, _workspace, run.RunId, jobId, CancellationToken.None))!;

        Assert.Equal("succeeded", poll["state"]!.GetValue<string>());
        Assert.Equal("succeeded", fetch["state"]!.GetValue<string>());
        var result = Assert.IsType<JsonObject>(fetch["result"]);
        Assert.Equal("done", result["result"]!["status"]!.GetValue<string>());
        Assert.Equal(1, result["tasksCompleted"]!.GetValue<int>());
    }

    /// <summary>
    /// The path travels with the result for the same reason the poll carries its next call: the
    /// instruction to surface the timeline lives in the skill, and a run that has been going for
    /// an hour has long since stopped reading it.
    /// </summary>
    [Fact]
    public async Task The_fetched_result_carries_the_run_documents_once_there_is_something_to_show()
    {
        var run = NewRun("timeline");
        var vendor = new RecordingVendor("claude");
        vendor.Enqueue(Built());
        var registry = new JobRegistry();

        var start = await StartBuild(registry, run, () => vendor);
        var jobId = JsonNode.Parse(start)!["jobId"]!.GetValue<string>();
        await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, jobId, CancellationToken.None);

        var fetch = JsonNode.Parse(await ForgeTools.FetchWork(registry, SessionRoots.None, _workspace, run.RunId, jobId, CancellationToken.None))!;

        var documents = fetch["documents"]!;
        Assert.Equal(run.FlowLogPath, documents["flowLog"]!["path"]!.GetValue<string>());
        Assert.Contains("show this file to the user now", documents["flowLog"]!["next"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.Equal(run.PlanPath, documents["plan"]!["path"]!.GetValue<string>());
        Assert.Contains("watch it change", documents["plan"]!["next"]!.GetValue<string>(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The job surface's shape of the same lag: the act runs for minutes behind start → poll →
    /// fetch, and the plan is on disk before the first of them, so the link has no reason to wait
    /// for the fetch that ends the wait.
    /// </summary>
    [Fact]
    public async Task A_started_job_carries_the_plan_written_before_it()
    {
        var run = NewRun("start-documents");
        var vendor = new BlockingVendor();
        var registry = new JobRegistry();

        var start = JsonNode.Parse(await StartBuild(registry, run, () => vendor))!;
        var jobId = start["jobId"]!.GetValue<string>();
        var poll = JsonNode.Parse(await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, jobId,
            TimeSpan.FromMilliseconds(50), CancellationToken.None))!;

        Assert.Equal(run.PlanPath, start["documents"]!["plan"]!["path"]!.GetValue<string>());
        Assert.Null(start["documents"]!["flowLog"]);
        Assert.Equal("running", poll["state"]!.GetValue<string>());
        Assert.Null(poll["documents"]);

        vendor.Release();
        await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, jobId, CancellationToken.None);
    }

    /// <summary>
    /// A long act is polled again and again, and the same documents on every poll were the larger
    /// half of what polling cost the Orchestrator. A poll repeats them only when they changed since
    /// the job last answered with them, so a file that first appears mid-job still reaches it once.
    /// </summary>
    [Fact]
    public async Task A_poll_carries_the_documents_only_when_they_changed_since_the_job_last_answered()
    {
        var run = NewRun("poll-documents");
        var vendor = new BlockingVendor();
        var registry = new JobRegistry();
        var start = JsonNode.Parse(await StartBuild(registry, run, () => vendor))!;
        var jobId = start["jobId"]!.GetValue<string>();
        async Task<JsonNode> Poll() => JsonNode.Parse(await ForgeTools.PollWork(registry, SessionRoots.None, _workspace,
            run.RunId, jobId, TimeSpan.FromMilliseconds(50), CancellationToken.None))!;

        var unchanged = await Poll();
        AtomicFile.Append(run.FlowLogPath, "## Timeline\n");
        var appeared = await Poll();
        var again = await Poll();

        Assert.Null(unchanged["documents"]);
        Assert.Equal(run.FlowLogPath, appeared["documents"]!["flowLog"]!["path"]!.GetValue<string>());
        Assert.Equal(run.PlanPath, appeared["documents"]!["plan"]!["path"]!.GetValue<string>());
        Assert.Null(again["documents"]);

        vendor.Release();
        await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, jobId, CancellationToken.None);
        var fetch = JsonNode.Parse(await ForgeTools.FetchWork(registry, SessionRoots.None, _workspace, run.RunId, jobId,
            CancellationToken.None))!;
        Assert.Equal(run.FlowLogPath, fetch["documents"]!["flowLog"]!["path"]!.GetValue<string>());
    }

    /// <summary>
    /// Reflection-based serialization is off repo-wide, so a result shape missing from
    /// <c>ForgeToolJson</c> compiles and then throws on the first real call — and the one-call
    /// tools that return these three cannot be reached without a live vendor CLI.
    /// </summary>
    [Fact]
    public void Every_act_result_shape_serializes_through_the_source_generated_context()
    {
        var documents = new RunDocuments(new RunDocument(@"C:\repo\.forge\run\flow_log.md", "show it"),
                                         new RunDocument(@"C:\repo\.forge\run\PLAN.md", "show it too"));
        var critique = new Critique("revise", [new Finding("major", "step 1", "no gate")], "one hole");
        var build = new BuildResult("done", ["tracked.cs"], new Verification("passed", "the checks ran"), "built");

        Assert.Contains("\"critique\"", JsonSerializer.Serialize(new CritiqueResult(critique, documents),
            ForgeToolJson.Default.CritiqueResult), StringComparison.Ordinal);
        Assert.Contains("\"tasksCompleted\"", BuildOutcomeView.Of(new BuildOutcome(build, 1, 1)).ToJsonString(),
                        StringComparison.Ordinal);
        Assert.Contains("\"fix\"", JsonSerializer.Serialize(new ReviewFixResult(build, documents),
            ForgeToolJson.Default.ReviewFixResult), StringComparison.Ordinal);
        var evidence = new EvidenceMapResult("fixture", "requested", null, false, 1, 0, 0, 0, 1, null, 0m, null, null, null,
                                             new Dictionary<string, int> { ["unchecked"] = 1 }, [], [], "map.json");
        Assert.Contains("\"remaining\"", JsonSerializer.Serialize(evidence, ForgeToolJson.Default.EvidenceMapResult),
                        StringComparison.Ordinal);
        var outline = PlanOutline.Of("# Plan\n");
        var shape = PlanShape.Of("# Plan\n", outline);
        Assert.Contains("\"outline\"", JsonSerializer.Serialize(new PlanOutlineResult("run", outline, documents),
            ForgeToolJson.Default.PlanOutlineResult), StringComparison.Ordinal);
        Assert.Contains("\"tasksKept\"", JsonSerializer.Serialize(new PlanForge.Mcp.PlanPatchResult("run", outline, shape, 0, 1, documents),
            ForgeToolJson.Default.PlanPatchResult), StringComparison.Ordinal);
        Assert.Contains("\"shape\"", JsonSerializer.Serialize(new PlanWriteResult("run", outline, shape, documents),
            ForgeToolJson.Default.PlanWriteResult), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_poll_that_times_out_asks_for_another_poll_and_a_terminal_one_asks_for_the_fetch()
    {
        var run = NewRun("next-call");
        var vendor = new BlockingVendor();
        var registry = new JobRegistry();

        var start = await StartBuild(registry, run, () => vendor);
        var jobId = JsonNode.Parse(start)!["jobId"]!.GetValue<string>();

        // The worker runs on a thread of its own, and `Emit` is what records the two activity
        // fields below — so polling straight after the start races the vendor's first line rather
        // than testing anything, and the 50 ms window loses that race whenever the suite is busy.
        // Waiting for the line to be recorded leaves the window meaning the one thing it is here
        // for: a poll that expires while the job is still running.
        await vendor.Recorded.WaitAsync(TimeSpan.FromSeconds(30));

        var running = JsonNode.Parse(await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, jobId,
            TimeSpan.FromMilliseconds(50), CancellationToken.None))!;

        Assert.Equal("running", running["state"]!.GetValue<string>());
        Assert.Contains("forge.work.poll", running["next"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotNull(running["lastActivityAt"]);
        Assert.Equal("command_execution: dotnet test", running["lastEvent"]!.GetValue<string>());

        vendor.Release();
        var settled = JsonNode.Parse(await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, jobId,
            CancellationToken.None))!;

        Assert.Equal("succeeded", settled["state"]!.GetValue<string>());
        Assert.Contains("forge.work.fetch", settled["next"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A poll may wait up to 3000 of the hour its host grants a call. Outside the range the wait is
    /// clamped and says so.
    /// </summary>
    [Theory]
    [InlineData(null, 45, false)]
    [InlineData(30, 30, false)]
    [InlineData(3000, 3000, false)]
    [InlineData(5000, 3000, true)]
    [InlineData(0, 1, true)]
    public void A_poll_waits_as_asked_within_its_range(int? waitSeconds, int expected, bool clamped)
    {
        var wait = PollWait.For(waitSeconds);

        Assert.Equal(TimeSpan.FromSeconds(expected), wait.Timeout);
        Assert.Equal(clamped, wait.Clamped is not null);
    }

    [Fact]
    public async Task A_long_poll_returns_the_moment_the_job_ends()
    {
        var run = NewRun("long-poll");
        var vendor = new BlockingVendor();
        var registry = new JobRegistry();
        var start = await StartBuild(registry, run, () => vendor);
        var jobId = JsonNode.Parse(start)!["jobId"]!.GetValue<string>();
        await vendor.Recorded.WaitAsync(TimeSpan.FromSeconds(30));

        var poll = ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, jobId, CancellationToken.None,
                                       3000);
        vendor.Release();
        var settled = JsonNode.Parse(await poll.WaitAsync(TimeSpan.FromSeconds(30)))!;

        Assert.Equal("succeeded", settled["state"]!.GetValue<string>());
        Assert.Null(settled["waitClamped"]);
    }

    [Fact]
    public async Task A_clamped_wait_is_reported_in_the_result_and_an_omitted_one_adds_nothing()
    {
        var run = NewRun("clamped-poll");
        var vendor = new RecordingVendor("claude");
        vendor.Enqueue(Built());
        var registry = new JobRegistry();
        var start = await StartBuild(registry, run, () => vendor);
        var jobId = JsonNode.Parse(start)!["jobId"]!.GetValue<string>();

        var plain = JsonNode.Parse(await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, jobId,
            CancellationToken.None))!;
        var clamped = JsonNode.Parse(await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, jobId,
            CancellationToken.None, 5000))!;

        Assert.Null(plain["waitClamped"]);
        Assert.Equal("succeeded", clamped["state"]!.GetValue<string>());
        Assert.Contains("up to 3000 seconds", clamped["waitClamped"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_is_non_blocking_and_the_job_settles_as_a_persisted_failure()
    {
        var run = NewRun("cancel");
        var vendor = new BlockingVendor();
        var registry = new JobRegistry();
        var start = JsonNode.Parse(await StartBuild(registry, run, () => vendor))!;
        var jobId = start["jobId"]!.GetValue<string>();

        var cancelled = JsonNode.Parse(await ForgeTools.CancelWork(registry, SessionRoots.None, _workspace, run.RunId,
            jobId, CancellationToken.None))!;
        var settled = JsonNode.Parse(await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId,
            jobId, CancellationToken.None))!;
        var repeated = JsonNode.Parse(await ForgeTools.CancelWork(registry, SessionRoots.None, _workspace, run.RunId,
            jobId, CancellationToken.None))!;
        var persisted = new JobRegistry().Get(run.Path, jobId);

        Assert.Contains("forge.work.poll", cancelled["next"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("failed", settled["state"]!.GetValue<string>());
        Assert.Equal("job was cancelled", settled["error"]!.GetValue<string>());
        Assert.Equal("failed", repeated["state"]!.GetValue<string>());
        Assert.Equal(JobState.Failed, persisted?.State);
        Assert.Equal("job was cancelled", persisted?.Error);
    }

    /// <summary>
    /// A job runs a Builder act or a Scout question and nothing else: the critic acts a job once
    /// carried for a host that cancelled every call at a minute have their one-call tools only —
    /// docs/adr/0043 and 0044.
    /// </summary>
    [Theory]
    [InlineData("unknown")]
    [InlineData("plan.review")]
    [InlineData("review.code")]
    public async Task An_act_a_job_does_not_run_does_not_construct_the_vendor(string act)
    {
        var run = NewRun("invalid");
        var registry = new JobRegistry();
        var calls = 0;
        Func<IVendor> factory = () =>
        {
            calls++;
            return new RecordingVendor("claude");
        };

        var rejection = await Assert.ThrowsAsync<ArgumentRejectedException>(() => ForgeTools.StartWork(registry,
            SessionRoots.None, _workspace, run.RunId, act, "builder", null, "claude", CancellationToken.None, factory));

        Assert.Contains("build.next, review.fix or scout", rejection.Message, StringComparison.Ordinal);
        Assert.Equal(0, calls);
        JobAssert.NoJob(registry, run.Path);
    }

    /// <summary>
    /// The arguments are read before the registry is, so a start that would have rejoined a running
    /// job still answers for what it was called with.
    /// </summary>
    [Fact]
    public async Task Invalid_arguments_are_rejected_before_rejoining_an_active_job()
    {
        var run = NewRun("invalid-active");
        var vendor = new BlockingVendor();
        var registry = new JobRegistry();
        var first = JsonNode.Parse(await StartBuild(registry, run, () => vendor))!;
        var jobId = first["jobId"]!.GetValue<string>();
        var calls = 0;
        Func<IVendor> factory = () =>
        {
            calls++;
            return new RecordingVendor("claude");
        };

        await Assert.ThrowsAsync<ArgumentRejectedException>(() => ForgeTools.StartWork(registry, SessionRoots.None, _workspace, run.RunId,
            "unknown", "builder", null, "claude", CancellationToken.None, factory));
        await Assert.ThrowsAsync<ArgumentRejectedException>(() => ForgeTools.StartWork(registry, SessionRoots.None, _workspace, run.RunId,
            "build.next", "builder", null, "claude", CancellationToken.None, factory, fixFindingIds: ["F-0001"]));

        Assert.Equal(0, calls);
        Assert.Equal([jobId], registry.Running(run.Path).Select(job => job.Id));
        vendor.Release();
        await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, jobId, CancellationToken.None);
    }

    [Fact]
    public async Task A_failed_job_is_returned_by_fetch_without_throwing()
    {
        var run = NewRun("failure");
        var vendor = new RecordingVendor("claude");
        vendor.Enqueue(new Critique("approve", [], "wrong schema"));
        var registry = new JobRegistry();

        var start = await StartBuild(registry, run, () => vendor);
        var jobId = JsonNode.Parse(start)!["jobId"]!.GetValue<string>();
        await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, jobId, CancellationToken.None);

        var fetch = JsonNode.Parse(await ForgeTools.FetchWork(registry, SessionRoots.None, _workspace, run.RunId, jobId, CancellationToken.None))!;

        Assert.Equal("failed", fetch["state"]!.GetValue<string>());
        Assert.Null(fetch["result"]);
        Assert.Contains("scripted response", fetch["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_start_returns_the_rejoin_shape_and_fetch_rejects_running_jobs()
    {
        var run = NewRun("rejoin");
        var vendor = new BlockingVendor();
        var registry = new JobRegistry();

        var first = JsonNode.Parse(await StartBuild(registry, run, () => vendor))!;
        var jobId = first["jobId"]!.GetValue<string>();
        var second = JsonNode.Parse(await StartBuild(registry, run, () => vendor))!;

        Assert.False(second["started"]!.GetValue<bool>());
        Assert.Equal(jobId, second["jobId"]!.GetValue<string>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => ForgeTools.FetchWork(registry, SessionRoots.None, _workspace, run.RunId, jobId, CancellationToken.None));

        vendor.Release();
        await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, jobId, CancellationToken.None);
    }

    [Fact]
    public async Task An_unknown_valid_job_id_is_rejected_by_poll_fetch_and_cancel()
    {
        var run = NewRun("unknown");
        var registry = new JobRegistry();
        const string jobId = "0123456789abcdef";

        await Assert.ThrowsAsync<InvalidOperationException>(() => ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId,
            jobId, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ForgeTools.FetchWork(registry, SessionRoots.None, _workspace, run.RunId,
            jobId, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ForgeTools.CancelWork(registry, SessionRoots.None, _workspace, run.RunId,
            jobId, CancellationToken.None));
    }

    [Fact]
    public async Task Malformed_job_id_touches_no_jobs_file_but_is_logged()
    {
        var run = NewRun("malformed");
        var registry = new JobRegistry();

        await Assert.ThrowsAsync<ArgumentRejectedException>(() => ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId,
            "not-a-job", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentRejectedException>(() => ForgeTools.FetchWork(registry, SessionRoots.None, _workspace, run.RunId,
            "not-a-job", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentRejectedException>(() => ForgeTools.CancelWork(registry, SessionRoots.None, _workspace, run.RunId,
            "not-a-job", CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(run.Path, "jobs")));
        Assert.Contains("forge.work.poll", AtomicFile.Read(run.DiagnosticLogPath), StringComparison.Ordinal);
        Assert.Contains("forge.work.fetch", AtomicFile.Read(run.DiagnosticLogPath), StringComparison.Ordinal);
        Assert.Contains("forge.work.cancel", AtomicFile.Read(run.DiagnosticLogPath), StringComparison.Ordinal);
    }

    /// <summary>An approved one-task plan whose gate is a condition, so a build runs no command.</summary>
    /// <param name="runId">The Run to create.</param>
    private RunDirectory NewRun(string runId)
    {
        var run = RunDirectory.Create(_workspace, runId);
        run.WritePlan("## Approach\n1. Task.\n   **Gate:** condition checked\n");
        run.WriteState(new RunState(runId, _workspace, "Text", DateTimeOffset.Now, 0, 5, Approved: true));
        return run;
    }

    private Task<string> StartBuild(JobRegistry registry, RunDirectory run, Func<IVendor> vendor) =>
        ForgeTools.StartWork(registry, SessionRoots.None, _workspace, run.RunId, "build.next", "builder", null, "claude",
                             CancellationToken.None, vendor);

    private static BuildResult Built() =>
        new("done", [], new Verification("passed", "condition checked"), "built");

    private sealed class BlockingVendor : IVendor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _recorded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "claude";
        public VendorCatalog Catalog { get; } = new([], CatalogSource.Resolved);

        /// <summary>
        /// Completes once the session has emitted its one event, and so once the activity fields a
        /// poll reports have been written. A test that asserts on those waits for this first; one
        /// that only asserts the job is running has nothing to wait for.
        /// </summary>
        public Task Recorded => _recorded.Task;

        public void Release() => _release.SetResult();

        public Task<VendorReadiness> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new VendorReadiness(true, "blocking vendor"));

        public Task<IVendorSession> StartAsync(RoleSpec role, Selection selection, string? resumeToken,
                                               CancellationToken ct) =>
            Task.FromResult<IVendorSession>(new BlockingSession(_release, _recorded));
    }

    private sealed class BlockingSession(TaskCompletionSource release, TaskCompletionSource recorded) : IVendorSession
    {
        public IAsyncEnumerable<VendorEvent> Events => EmptyEvents();
        public bool CanResume => true;
        public string? ResumeToken => null;

        public async Task<T> RunAsync<T>(string prompt, VendorSchema<T> schema, CancellationToken ct)
        {
            WorkerActivity.RecordOutput();
            var events = Channel.CreateUnbounded<VendorEvent>();
            events.Writer.Emit("claude", new VendorEvent(VendorEventKind.ToolUse, "command_execution",
                [("command", "dotnet test")]));
            recorded.TrySetResult();
            await release.Task.WaitAsync(ct);
            return (T)(object)new BuildResult("done", [], new Verification("passed", "condition checked"), "released");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<VendorEvent> EmptyEvents()
        {
            yield break;
        }
    }

}
