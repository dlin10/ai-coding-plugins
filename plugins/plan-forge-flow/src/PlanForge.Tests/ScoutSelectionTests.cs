using System.Text.Json;
using PlanForge.Mcp;
using PlanForge.Infrastructure;
using PlanForge.Repo;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class ScoutSelectionTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void An_old_state_file_reads_with_an_unset_scout_selection()
    {
        var run = NewRun();
        File.WriteAllText(Path.Combine(run.Path, "state.json"),
            """
            {
              "runId": "old",
              "workspaceRoot": "C:\\workspace",
              "profile": "Text",
              "startedAt": "2026-09-03T00:00:00+00:00",
              "reviewRounds": 1,
              "reviewRoundCap": 5
            }
            """);

        Assert.Null(run.ReadState().Scout);
    }

    /// <summary>
    /// A Scout selection carries its speed beside its model and effort. It is confirmed when chosen
    /// rather than at the first question, and a refusal leaves the selection as it stood.
    /// </summary>
    [Fact]
    public async Task A_fast_scout_is_confirmed_when_selected_and_kept_with_the_selection()
    {
        var run = NewRun();
        var cache = new CatalogCache((id, _) => new RecordingVendor(id!)
        {
            Catalog = new VendorCatalog([new VendorModel("gpt-6-astra", ["low"]) { FastEfforts = ["low"] },
                                         new VendorModel("gpt-5.4-mini", ["low"])], CatalogSource.Live)
        });

        await ForgeTools.SelectScout(cache, SessionRoots.None, _workspace, run.RunId, true, CancellationToken.None,
            vendor: "codex", model: "gpt-6-astra", effort: "low", fast: true);
        Assert.True(run.ReadState().Scout!.Fast);

        await Assert.ThrowsAsync<ArgumentRejectedException>(() => ForgeTools.SelectScout(cache, SessionRoots.None,
            _workspace, run.RunId, true, CancellationToken.None, vendor: "codex", model: "gpt-5.4-mini", effort: "low",
            fast: true));
        Assert.Equal("gpt-6-astra", run.ReadState().Scout!.Model);
    }

    [Fact]
    public async Task A_selected_choice_persists_exactly_and_status_exposes_it()
    {
        var run = await NewGitRun();
        await ForgeTools.SelectScout(new CatalogCache(), SessionRoots.None, _workspace, run.RunId, true, CancellationToken.None,
            vendor: "codex", model: " model exactly ", effort: " effort exactly ");

        var state = run.ReadState().Scout;
        Assert.NotNull(state);
        Assert.True(state.Enabled);
        Assert.Equal("codex", state.Vendor);
        Assert.Equal(" model exactly ", state.Model);
        Assert.Equal(" effort exactly ", state.Effort);
        Assert.Null(state.SessionId);
        Assert.Null(state.LastFailure);

        using var status = JsonDocument.Parse(await ForgeTools.Status(SessionRoots.None, _workspace, run.RunId,
                                                                       CancellationToken.None));
        var scout = status.RootElement.GetProperty("run").GetProperty("scout");
        Assert.True(scout.GetProperty("enabled").GetBoolean());
        Assert.Equal("codex", scout.GetProperty("vendor").GetString());
        Assert.Equal(" model exactly ", scout.GetProperty("model").GetString());
        Assert.Equal(" effort exactly ", scout.GetProperty("effort").GetString());
    }

    [Fact]
    public async Task A_continue_without_scout_decline_persists_and_is_logged()
    {
        var run = NewRun();

        await ForgeTools.SelectScout(new CatalogCache(), SessionRoots.None, _workspace, run.RunId, false, CancellationToken.None);

        var state = run.ReadState().Scout;
        Assert.NotNull(state);
        Assert.False(state.Enabled);
        Assert.Null(state.Vendor);
        Assert.Null(state.Model);
        Assert.Null(state.Effort);
        Assert.Null(state.SessionId);
        Assert.Null(state.LastFailure);
        Assert.Contains("## Scout declined", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
        Assert.Contains("scout.declined", AtomicFile.Read(run.DiagnosticLogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_declined_run_can_be_explicitly_enabled()
    {
        var run = NewRun();
        await ForgeTools.SelectScout(new CatalogCache(), SessionRoots.None, _workspace, run.RunId, false, CancellationToken.None);

        await ForgeTools.SelectScout(new CatalogCache(), SessionRoots.None, _workspace, run.RunId, true, CancellationToken.None,
            vendor: "claude", model: "model", effort: null);

        var state = run.ReadState().Scout;
        Assert.NotNull(state);
        Assert.True(state.Enabled);
        Assert.Equal("claude", state.Vendor);
        Assert.Equal("model", state.Model);
        Assert.Contains("## Scout reselected", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
        Assert.Contains("scout.reselected", AtomicFile.Read(run.DiagnosticLogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeating_a_healthy_selection_reuses_the_session()
    {
        var run = NewRun(new ScoutState(true, "codex", "model", "low", "session-1"));

        using var result = JsonDocument.Parse(await ForgeTools.SelectScout(
            new CatalogCache(), SessionRoots.None, _workspace, run.RunId, true, CancellationToken.None,
            vendor: "codex", model: "model", effort: "low"));

        var state = run.ReadState().Scout;
        Assert.NotNull(state);
        Assert.Equal("session-1", state.SessionId);
        Assert.Null(state.LastFailure);
        Assert.Equal("resumed", result.RootElement.GetProperty("sessionState").GetString());
    }

    [Fact]
    public async Task Reselecting_after_failure_clears_the_prior_session_and_failure()
    {
        var run = NewRun(new ScoutState(true, "codex", "model", "low", "session-1",
                                        new ScoutFailure("vendor_failed", "previous failure")));

        using var result = JsonDocument.Parse(await ForgeTools.SelectScout(
            new CatalogCache(), SessionRoots.None, _workspace, run.RunId, true, CancellationToken.None,
            vendor: "codex", model: "model", effort: "low"));

        var state = run.ReadState().Scout;
        Assert.NotNull(state);
        Assert.Null(state.SessionId);
        Assert.Null(state.LastFailure);
        Assert.Equal("fresh", result.RootElement.GetProperty("sessionState").GetString());
        Assert.Contains("## Scout reselected", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_vendor_is_rejected_during_selection()
    {
        var run = NewRun();

        await Assert.ThrowsAsync<VendorException>(() => ForgeTools.SelectScout(
            new CatalogCache(), SessionRoots.None, _workspace, run.RunId, true, CancellationToken.None,
            vendor: "unknown", model: "model"));

        Assert.Null(run.ReadState().Scout);
        Assert.DoesNotContain("scout.selected", AtomicFile.Read(run.DiagnosticLogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disabled_selection_rejects_selection_fields()
    {
        var run = NewRun();

        await Assert.ThrowsAsync<ArgumentRejectedException>(() => ForgeTools.SelectScout(
            new CatalogCache(), SessionRoots.None, _workspace, run.RunId, false, CancellationToken.None,
            vendor: "codex"));

        Assert.Null(run.ReadState().Scout);
    }

    private RunDirectory NewRun(ScoutState? scout = null)
    {
        var runId = Guid.NewGuid().ToString("n");
        var run = RunDirectory.Create(_workspace, runId);
        run.WriteState(new RunState(runId, _workspace, "Text", DateTimeOffset.Now, 0, 5, Scout: scout));
        return run;
    }

    private async Task<RunDirectory> NewGitRun()
    {
        Directory.CreateDirectory(_workspace);
        var git = new GitClient(_workspace);
        await git.OutputAsync(["init", "-q"], CancellationToken.None);
        await git.OutputAsync(["config", "user.email", "tests@example.invalid"], CancellationToken.None);
        await git.OutputAsync(["config", "user.name", "PlanForge Tests"], CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_workspace, "tracked.txt"), "tracked\n");
        await git.OutputAsync(["add", "tracked.txt"], CancellationToken.None);
        await git.OutputAsync(["commit", "-qm", "initial"], CancellationToken.None);

        var baseline = await Baseline.CaptureAsync(git, CancellationToken.None);
        var run = NewRun();
        run.WriteBaseline(baseline);
        run.WriteState(run.ReadState() with { BaselineHead = baseline.Head });
        return run;
    }
}
