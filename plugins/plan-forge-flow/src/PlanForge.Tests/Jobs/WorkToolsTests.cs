using System.Text.Json;
using System.Text.Json.Nodes;
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
    public async Task Start_poll_fetch_returns_the_one_call_critique_payload()
    {
        var run = NewRun("cycle");
        var vendor = new RecordingVendor("claude");
        var critique = new Critique("approve", [], "looks good");
        vendor.Enqueue(critique);
        var registry = new JobRegistry();

        var start = await ForgeTools.StartWork(registry, _workspace, run.RunId, "plan.review", "critic", null,
            "claude", "## draft", null, null, CancellationToken.None, () => vendor);
        Assert.Equal("running", JsonNode.Parse(start)!["state"]!.GetValue<string>());
        var jobId = JsonNode.Parse(start)!["jobId"]!.GetValue<string>();

        var poll = JsonNode.Parse(await ForgeTools.PollWork(registry, _workspace, run.RunId, jobId,
            CancellationToken.None))!;
        var fetch = JsonNode.Parse(await ForgeTools.FetchWork(registry, _workspace, run.RunId, jobId))!;

        Assert.Equal("succeeded", poll["state"]!.GetValue<string>());
        Assert.Equal("succeeded", fetch["state"]!.GetValue<string>());
        Assert.Equal(JsonSerializer.Serialize(critique, ContractJson.Default.Critique),
            fetch["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_act_and_blank_plan_do_not_construct_the_vendor()
    {
        var run = NewRun("invalid");
        var registry = new JobRegistry();
        var calls = 0;
        Func<IVendor> factory = () =>
        {
            calls++;
            return new RecordingVendor("claude");
        };

        await Assert.ThrowsAsync<ArgumentException>(() => ForgeTools.StartWork(registry, _workspace, run.RunId,
            "unknown", "critic", null, "claude", null, null, null, CancellationToken.None, factory));
        await Assert.ThrowsAsync<ArgumentException>(() => ForgeTools.StartWork(registry, _workspace, run.RunId,
            "plan.review", "critic", null, "claude", " ", null, null, CancellationToken.None, factory));

        Assert.Equal(0, calls);
        Assert.Null(registry.Get(run.Path));
        Assert.False(Directory.Exists(Path.Combine(run.Path, "jobs")));
    }

    [Fact]
    public async Task Invalid_act_and_blank_plan_are_rejected_before_rejoining_an_active_job()
    {
        var run = NewRun("invalid-active");
        var vendor = new BlockingVendor();
        var registry = new JobRegistry();
        var first = JsonNode.Parse(await ForgeTools.StartWork(registry, _workspace, run.RunId, "plan.review",
            "critic", null, "claude", "## draft", null, null, CancellationToken.None, () => vendor))!;
        var jobId = first["jobId"]!.GetValue<string>();
        var calls = 0;
        Func<IVendor> factory = () =>
        {
            calls++;
            return new RecordingVendor("claude");
        };

        await Assert.ThrowsAsync<ArgumentException>(() => ForgeTools.StartWork(registry, _workspace, run.RunId,
            "unknown", "critic", null, "claude", null, null, null, CancellationToken.None, factory));
        await Assert.ThrowsAsync<ArgumentException>(() => ForgeTools.StartWork(registry, _workspace, run.RunId,
            "plan.review", "critic", null, "claude", " ", null, null, CancellationToken.None, factory));

        Assert.Equal(0, calls);
        Assert.Equal(jobId, registry.Get(run.Path)?.Id);
        vendor.Release();
        await ForgeTools.PollWork(registry, _workspace, run.RunId, jobId, CancellationToken.None);
    }

    [Fact]
    public async Task A_failed_job_is_returned_by_fetch_without_throwing()
    {
        var run = NewRun("failure");
        var vendor = new RecordingVendor("claude");
        vendor.Enqueue(new BuildResult("done", [], new Verification("passed", "the checks ran"), "wrong schema"));
        var registry = new JobRegistry();

        var start = await ForgeTools.StartWork(registry, _workspace, run.RunId, "plan.review", "critic", null,
            "claude", "## draft", null, null, CancellationToken.None, () => vendor);
        var jobId = JsonNode.Parse(start)!["jobId"]!.GetValue<string>();
        await ForgeTools.PollWork(registry, _workspace, run.RunId, jobId, CancellationToken.None);

        var fetch = JsonNode.Parse(await ForgeTools.FetchWork(registry, _workspace, run.RunId, jobId))!;

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

        var first = JsonNode.Parse(await ForgeTools.StartWork(registry, _workspace, run.RunId, "plan.review", "critic",
            null, "claude", "## draft", null, null, CancellationToken.None, () => vendor))!;
        var jobId = first["jobId"]!.GetValue<string>();
        var second = JsonNode.Parse(await ForgeTools.StartWork(registry, _workspace, run.RunId, "plan.review", "critic",
            null, "claude", "## draft", null, null, CancellationToken.None, () => vendor))!;

        Assert.False(second["started"]!.GetValue<bool>());
        Assert.Equal(jobId, second["jobId"]!.GetValue<string>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => ForgeTools.FetchWork(registry, _workspace, run.RunId, jobId));

        vendor.Release();
        await ForgeTools.PollWork(registry, _workspace, run.RunId, jobId, CancellationToken.None);
    }

    [Fact]
    public async Task An_unknown_valid_job_id_is_rejected_by_poll_and_fetch()
    {
        var run = NewRun("unknown");
        var registry = new JobRegistry();
        const string jobId = "0123456789abcdef";

        await Assert.ThrowsAsync<InvalidOperationException>(() => ForgeTools.PollWork(registry, _workspace, run.RunId,
            jobId, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ForgeTools.FetchWork(registry, _workspace, run.RunId,
            jobId));
    }

    [Fact]
    public async Task Malformed_job_id_touches_no_jobs_file_but_is_logged()
    {
        var run = NewRun("malformed");
        var registry = new JobRegistry();

        await Assert.ThrowsAsync<ArgumentException>(() => ForgeTools.PollWork(registry, _workspace, run.RunId,
            "not-a-job", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => ForgeTools.FetchWork(registry, _workspace, run.RunId,
            "not-a-job"));

        Assert.False(Directory.Exists(Path.Combine(run.Path, "jobs")));
        Assert.Contains("forge.work.poll", File.ReadAllText(run.DiagnosticLogPath), StringComparison.Ordinal);
        Assert.Contains("forge.work.fetch", File.ReadAllText(run.DiagnosticLogPath), StringComparison.Ordinal);
    }

    private RunDirectory NewRun(string runId)
    {
        var run = RunDirectory.Create(_workspace, runId);
        run.WriteState(new RunState(runId, _workspace, "Text", DateTimeOffset.Now, 0, 5));
        return run;
    }

    private sealed class BlockingVendor : IVendor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "claude";
        public VendorCatalog Catalog { get; } = new([]);

        public void Release() => _release.SetResult();

        public Task<VendorReadiness> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new VendorReadiness(true, "blocking vendor"));

        public Task<IVendorSession> StartAsync(RoleSpec role, Selection selection, string? resumeToken,
                                               CancellationToken ct) =>
            Task.FromResult<IVendorSession>(new BlockingSession(_release));
    }

    private sealed class BlockingSession(TaskCompletionSource release) : IVendorSession
    {
        public IAsyncEnumerable<VendorEvent> Events => EmptyEvents();
        public bool CanResume => true;
        public string? ResumeToken => null;

        public async Task<T> RunAsync<T>(string prompt, VendorSchema<T> schema, CancellationToken ct)
        {
            await release.Task;
            return (T)(object)new Critique("approve", [], "released");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<VendorEvent> EmptyEvents()
        {
            yield break;
        }
    }

}
