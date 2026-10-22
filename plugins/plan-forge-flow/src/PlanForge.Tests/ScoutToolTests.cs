using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PlanForge.Acts;
using PlanForge.Infrastructure;
using PlanForge.Jobs;
using PlanForge.Mcp;
using PlanForge.Prompts;
using PlanForge.Repo;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class ScoutToolTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-scout-tools-" + Guid.NewGuid().ToString("N"));
    private readonly PromptLibrary _prompts;

    public ScoutToolTests()
    {
        Directory.CreateDirectory(_workspace);
        _prompts = new PromptLibrary(RepositoryPrompts());
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Direct result carries the complete report and documents.</summary>
    [Fact]
    public async Task Direct_result_carries_the_complete_report_and_documents()
    {
        var run = NewRun();
        var vendor = VendorWith("direct", "session");

        using var result = JsonDocument.Parse(await Direct(run, vendor));
        var root = result.RootElement;

        Assert.True(root.TryGetProperty("scout", out var scout));
        Assert.True(scout.TryGetProperty("dependentsAndPinnedBehaviour", out _));
        Assert.False(scout.TryGetProperty("truncated", out _));
        Assert.False(root.TryGetProperty("report", out _));
        Assert.Equal(run.ScoutAnswersPath, root.GetProperty("documents").GetProperty("scout").GetProperty("path").GetString());
    }

    [Fact]
    public async Task Direct_success_is_the_only_direct_path_that_includes_scout_metadata()
    {
        var run = NewRun();
        await Direct(run, VendorWith("direct", "session"));

        using var result = JsonDocument.Parse(await ForgeTools.WritePlan(
            SessionRoots.None, _workspace, run.RunId, "## draft", CancellationToken.None));

        Assert.False(result.RootElement.GetProperty("documents").TryGetProperty("scout", out _));
    }

    [Fact]
    public async Task Scout_document_metadata_uses_the_exact_display_instruction()
    {
        var run = NewRun();
        using var result = JsonDocument.Parse(await Direct(run, VendorWith("direct", "session")));

        Assert.Equal(
            "show the Scout report to the user now, and show it again after each later successful Scout call appends its answer.",
            result.RootElement.GetProperty("documents").GetProperty("scout").GetProperty("next").GetString());
    }

    [Fact]
    public void Direct_tool_does_not_repeat_selection_arguments()
    {
        var schema = ToolSchema(nameof(ForgeTools.ScoutRun));

        Assert.Equal(["workspaceRoot", "runId", "question", "sessionMode"],
            schema.GetProperty("properties").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task Direct_requires_an_explicit_session_mode()
    {
        var run = NewRun();
        var starts = 0;

        await Assert.ThrowsAsync<ArgumentRejectedException>(() =>
            ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "question", "",
                                CancellationToken.None, _ =>
                                {
                                    starts++;
                                    return VendorWith("direct", "session");
                                }, _prompts));

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Direct_rejects_an_invalid_session_mode_before_factory_construction()
    {
        var run = NewRun();
        var starts = 0;

        await Assert.ThrowsAsync<ArgumentRejectedException>(() =>
            ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "question", "resume",
                                CancellationToken.None, _ =>
                                {
                                    starts++;
                                    return VendorWith("direct", "session");
                                }, _prompts));

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Continue_without_a_current_token_starts_without_resume()
    {
        var run = NewRun(new ScoutState(true, "codex", "model", "effort"));
        var vendor = VendorWith("continue", "new-session");

        await Direct(run, vendor, mode: "continue");

        Assert.Null(Assert.Single(vendor.Sessions).StartedWithResumeToken);
        Assert.Equal("new-session", run.ReadState().Scout!.SessionId);
    }

    [Fact]
    public async Task Continue_with_a_current_token_reuses_the_persisted_anchor()
    {
        var run = NewRun(new ScoutState(true, "codex", "model", "effort", "old-session"));
        var vendor = VendorWith("continue", "new-session");

        await Direct(run, vendor, mode: "continue");

        Assert.Equal("old-session", Assert.Single(vendor.Sessions).StartedWithResumeToken);
        Assert.Equal("new-session", run.ReadState().Scout!.SessionId);
    }

    [Fact]
    public async Task Fresh_abandons_the_previous_token_and_installs_the_new_anchor()
    {
        var run = NewRun(new ScoutState(true, "codex", "model", "effort", "old-session"));
        var vendor = VendorWith("fresh", "new-session");

        await Direct(run, vendor, mode: "fresh");

        Assert.Null(Assert.Single(vendor.Sessions).StartedWithResumeToken);
        Assert.Equal("new-session", run.ReadState().Scout!.SessionId);
    }

    [Fact]
    public async Task Direct_missing_selection_is_refused_before_factory_construction()
    {
        var run = NewRunCore(null);
        var starts = 0;

        await Assert.ThrowsAsync<ArgumentRejectedException>(() =>
            ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "question", "fresh",
                                CancellationToken.None, _ =>
                                {
                                    starts++;
                                    return VendorWith("direct", "session");
                                }, _prompts));

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Direct_disabled_selection_is_refused_before_factory_construction()
    {
        var run = NewRunCore(new ScoutState(false));
        var starts = 0;

        await Assert.ThrowsAsync<ArgumentRejectedException>(() =>
            ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "question", "fresh",
                                CancellationToken.None, _ =>
                                {
                                    starts++;
                                    return VendorWith("direct", "session");
                                }, _prompts));

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Direct_reuses_the_exact_persisted_model_and_effort()
    {
        var run = NewRun(new ScoutState(true, "codex", " model exactly ", " effort exactly "));
        var vendor = VendorWith("selection", "session");

        await Direct(run, vendor);

        var session = Assert.Single(vendor.Sessions);
        Assert.Equal(" model exactly ", session.Selection.Model);
        Assert.Equal(" effort exactly ", session.Selection.Effort);
    }

    [Fact]
    public async Task Direct_id_keyed_factory_receives_the_persisted_vendor_id()
    {
        var run = NewRun(new ScoutState(true, "codex", "model", null));
        var vendor = VendorWith("codex", "session");
        string? requested = null;

        await ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "question", "fresh",
            CancellationToken.None, id =>
            {
                requested = id;
                return vendor;
            }, _prompts);

        Assert.Equal("codex", requested);
    }

    [Fact]
    public void Job_schema_makes_model_optional_but_runtime_requires_it()
    {
        var schema = ToolSchema(nameof(ForgeTools.StartWork));
        var properties = schema.GetProperty("properties");

        Assert.Contains("model", properties.EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("model", schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Throws<ArgumentRejectedException>(() => WorkAct.ValidateArguments("build.next", null));
    }

    [Fact]
    public async Task Failed_status_has_no_fallback_and_successful_recovery_clears_it()
    {
        var run = await NewGitRun();
        var failed = new RecordingVendor("codex");
        failed.Enqueue(new VendorException("stderr must not escape", 3), "failed-session");
        await Assert.ThrowsAsync<ScoutException>(() => Direct(run, failed));

        using (var failedStatus = JsonDocument.Parse(await ForgeTools.Status(SessionRoots.None, _workspace,
                   run.RunId, CancellationToken.None)))
        {
            var scout = failedStatus.RootElement.GetProperty("run").GetProperty("scout");
            Assert.Equal("vendor_failed", scout.GetProperty("lastFailure").GetProperty("code").GetString());
        }

        await Direct(run, VendorWith("recovered", "recovered-session"), mode: "continue");
        using var recoveredStatus = JsonDocument.Parse(await ForgeTools.Status(SessionRoots.None, _workspace,
            run.RunId, CancellationToken.None));
        Assert.Equal(JsonValueKind.Null, recoveredStatus.RootElement.GetProperty("run").GetProperty("scout")
            .GetProperty("lastFailure").ValueKind);
    }

    private async Task<string> Direct(RunDirectory run,
                                      RecordingVendor vendor,
                                      string mode = "fresh") =>
        await ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "bounded question", mode,
            CancellationToken.None, _ => vendor, _prompts);

    private RecordingVendor VendorWith(string summary, string resumeToken)
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(ValidReport(summary), resumeToken);
        return vendor;
    }

    private RunDirectory NewRun(ScoutState? scout = null) =>
        NewRunCore(scout ?? new ScoutState(true, "codex", "model", "effort"));

    private RunDirectory NewRunCore(ScoutState? scout)
    {
        var runId = Guid.NewGuid().ToString("N");
        var run = RunDirectory.Create(_workspace, runId);
        run.WriteState(new RunState(runId, _workspace, "Text", DateTimeOffset.UtcNow, 0, 5, Scout: scout));
        return run;
    }

    private async Task<RunDirectory> NewGitRun()
    {
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

    private static JsonElement ToolSchema(string methodName)
    {
        var services = new ServiceCollection()
            .AddSingleton(SessionRoots.None)
            .AddSingleton(new JobRegistry())
            .AddSingleton(new CatalogCache())
            .BuildServiceProvider();
        var method = typeof(ForgeTools).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!;
        var tool = McpServerTool.Create(method, options: new McpServerToolCreateOptions
        {
            Services = services,
            SerializerOptions = ToolArgumentJson.ArgumentOptions
        });
        return tool.ProtocolTool.InputSchema.Clone();
    }

    private static ScoutReport ValidReport(string summary) => new()
    {
        Summary = summary,
        ConfirmedFacts = [Item("fact", "src/PlanForge/Acts/Scout.cs:42")],
        MaterialAssumptions = [Item("assumption", "src/PlanForge/Acts/Scout.cs#Scout.RunAsync")],
        OpenDecisions = [Item("decision", "https://example.com/reference", "external")],
        LikelyChangeSurface = [Item("surface", "src/PlanForge/Run/RunDirectory.cs:1")],
        VerificationEvidence = [Item("verification", "src/PlanForge/Vendors/Contracts.cs#ScoutReport")],
        DependentsAndPinnedBehaviour = [Item("dependent", "src/PlanForge/Mcp/ForgeTools.cs#ForgeTools.ScoutRun")]
    };

    private static ScoutReport LargeReport(string source)
    {
        var report = ValidReport(new string('s', 2000));
        return new ScoutReport
        {
            Summary = report.Summary,
            ConfirmedFacts = Enumerable.Range(0, 5).Select(_ => Item(new string('x', 1000), source)).ToArray(),
            MaterialAssumptions = report.MaterialAssumptions,
            OpenDecisions = report.OpenDecisions,
            LikelyChangeSurface = report.LikelyChangeSurface,
            VerificationEvidence = report.VerificationEvidence,
            DependentsAndPinnedBehaviour = report.DependentsAndPinnedBehaviour
        };
    }

    private static ScoutItem Item(string text, string source, string kind = "repository") =>
        new() { Text = text, SourceKind = kind, Source = source };

    private static string RepositoryPrompts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var prompts = Path.Combine(directory.FullName, "prompts");
            if (Directory.Exists(prompts) && File.Exists(Path.Combine(prompts, "scout-contract.md"))) return prompts;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("could not locate the prompts folder above the test binary");
    }
}
