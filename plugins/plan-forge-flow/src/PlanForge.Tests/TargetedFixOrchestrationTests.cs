using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using PlanForge.Acts;
using PlanForge.Infrastructure;
using PlanForge.Jobs;
using PlanForge.Mcp;
using PlanForge.Prompts;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class TargetedFixOrchestrationTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-targeted-" + Guid.NewGuid().ToString("n"));
    private const string Plan = "## Gates\n1. **G1.** `Add-Content full.txt full`\n**Fix gate:** `Add-Content short.txt short` (R2)\n";
    private static readonly Selection Builder = new("builder", null);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("targeted")]
    [InlineData("full")]
    [InlineData(null)]
    public async Task Direct_and_background_complete_with_equivalent_host_fields_and_only_selected_gate(string? mode)
    {
        var run = NewRun();
        var vendor = Vendor();
        var direct = await Fix(vendor).FixAsync(run, Builder, null, "A", ["F-0001"], CancellationToken.None, gate: mode);
        Assert.Equal(mode ?? "full", direct.GateMode);
        Assert.Equal(mode == "targeted", direct.PendingFullGateFindingIds is not null);
        Assert.Equal(mode == "targeted", File.Exists(Path.Combine(_workspace, "short.txt")));
        Assert.Equal(mode != "targeted", File.Exists(Path.Combine(_workspace, "full.txt")));
        Assert.Null(run.ReadDecisionLedger().FindFixAttempt("A")!.LastResult!.GateMode);
        Assert.Null(run.ReadDecisionLedger().FindFixAttempt("A")!.LastResult!.PendingFullGateFindingIds);

        var backgroundRun = NewRun();
        var backgroundVendor = Vendor();
        var registry = new JobRegistry();
        var start = await Start(registry, backgroundRun, backgroundVendor, mode);
        var id = JsonNode.Parse(start)!["jobId"]!.GetValue<string>();
        await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, backgroundRun.RunId, id, CancellationToken.None);
        var fetched = JsonNode.Parse(await ForgeTools.FetchWork(registry, SessionRoots.None, _workspace, backgroundRun.RunId,
            id, CancellationToken.None))!;
        var payload = JsonSerializer.Deserialize(fetched["result"]!.GetValue<string>(), ContractJson.Default.BuildResult)!;
        Assert.Equal(direct.Status, payload.Status);
        Assert.Equal(direct.GateMode, payload.GateMode);
        Assert.Equal(direct.Gate!.Outcome, payload.Gate!.Outcome);
        Assert.Equal(direct.Gate.Command, payload.Gate.Command);
        Assert.Equal(direct.PendingFullGateFindingIds, payload.PendingFullGateFindingIds);
        Assert.Single(vendor.Sessions);
        Assert.Single(backgroundVendor.Sessions);
    }

    [Fact]
    public async Task Replay_recomputes_partial_and_complete_host_verification_but_fetch_keeps_snapshot()
    {
        var run = NewRun();
        var vendor = Vendor();
        var registry = new JobRegistry();
        var start = await Start(registry, run, vendor, "targeted", ids: ["F-0001", "F-0002"]);
        var id = JsonNode.Parse(start)!["jobId"]!.GetValue<string>();
        await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, id, CancellationToken.None);
        var before = await ForgeTools.FetchWork(registry, SessionRoots.None, _workspace, run.RunId, id, CancellationToken.None);
        run.WritePlan("no Fix gate any more");
        await Fix(vendor).FixAsync(run, Builder, Close("partial", "F-0001"), null, [], CancellationToken.None);
        var replay = await Fix(vendor).FixAsync(run, Builder, new OrchestratorDecisionBatch("independent",
            [new OrchestratorDecision("F-0003", "defer", "orchestrator", "later")]), "A",
            ["F-0002", "F-0001"], CancellationToken.None, gate: "targeted");
        Assert.Equal(["F-0002"], replay.PendingFullGateFindingIds);
        Assert.Equal(["F-0002"], run.ReadDecisionLedger().Summary.PendingFullGateFindingIds);
        Assert.Equal("A", Assert.Single(run.ReadDecisionLedger().Summary.PendingFullGateAttempts).FixAttemptId);
        var replayStart = await Start(registry, run, vendor, "targeted", ids: ["F-0001", "F-0002"]);
        var replayJob = JsonNode.Parse(replayStart)!["jobId"]!.GetValue<string>();
        await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, replayJob, CancellationToken.None);
        var replayFetch = JsonNode.Parse(await ForgeTools.FetchWork(registry, SessionRoots.None, _workspace, run.RunId,
            replayJob, CancellationToken.None))!;
        var replayPayload = JsonNode.Parse(replayFetch["result"]!.GetValue<string>())!;
        Assert.Equal("[\"F-0002\"]", replayPayload["pendingFullGateFindingIds"]!.ToJsonString());
        await Fix(vendor).FixAsync(run, Builder, Close("rest", "F-0002"), null, [], CancellationToken.None);
        replay = await Fix(vendor).FixAsync(run, Builder, null, "A",
            ["F-0001", "F-0002"], CancellationToken.None, gate: "targeted");
        Assert.Empty(replay.PendingFullGateFindingIds!);
        Assert.Empty(run.ReadDecisionLedger().Summary.PendingFullGateFindingIds);
        var after = await ForgeTools.FetchWork(registry, SessionRoots.None, _workspace, run.RunId, id, CancellationToken.None);
        Assert.Equal(before, after);
        Assert.Single(vendor.Sessions);
        Assert.Single(File.ReadAllLines(Path.Combine(_workspace, "short.txt")));
        Assert.False(File.Exists(Path.Combine(_workspace, "full.txt")));
    }

    [Fact]
    public async Task New_attempts_can_refix_pending_but_full_closes_only_selected_ids()
    {
        var run = NewRun();
        var vendor = Vendor();
        await Fix(vendor).FixAsync(run, Builder, null, "A", ["F-0001", "F-0002"], CancellationToken.None, gate: "targeted");
        vendor.Enqueue(Report());
        var next = await Fix(vendor).FixAsync(run, Builder, null, "B", ["F-0001"], CancellationToken.None, gate: "targeted");
        Assert.Equal(["F-0001"], next.PendingFullGateFindingIds);
        vendor.Enqueue(Report());
        var full = await Fix(vendor).FixAsync(run, Builder, null, "C", ["F-0002"], CancellationToken.None);
        Assert.Equal("full", full.GateMode);
        Assert.Null(full.PendingFullGateFindingIds);
        var summary = run.ReadDecisionLedger().Summary;
        Assert.Equal(["F-0001"], summary.PendingFullGateFindingIds);
        Assert.Equal("B", Assert.Single(summary.PendingFullGateAttempts).FixAttemptId);
        Assert.Contains("F-0003", summary.UnresolvedFindingIds);
        var replay = await Fix(vendor).FixAsync(run, Builder, null, "A", ["F-0001", "F-0002"], CancellationToken.None, gate: "targeted");
        Assert.Empty(replay.PendingFullGateFindingIds!);
        Assert.Equal(3, vendor.Sessions.Count);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(_workspace, "short.txt")).Length);
        Assert.Single(File.ReadAllLines(Path.Combine(_workspace, "full.txt")));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("timeout")]
    [InlineData("not_run")]
    [InlineData("killed")]
    [InlineData("cut_short")]
    public async Task Unsuccessful_attempts_preserve_old_pending_and_retry_under_the_same_binding(string outcome)
    {
        var run = NewRun();
        var ledger = run.ReadDecisionLedger();
        ledger.RecordFixAttempt("old", ["F-0001"], Passed(), true, "targeted");
        run.WritePlan(outcome switch
        {
            "failed" => "## Gates\n1. **G1.** `exit 0`\n**Fix gate:** `exit 7` (R2)",
            "timeout" => "## Gates\n1. **G1.** `exit 0`\n**Fix gate:** `Start-Sleep -Seconds 60` (R2)",
            _ => Plan
        });
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(outcome == "cut_short" ? new OperationCanceledException("host took call") :
            outcome == "not_run" ? Report() with { Status = "blocked", Verification = new Verification("failed", "diagnostic failed") } : Report(),
            killedBackgroundTasks: outcome == "killed" ? ["still running"] : null);
        var fix = new ReviewFix(vendor, Prompts(), outcome == "timeout" ? TimeSpan.FromSeconds(1) : null);
        if (outcome == "cut_short")
            await Assert.ThrowsAsync<TurnCutShortException>(() => fix.FixAsync(run, Builder, null, "A", ["F-0001"],
                CancellationToken.None, gate: "targeted"));
        else
        {
            var result = await fix.FixAsync(run, Builder, null, "A", ["F-0001"], CancellationToken.None, gate: "targeted");
            Assert.Equal(outcome == "killed" ? "not_run" : outcome, result.Gate!.Outcome);
            Assert.Equal("targeted", result.GateMode);
            Assert.Empty(result.PendingFullGateFindingIds!);
        }
        Assert.False(ledger.FindFixAttempt("A")!.Terminal);
        Assert.Equal("old", ledger.Snapshot.Entries[0].PendingFullGateAttemptId);
        run.WritePlan("## Gates\n1. **G1.** `exit 0`\n**Fix gate:** `exit 0` (R2)");
        vendor.Enqueue(Report());
        var retry = await Fix(vendor).FixAsync(run, Builder, null, "A", ["F-0001"], CancellationToken.None, gate: "targeted");
        Assert.Equal("passed", retry.Gate!.Outcome);
        Assert.Equal(["F-0001"], retry.PendingFullGateFindingIds);
        Assert.Equal("A", ledger.Snapshot.Entries[0].PendingFullGateAttemptId);
        Assert.Equal(2, vendor.Sessions.Count);
    }

    [Fact]
    public async Task Cancellation_of_background_fix_keeps_binding_and_last_completed_result()
    {
        var run = NewRun();
        var previous = Passed() with { Status = "gate_failed", Gate = Passed().Gate! with { Outcome = "failed" } };
        run.ReadDecisionLedger().RecordFixAttempt("A", ["F-0001"], previous, false, "targeted");
        var vendor = new WaitingVendor();
        var registry = new JobRegistry();
        var start = await Start(registry, run, vendor, "targeted");
        var id = JsonNode.Parse(start)!["jobId"]!.GetValue<string>();
        await vendor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await ForgeTools.CancelWork(registry, SessionRoots.None, _workspace, run.RunId, id, CancellationToken.None);
        await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, id, CancellationToken.None);
        await registry.CloseAsync();
        var attempt = run.ReadDecisionLedger().FindFixAttempt("A")!;
        Assert.False(attempt.Terminal);
        Assert.Equal("targeted", attempt.GateMode);
        Assert.Equal(previous.Gate, attempt.LastResult!.Gate);
        Assert.Empty(run.ReadDecisionLedger().Summary.PendingFullGateFindingIds);
    }

    [Theory]
    [InlineData("Targeted", true, Plan, "exactly")]
    [InlineData("", true, Plan, "exactly")]
    [InlineData("   ", true, Plan, "exactly")]
    [InlineData("unknown", true, Plan, "exactly")]
    [InlineData("full", false, Plan, "requires")]
    [InlineData("targeted", false, Plan, "requires")]
    [InlineData("targeted", true, "## Gates\n1. **G1.** `exit 0`", "Fix gate")]
    [InlineData("targeted", true, "## Gates\n1. **G1.** `exit 0`\n**Fix gate:** check `file.cs` (R2)", "executable Fix gate")]
    [InlineData("targeted", true, "## Gates\n1. **G1.** `exit 0`\n**Fix gate:** `exit 0` (R2)\n**Fix gate:** `exit 0` (R2)", "duplicate labels")]
    public async Task Real_tool_filter_surfaces_refusals_before_decisions_workers_or_jobs(string mode, bool ids,
                                                                                       string plan, string reason)
    {
        foreach (var background in new[] { false, true })
        {
            var run = NewRun(plan);
            var registry = new JobRegistry();
            var before = File.ReadAllBytes(run.DecisionLedgerPath);
            var state = File.ReadAllBytes(Path.Combine(run.Path, "state.json"));
            var vendor = Vendor();
            var batch = new OrchestratorDecisionBatch("independent", [new OrchestratorDecision("F-0003", "defer", "orchestrator", "later")]);
            var result = await ToolErrors.Surfaced(async (_, ct) =>
            {
                var json = background
                    ? await Start(registry, run, vendor, mode, batch, ids ? ["F-0001"] : [], ids ? "A" : null)
                    : await ForgeTools.ReviewFix(new CatalogCache(), SessionRoots.None, _workspace, run.RunId, "builder", ct,
                        vendor: "codex", decisions: batch, fixAttemptId: ids ? "A" : null,
                        fixFindingIds: ids ? ["F-0001"] : [], gate: mode);
                return new CallToolResult { Content = [new TextContentBlock { Text = json }] };
            })(null!, CancellationToken.None);
            Assert.True(result.IsError);
            Assert.Contains(reason, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllBytes(run.DecisionLedgerPath));
            Assert.Equal(state, File.ReadAllBytes(Path.Combine(run.Path, "state.json")));
            Assert.Equal(plan, run.ReadPlan());
            Assert.Empty(vendor.Sessions);
            Assert.Null(registry.Get(run.Path));
        }
    }

    [Theory]
    [InlineData("build.next")]
    [InlineData("review.code")]
    [InlineData("plan.review")]
    [InlineData("scout")]
    public async Task Explicit_gate_is_refused_for_other_background_acts(string act)
    {
        var run = NewRun();
        var registry = new JobRegistry();
        var error = await Assert.ThrowsAsync<ArgumentRejectedException>(() => ForgeTools.StartWork(registry, SessionRoots.None,
            _workspace, run.RunId, act, "builder", null, "codex", null, null, null, null, false,
            CancellationToken.None, () => Vendor(), gate: "full"));
        Assert.Contains("gate", error.Message, StringComparison.Ordinal);
        Assert.Null(registry.Get(run.Path));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("settled")]
    [InlineData("digest")]
    public async Task Rejected_decisions_are_audited_before_workers_on_both_tool_paths(string rejection)
    {
        foreach (var background in new[] { false, true })
        {
            var run = NewRun();
            var batch = Close("batch", rejection == "missing" ? "F-9999" : "F-0002");
            if (rejection != "missing")
            {
                run.ReadDecisionLedger().Apply(Close("batch", "F-0002"), LedgerPhase.CodeReview);
                batch = rejection == "digest" ? Close("batch", "F-0003") : Close("another", "F-0002");
            }
            var ledger = File.ReadAllBytes(run.DecisionLedgerPath);
            var state = File.ReadAllBytes(Path.Combine(run.Path, "state.json"));
            var registry = new JobRegistry();
            var vendor = Vendor();
            var result = await ToolErrors.Surfaced(async (_, ct) =>
            {
                var json = background
                    ? await Start(registry, run, vendor, null, batch, [], null)
                    : await ForgeTools.ReviewFix(new CatalogCache(), SessionRoots.None, _workspace, run.RunId,
                        "builder", ct, vendor: "codex", decisions: batch);
                return new CallToolResult { Content = [new TextContentBlock { Text = json }] };
            })(null!, CancellationToken.None);
            Assert.True(result.IsError);
            var error = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            var flow = File.ReadAllText(run.FlowLogPath);
            Assert.Contains("review.fix preflight — decisions rejected", flow);
            Assert.Contains("decisionBatchId: " + batch.DecisionBatchId, flow);
            Assert.Contains(rejection == "digest" ? "conflicts" : "F-", error);
            Assert.Equal(ledger, File.ReadAllBytes(run.DecisionLedgerPath));
            Assert.Equal(state, File.ReadAllBytes(Path.Combine(run.Path, "state.json")));
            Assert.Empty(vendor.Sessions);
            Assert.Null(registry.Get(run.Path));
        }
    }

    [Fact]
    public async Task Accepted_and_replayed_direct_decisions_do_not_log_rejection()
    {
        var run = NewRun();
        var batch = Close("batch", "F-0002");
        await ForgeTools.ReviewFix(new CatalogCache(), SessionRoots.None, _workspace, run.RunId,
            "builder", CancellationToken.None, vendor: "codex", decisions: batch);
        var ledger = File.ReadAllBytes(run.DecisionLedgerPath);
        await ForgeTools.ReviewFix(new CatalogCache(), SessionRoots.None, _workspace, run.RunId,
            "builder", CancellationToken.None, vendor: "codex", decisions: batch);
        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.DoesNotContain("decisions rejected", flow);
        Assert.Equal(1, flow.Split("Review fix — decisions applied").Length - 1);
        Assert.Equal(ledger, File.ReadAllBytes(run.DecisionLedgerPath));
    }

    [Fact]
    public async Task Mode_binding_is_checked_before_changed_plan_and_independent_decisions_on_both_paths()
    {
        foreach (var background in new[] { false, true })
        {
            var run = NewRun("missing Fix gate");
            var ledger = run.ReadDecisionLedger();
            ledger.RecordFixAttempt("A", ["F-0001"], Passed(), true, "full");
            var before = File.ReadAllBytes(run.DecisionLedgerPath);
            var batch = Close("independent", "F-0002");
            var error = await Record.ExceptionAsync(() => background
                ? Start(new JobRegistry(), run, Vendor(), "targeted", batch)
                : Fix(Vendor()).FixAsync(run, Builder, batch, "A", ["F-0001"], CancellationToken.None, gate: "targeted"));
            Assert.IsType<DecisionLedgerRequestException>(error);
            Assert.Contains("mode", error!.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, File.ReadAllBytes(run.DecisionLedgerPath));
        }
    }

    [Fact]
    public async Task Legacy_v2_replay_is_full_and_decisions_only_and_build_next_have_no_annotations()
    {
        var run = NewRun();
        var ledger = run.ReadDecisionLedger();
        ledger.RecordFixAttempt("A", ["F-0001"], Passed(), true, "full");
        var json = JsonNode.Parse(AtomicFile.Read(run.DecisionLedgerPath))!;
        json["schemaVersion"] = 2;
        foreach (var attempt in json["fixAttempts"]!.AsArray()) attempt!.AsObject().Remove("gateMode");
        AtomicFile.Write(run.DecisionLedgerPath, json.ToJsonString());
        var replay = await Fix(new RecordingVendor("codex")).FixAsync(run, Builder, null, "A", ["F-0001"], CancellationToken.None);
        Assert.Equal("full", replay.GateMode);
        Assert.Null(replay.PendingFullGateFindingIds);
        var noWork = await Fix(new RecordingVendor("codex")).FixAsync(run, Builder, Close("host", "F-0002"), null, [], CancellationToken.None);
        Assert.Null(noWork.GateMode);
        Assert.Null(noWork.PendingFullGateFindingIds);
        Assert.DoesNotContain("gateMode", JsonSerializer.Serialize(noWork, ContractJson.Default.BuildResult), StringComparison.Ordinal);

        var taskRun = NewRun("## Approach\n1. Task.\n   **Gate:** condition checked\n");
        var build = await new Build(Vendor(), Prompts()).NextAsync(taskRun, Builder, CancellationToken.None);
        Assert.Null(build.Result!.GateMode);
        Assert.Null(build.Result!.PendingFullGateFindingIds);
    }

    [Fact]
    public async Task Cursor_parsed_host_fields_are_stripped_before_task_and_fix_consumers()
    {
        const string wire = """
            {"status":"done","filesChanged":[],"verification":{"outcome":"passed","evidence":"condition checked"},
             "summary":"fixed","gateMode":"targeted","pendingFullGateFindingIds":["F-9999"]}
            """;
        Assert.DoesNotContain("gateMode", Schemas.BuildResult.Json, StringComparison.Ordinal);
        Assert.True(SchemaInPrompt.TryExtract(wire, Schemas.BuildResult, out var parsed, out var error), error);
        Assert.Equal("targeted", parsed.GateMode);
        var vendor = new RecordingVendor("cursor");
        vendor.Enqueue(parsed);
        var fixRun = NewRun("## Gates\n1. **G1.** condition checked\n");
        var fix = await Fix(vendor).FixAsync(fixRun, Builder, null, "A", ["F-0001"], CancellationToken.None);
        Assert.Equal("full", fix.GateMode);
        Assert.Null(fix.PendingFullGateFindingIds);
        Assert.Null(fixRun.ReadDecisionLedger().FindFixAttempt("A")!.LastResult!.GateMode);
        vendor.Enqueue(parsed);
        var taskRun = NewRun("## Approach\n1. Task.\n   **Gate:** condition checked\n");
        var build = await new Build(vendor, Prompts()).NextAsync(taskRun, Builder, CancellationToken.None);
        Assert.Null(build.Result!.GateMode);
        Assert.Null(build.Result!.PendingFullGateFindingIds);
    }

    private RunDirectory NewRun(string plan = Plan)
    {
        var run = RunDirectory.Create(_workspace, Guid.NewGuid().ToString("n"));
        run.WritePlan(plan);
        run.WriteState(new RunState(run.RunId, _workspace, "Text", DateTimeOffset.Now, 0, 5, Approved: true, CodeReviewRounds: 1));
        run.ReadDecisionLedger().AddFindings([new Finding("major", "one.cs", "first rule"),
            new Finding("major", "two.cs", "second rule"), new Finding("minor", "three.cs", "independent")], LedgerPhase.CodeReview);
        return run;
    }

    private Task<string> Start(JobRegistry registry, RunDirectory run, IVendor vendor, string? mode,
                               OrchestratorDecisionBatch? decisions = null, string[]? ids = null, string? attempt = "A") =>
        ForgeTools.StartWork(registry, SessionRoots.None, _workspace, run.RunId, "review.fix", "builder", null, "codex",
            null, null, null, null, false, null, null, CancellationToken.None, _ => vendor, Prompts(),
            decisions, attempt, ids ?? ["F-0001"], gate: mode);

    private static ReviewFix Fix(IVendor vendor) => new(vendor, Prompts());
    private static BuildResult Report() => new("done", [], new Verification("unavailable", "gate belongs to server"), "fixed");
    private static BuildResult Passed() => Report() with { Gate = new GateRun("passed", "Fix gate", "exit 0", 0, "ok", 0.01, null) };
    private static OrchestratorDecisionBatch Close(string batch, string id) => new(batch,
        [new OrchestratorDecision(id, "hostVerified", "orchestrator", "verified", "all full gates passed; attempt A")]);
    private static RecordingVendor Vendor()
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(Report());
        return vendor;
    }

    private static PromptLibrary Prompts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "prompts"))) directory = directory.Parent;
        return new PromptLibrary(Path.Combine(directory!.FullName, "prompts"));
    }

    private sealed class WaitingVendor : IVendor
    {
        public string Id => "codex";
        public VendorCatalog Catalog { get; } = new([], CatalogSource.Resolved);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<VendorReadiness> ProbeAsync(CancellationToken ct) => Task.FromResult(new VendorReadiness(true, "test"));
        public Task<IVendorSession> StartAsync(RoleSpec role, Selection selection, string? resumeToken, CancellationToken ct) =>
            Task.FromResult<IVendorSession>(new WaitingSession(Entered));
    }

    private sealed class WaitingSession(TaskCompletionSource entered) : IVendorSession
    {
        public IAsyncEnumerable<VendorEvent> Events => EmptyEvents();
        public bool CanResume => false;
        public string? ResumeToken => null;
        public async Task<T> RunAsync<T>(string prompt, VendorSchema<T> schema, CancellationToken ct)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private static async IAsyncEnumerable<VendorEvent> EmptyEvents() { yield break; }
    }
}
