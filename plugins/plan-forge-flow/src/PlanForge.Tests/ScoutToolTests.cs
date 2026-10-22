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

    /// <summary>
    /// A Scout question through start → poll → fetch lands exactly as a direct call's does: the
    /// numbered entry, the Flow-log outcome, the anchor and <c>documents.scout</c>. See docs/adr/0044.
    /// </summary>
    [Fact]
    public async Task A_scout_job_runs_end_to_end_as_the_direct_route_does()
    {
        var direct = NewRun();
        var background = NewRun();
        var registry = new JobRegistry();

        await Direct(direct, VendorWith("answer", "session"));
        var start = await StartScout(registry, background, VendorWith("answer", "session"));
        var fetch = await Finish(registry, background, Id(start));

        Assert.True(start["started"]!.GetValue<bool>());
        Assert.Equal("scout", start["act"]!.GetValue<string>());
        Assert.Equal("succeeded", fetch["state"]!.GetValue<string>());
        Assert.Equal("answer", fetch["result"]!["summary"]!.GetValue<string>());
        Assert.Equal(background.ScoutAnswersPath, fetch["documents"]!["scout"]!["path"]!.GetValue<string>());
        Assert.Equal(AtomicFile.Read(direct.ScoutAnswersPath), AtomicFile.Read(background.ScoutAnswersPath));
        Assert.Equal(AtomicFile.Read(direct.FlowLogPath), AtomicFile.Read(background.FlowLogPath));
        Assert.Equal(direct.ReadState().Scout, background.ReadState().Scout);
        Assert.Equal(1, background.ReadState().ScoutAnswers);
    }

    [Fact]
    public async Task A_scout_job_result_is_the_complete_report()
    {
        var run = NewRun();
        var registry = new JobRegistry();
        var source = new string('a', 397) + ":42";
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(LargeReport(source), "session");

        var fetch = await Finish(registry, run, Id(await StartScout(registry, run, vendor)));

        using var report = JsonDocument.Parse(fetch["result"]!.ToJsonString());
        var root = report.RootElement;
        var facts = root.GetProperty("confirmedFacts").EnumerateArray().ToArray();
        Assert.Equal(2000, root.GetProperty("summary").GetString()!.Length);
        Assert.Equal(5, facts.Length);
        Assert.All(facts, fact =>
        {
            Assert.Equal(1000, fact.GetProperty("text").GetString()!.Length);
            Assert.Equal(source, fact.GetProperty("source").GetString());
        });
        Assert.False(root.TryGetProperty("truncated", out _));
        Assert.False(root.TryGetProperty("scout", out _));
    }

    [Fact]
    public async Task A_failed_scout_job_fetch_omits_scout_metadata_and_carries_the_safe_failure()
    {
        var run = NewRun();
        var registry = new JobRegistry();
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new VendorException("vendor stderr", 7), "failed-session");

        var fetch = await Finish(registry, run, Id(await StartScout(registry, run, vendor)));

        Assert.Equal("failed", fetch["state"]!.GetValue<string>());
        Assert.Null(fetch["documents"]!["scout"]);
        Assert.Contains("vendor_failed", fetch["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("vendor stderr", fetch.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("vendor_failed", run.ReadState().Scout!.LastFailure!.Code);
    }

    [Fact]
    public async Task A_cancelled_scout_job_leaves_no_scout_failure()
    {
        var run = NewRun();
        var registry = new JobRegistry();
        var vendor = new GatedScoutVendor("never", "cancel-session");
        var jobId = Id(await StartScout(registry, run, vendor));
        await vendor.Holding.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await ForgeTools.CancelWork(registry, SessionRoots.None, _workspace, run.RunId, jobId, CancellationToken.None);
        var fetch = await Finish(registry, run, jobId);

        Assert.Equal("job was cancelled", fetch["error"]!.GetValue<string>());
        Assert.Null(run.ReadState().Scout!.LastFailure);
        Assert.Contains("scout.cancelled", AtomicFile.Read(run.DiagnosticLogPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing", "question", "fresh")]
    [InlineData("disabled", "question", "fresh")]
    [InlineData("selected", " ", "fresh")]
    [InlineData("selected", "overlong", "fresh")]
    [InlineData("selected", "question", null)]
    [InlineData("selected", "question", "resume")]
    public async Task A_scout_job_that_cannot_run_is_refused_before_a_job_or_vendor(string selection,
                                                                                   string question,
                                                                                   string? sessionMode)
    {
        var run = NewRunCore(selection switch
        {
            "missing" => null,
            "disabled" => new ScoutState(false),
            _ => new ScoutState(true, "codex", "model", "effort")
        });
        var registry = new JobRegistry();
        var starts = 0;

        await Assert.ThrowsAsync<ArgumentRejectedException>(() => ForgeTools.StartWork(registry, SessionRoots.None,
            _workspace, run.RunId, "scout", null, null, null, CancellationToken.None, _ =>
            {
                starts++;
                return VendorWith("unused", "session");
            }, _prompts,
            question: question == "overlong" ? new string('q', Scout.MaxQuestionLength + 1) : question,
            sessionMode: sessionMode));

        Assert.Equal(0, starts);
        JobAssert.NoJob(registry, run.Path);
    }

    /// <summary>Scout runs on its persisted selection, so a job takes none of the Builder's arguments, and a Builder act takes none of Scout's.</summary>
    [Fact]
    public async Task A_scout_job_and_a_builder_job_take_only_their_own_arguments()
    {
        var run = NewRun();
        var registry = new JobRegistry();
        var starts = 0;
        Func<string, IVendor> factory = _ =>
        {
            starts++;
            return VendorWith("unused", "session");
        };
        Task<string> StartWith(string? model = null, string? effort = null, string? vendor = null, bool fast = false,
                               string? note = null, string[]? fixFindingIds = null, string? gate = null) =>
            ForgeTools.StartWork(registry, SessionRoots.None, _workspace, run.RunId, "scout", model, effort, vendor,
                                 CancellationToken.None, factory, _prompts, fixFindingIds: fixFindingIds, fast: fast,
                                 note: note, gate: gate, question: "question", sessionMode: "fresh");

        await Assert.ThrowsAsync<ArgumentRejectedException>(() => StartWith(model: "model"));
        await Assert.ThrowsAsync<ArgumentRejectedException>(() => StartWith(effort: "high"));
        await Assert.ThrowsAsync<ArgumentRejectedException>(() => StartWith(vendor: "codex"));
        await Assert.ThrowsAsync<ArgumentRejectedException>(() => StartWith(fast: true));
        await Assert.ThrowsAsync<ArgumentRejectedException>(() => StartWith(note: "note"));
        await Assert.ThrowsAsync<ArgumentRejectedException>(() => StartWith(fixFindingIds: ["F-0001"]));
        await Assert.ThrowsAsync<ArgumentRejectedException>(() => StartWith(gate: "full"));
        await Assert.ThrowsAsync<ArgumentRejectedException>(() => ForgeTools.StartWork(registry, SessionRoots.None,
            _workspace, run.RunId, "build.next", "builder", null, null, CancellationToken.None, factory, _prompts,
            question: "question"));

        Assert.Equal(0, starts);
        JobAssert.NoJob(registry, run.Path);
    }

    /// <summary>
    /// The split Impact pass: fresh questions run side by side, both answers land, numbered in the
    /// order they finished, and no anchor survives them — none of their sessions is the one
    /// investigation a later continue would mean.
    /// </summary>
    [Fact]
    public async Task Two_fresh_jobs_run_side_by_side_both_answers_land_and_the_anchor_is_cleared()
    {
        var run = NewRun(new ScoutState(true, "codex", "model", "effort", "old-session"));
        var registry = new JobRegistry();
        var first = new GatedScoutVendor("first", "first-session");
        var second = new GatedScoutVendor("second", "second-session");

        var firstStart = await StartScout(registry, run, first, question: "first area");
        var secondStart = await StartScout(registry, run, second, question: "second area");
        await Task.WhenAll(first.Holding.Task, second.Holding.Task).WaitAsync(TimeSpan.FromSeconds(30));
        second.Release();
        var secondFetch = await Finish(registry, run, Id(secondStart));
        first.Release();
        var firstFetch = await Finish(registry, run, Id(firstStart));

        Assert.True(secondStart["started"]!.GetValue<bool>());
        Assert.Equal("succeeded", firstFetch["state"]!.GetValue<string>());
        Assert.Equal("succeeded", secondFetch["state"]!.GetValue<string>());
        Assert.Equal([(1, "second area"), (2, "first area")],
                     run.ReadScoutAnswers().Select(entry => (entry.Number, entry.Question)));
        Assert.Equal(2, run.ReadState().ScoutAnswers);
        Assert.Null(run.ReadState().Scout!.SessionId);
    }

    [Fact]
    public async Task A_continue_while_a_scout_job_runs_does_not_start_and_names_the_running_jobs()
    {
        var run = NewRun(new ScoutState(true, "codex", "model", "effort", "old-session"));
        var registry = new JobRegistry();
        var running = new GatedScoutVendor("running", "running-session");
        var waiting = new GatedScoutVendor("waiting", "waiting-session");
        var start = await StartScout(registry, run, running);
        await running.Holding.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var refused = await StartScout(registry, run, waiting, mode: "continue");

        Assert.False(refused["started"]!.GetValue<bool>());
        Assert.Equal(Id(start), refused["jobId"]!.GetValue<string>());
        Assert.Equal([Id(start)], refused["activeJobIds"]!.AsArray().Select(id => id!.GetValue<string>()));
        running.Release();
        await Finish(registry, run, Id(start));
        Assert.False(waiting.Holding.Task.IsCompleted);
        Assert.Single(run.ReadScoutAnswers());
    }

    [Fact]
    public async Task A_continue_job_runs_alone_and_keeps_its_anchor()
    {
        var run = NewRun(new ScoutState(true, "codex", "model", "effort", "old-session"));
        var registry = new JobRegistry();
        var continued = new GatedScoutVendor("continued", "old-session");
        var start = await StartScout(registry, run, continued, mode: "continue");
        await continued.Holding.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var fresh = await StartScout(registry, run, new GatedScoutVendor("unused", "unused"));
        continued.Release();
        await Finish(registry, run, Id(start));

        Assert.False(fresh["started"]!.GetValue<bool>());
        Assert.Equal([Id(start)], fresh["activeJobIds"]!.AsArray().Select(id => id!.GetValue<string>()));
        Assert.Equal("old-session", continued.StartedWithResumeToken);
        Assert.Equal("old-session", run.ReadState().Scout!.SessionId);
    }

    /// <summary>A direct Scout call follows the job rules: a continue waits for the jobs, and a fresh question beside one leaves no anchor.</summary>
    [Fact]
    public async Task A_direct_call_beside_a_scout_job_follows_the_job_rules()
    {
        var run = NewRun(new ScoutState(true, "codex", "model", "effort", "old-session"));
        var registry = new JobRegistry();
        var job = new GatedScoutVendor("job", "job-session");
        var start = await StartScout(registry, run, job);
        await job.Holding.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var refusal = await Assert.ThrowsAsync<ArgumentRejectedException>(() => ForgeTools.ScoutRun(SessionRoots.None,
            _workspace, run.RunId, "follow-up", "continue", CancellationToken.None, _ => VendorWith("unused", "unused"),
            _prompts, registry));
        await ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "beside", "fresh", CancellationToken.None,
                                  _ => VendorWith("direct", "direct-session"), _prompts, registry);
        var anchorBesideTheJob = run.ReadState().Scout!.SessionId;
        job.Release();
        await Finish(registry, run, Id(start));

        Assert.Contains(Id(start), refusal.Message, StringComparison.Ordinal);
        Assert.Null(anchorBesideTheJob);
        Assert.Null(run.ReadState().Scout!.SessionId);
        Assert.Equal(2, run.ReadScoutAnswers().Count);
    }

    /// <summary>
    /// A Scout turn reads the state when it starts and writes it when it ends, minutes later. What the
    /// Orchestrator wrote in between survives — here the user's instructions and a new Scout choice,
    /// which keeps the session it chose rather than taking the old choice's.
    /// </summary>
    [Fact]
    public async Task What_the_orchestrator_writes_while_a_scout_job_runs_survives_the_job()
    {
        var run = NewRun();
        var registry = new JobRegistry();
        var job = new GatedScoutVendor("job", "job-session");
        var start = await StartScout(registry, run, job);
        await job.Holding.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var reselected = new ScoutState(true, "codex", "other-model", "effort");
        run.WriteState(run.ReadState() with { CriticInstructions = "answer in Russian", Scout = reselected });
        job.Release();
        await Finish(registry, run, Id(start));

        var state = run.ReadState();
        Assert.Equal("answer in Russian", state.CriticInstructions);
        Assert.Equal(reselected, state.Scout);
        Assert.Equal(1, state.ScoutAnswers);
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

    private async Task<JsonNode> StartScout(JobRegistry registry,
                                            RunDirectory run,
                                            IVendor vendor,
                                            string mode = "fresh",
                                            string question = "bounded question") =>
        JsonNode.Parse(await ForgeTools.StartWork(registry, SessionRoots.None, _workspace, run.RunId, "scout", null, null,
                                                  null, CancellationToken.None, _ => vendor, _prompts,
                                                  question: question, sessionMode: mode))!;

    /// <summary>Polls the job until it stops, then fetches it, as the skill does.</summary>
    /// <param name="registry">The registry the job runs in.</param>
    /// <param name="run">The Run the job belongs to.</param>
    /// <param name="jobId">The job to finish.</param>
    private async Task<JsonNode> Finish(JobRegistry registry, RunDirectory run, string jobId)
    {
        var poll = JsonNode.Parse(await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, jobId,
                                                            TimeSpan.FromSeconds(30), CancellationToken.None))!;
        Assert.NotEqual("running", poll["state"]!.GetValue<string>());
        return JsonNode.Parse(await ForgeTools.FetchWork(registry, SessionRoots.None, _workspace, run.RunId, jobId,
                                                         CancellationToken.None))!;
    }

    private static string Id(JsonNode start) => start["jobId"]!.GetValue<string>();

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

    /// <summary>
    /// A Scout vendor whose one turn holds until released, so a test can act while a job runs. One per
    /// job: the recording vendor is not made to be started from two threads at once.
    /// </summary>
    /// <param name="summary">The summary of the report the turn answers with.</param>
    /// <param name="token">The session's resume token.</param>
    private sealed class GatedScoutVendor(string summary, string token) : IVendor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "codex";
        public VendorCatalog Catalog { get; } = new([], CatalogSource.Live);

        /// <summary>Completes once the turn runs and holds.</summary>
        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? StartedWithResumeToken { get; private set; }

        public void Release() => _release.TrySetResult();

        public Task<VendorReadiness> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new VendorReadiness(true, "gated"));

        public Task<IVendorSession> StartAsync(RoleSpec role, Selection selection, string? resumeToken, CancellationToken ct)
        {
            StartedWithResumeToken = resumeToken;
            return Task.FromResult<IVendorSession>(new GatedSession(this, token));
        }

        private async Task<ScoutReport> TurnAsync(CancellationToken ct)
        {
            Holding.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return ValidReport(summary);
        }

        private sealed class GatedSession(GatedScoutVendor vendor, string token) : IVendorSession
        {
            public IAsyncEnumerable<VendorEvent> Events => Empty();
            public bool CanResume => true;
            public string? ResumeToken => token;

            public async Task<T> RunAsync<T>(string prompt, VendorSchema<T> schema, CancellationToken ct) =>
                (T)(object)await vendor.TurnAsync(ct);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            private static async IAsyncEnumerable<VendorEvent> Empty() { yield break; }
        }
    }
}
