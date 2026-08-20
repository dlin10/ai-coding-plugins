using PlanForge.Acts;
using PlanForge.Prompts;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class BuildTests : IDisposable
{
    private const string Plan =
        """
        # Toy plan

        ## Approach

        1. First task.
        2. Second task.
        """;

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_fresh_builder_does_not_receive_a_foreign_token_and_records_its_vendor()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "done"), "new-token");
        var run = NewRun("claude", "foreign-token");

        await new Build(vendor, new PromptLibrary(RepositoryPrompts())).NextAsync(run,
                                                                                  new Selection("builder-model", "low"),
                                                                                  CancellationToken.None);

        var session = Assert.Single(vendor.Sessions);
        Assert.Null(session.StartedWithResumeToken);
        Assert.Equal("new-token", run.ReadState().BuilderSessionId);
        Assert.Equal(vendor.Id, run.ReadState().BuilderVendor);
    }

    [Fact]
    public async Task A_fresh_null_token_clears_the_foreign_token_and_the_next_call_stays_fresh()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "done"));
        vendor.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "done"));
        var run = NewRun("claude", "foreign-token");
        var build = new Build(vendor, new PromptLibrary(RepositoryPrompts()));

        await build.NextAsync(run, new Selection("builder-model", "low"), CancellationToken.None);
        await build.NextAsync(run, new Selection("builder-model", "low"), CancellationToken.None);

        Assert.Equal(2, vendor.Sessions.Count);
        Assert.All(vendor.Sessions, session => Assert.Null(session.StartedWithResumeToken));
        Assert.Equal(string.Empty, run.ReadState().BuilderSessionId);
        Assert.Equal(vendor.Id, run.ReadState().BuilderVendor);
    }

    [Fact]
    public async Task A_builder_reuses_a_token_recorded_for_the_same_vendor()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "done"), "next-token");
        var run = NewRun("codex", "existing-token");

        await new Build(vendor, new PromptLibrary(RepositoryPrompts())).NextAsync(run,
                                                                                  new Selection("builder-model", "low"),
                                                                                  CancellationToken.None);

        Assert.Equal("existing-token", Assert.Single(vendor.Sessions).StartedWithResumeToken);
        Assert.Equal("next-token", run.ReadState().BuilderSessionId);
        Assert.Equal(vendor.Id, run.ReadState().BuilderVendor);
    }

    [Fact]
    public async Task The_build_outcome_lands_in_the_flow_log()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new BuildResult("done", ["tracked.txt"], new Verification("passed", "the checks ran"), "first task built"));
        var run = NewRun("", "");

        await new Build(vendor, new PromptLibrary(RepositoryPrompts())).NextAsync(run,
                                                                                  new Selection("builder-model", null),
                                                                                  CancellationToken.None);

        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("## Task 1 of 2", flow, StringComparison.Ordinal);
        Assert.Contains("Status: done", flow, StringComparison.Ordinal);
        Assert.Contains("Verification: passed — the checks ran", flow, StringComparison.Ordinal);
        Assert.Contains("first task built", flow, StringComparison.Ordinal);
        Assert.Contains("- `tracked.txt`", flow, StringComparison.Ordinal);
    }

    private RunDirectory NewRun(string builderVendor, string builderSessionId)
    {
        var run = RunDirectory.Create(_workspace, "build");
        run.WritePlan(Plan);
        run.WriteState(new RunState("build", _workspace, "Text", DateTimeOffset.Now, 0, 5,
                                    Approved: true, BuilderSessionId: builderSessionId, BuilderVendor: builderVendor));
        return run;
    }

    private static string RepositoryPrompts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var prompts = Path.Combine(directory.FullName, "prompts");
            if (Directory.Exists(prompts)) return prompts;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("could not locate the prompts folder above the test binary");
    }
}
