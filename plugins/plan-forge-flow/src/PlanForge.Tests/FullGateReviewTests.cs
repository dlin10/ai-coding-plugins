using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
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

public sealed class FullGateReviewTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-timing-" + Guid.NewGuid().ToString("n"));
    private const string Plan = "# Brief\n## Approach\n1. Task.\n## Gates\n1. **G1.** `exit 0`\n";
    private static readonly string?[] Timings = [null, "beforeNextRound", "final", "", " ", "Final", "unknown"];
    private static readonly Selection Critic = new("critic", null);
    private string _head = "";

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Confirm_timing_matrix_covers_196_cells_with_saved_refusal_first()
    {
        await InitializeGit();
        var cells = 0;
        foreach (var saved in Timings)
        foreach (var input in Timings)
        foreach (var approved in new[] { false, true })
        foreach (var pending in new[] { false, true })
        {
            var run = NewRun(pending);
            SetSavedTiming(run, saved);
            var before = Snapshot(run);
            var error = await Record.ExceptionAsync(() => Confirm(run, approved, input));
            if (!Valid(saved) || !Valid(input))
            {
                Assert.IsType<ArgumentRejectedException>(error);
                Assert.Contains(!Valid(saved) ? "saved fullGate" : "fullGate must", error.Message);
                AssertSnapshot(before, run);
            }
            else if (!approved)
            {
                Assert.Null(error);
                AssertSnapshot(before, run);
                Assert.True(run.ReadState().Approved);
            }
            else
            {
                Assert.Null(error);
                Assert.Equal(input ?? saved ?? "beforeNextRound", run.ReadState().FullGate);
                Assert.Null(run.ReadState().PendingGateFailure);
                Assert.Equal(pending ? 2 : 0, run.ReadDecisionLedger().Summary.PendingFullGateFindingIds.Count);
                Assert.Equal(before.Ledger, File.ReadAllBytes(run.DecisionLedgerPath));
            }
            cells++;
        }
        Assert.Equal(196, cells);
    }

    [Fact]
    public async Task Confirm_settings_matrix_covers_72_cells_and_reset_preserves_attempts()
    {
        await InitializeGit();
        var cells = 0;
        foreach (var timing in new[] { "beforeNextRound", "final" })
        foreach (var approved in new[] { false, true })
        foreach (var pending in new[] { false, true })
        foreach (var environment in new[] { "omitted", "replace", "empty" })
        foreach (var roots in new[] { "omitted", "replace", "empty" })
        {
            var run = NewRun(pending);
            var oldRoot = Path.Combine(_workspace, "old");
            var newRoot = Path.Combine(_workspace, "new");
            run.WriteState(run.ReadState() with { FullGate = timing,
                GateEnvironment = new Dictionary<string, string> { ["TEST"] = "old" }, BuilderRoots = [oldRoot] });
            var before = Snapshot(run);
            Dictionary<string, string>? env = environment == "omitted" ? null
                : environment == "empty" ? [] : new() { ["TEST"] = "new" };
            string[]? paths = roots == "omitted" ? null : roots == "empty" ? [] : [newRoot];
            await ForgeTools.ConfirmPlan(SessionRoots.None, _workspace, run.RunId, Plan, approved,
                CancellationToken.None, env, paths);
            var state = run.ReadState();
            Assert.Equal(timing, state.FullGate);
            Assert.Equal(before.Ledger, File.ReadAllBytes(run.DecisionLedgerPath));
            if (!approved) AssertSnapshot(before, run);
            else
            {
                Assert.Null(state.PendingGateFailure);
                Assert.Equal(environment == "empty" ? null : environment == "replace" ? "new" : "old",
                    state.GateEnvironment?.GetValueOrDefault("TEST"));
                Assert.Equal(roots == "empty" ? null : roots == "replace" ? newRoot : oldRoot,
                    state.BuilderRoots?.Single());
            }
            cells++;
        }
        Assert.Equal(72, cells);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("\"Final\"")]
    [InlineData("\"unknown\"")]
    public async Task Corrupt_saved_timing_surfaces_through_status_and_confirm_without_rewrite(string value)
    {
        var run = NewRun(true);
        var node = JsonNode.Parse(File.ReadAllText(StatePath(run)))!;
        node["fullGate"] = JsonNode.Parse(value);
        AtomicFile.Write(StatePath(run), node.ToJsonString());
        var before = Snapshot(run);
        foreach (var read in new Func<Task<string>>[] {
            () => ForgeTools.Status(SessionRoots.None, _workspace, run.RunId, CancellationToken.None),
            () => Confirm(run, false, "bad", new OrchestratorDecisionBatch("invalid", [])),
            () => new WorkAct(new RecordingVendor("codex"), Prompts()).RunAsync("review.fix", run,
                null, Critic, null, null, null, false, CancellationToken.None, gate: "bad") })
        {
            await AssertFiltered(read, "saved fullGate");
            AssertSnapshot(before, run);
        }
        Assert.Throws<ArgumentRejectedException>(() => run.ReadState());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Final")]
    [InlineData("unknown")]
    public async Task Invalid_input_is_filtered_before_decisions_even_when_refused(string timing)
    {
        var run = NewRun(true);
        var before = Snapshot(run);
        foreach (var approved in new[] { false, true })
        {
            await AssertFiltered(() => Confirm(run, approved, timing,
                new OrchestratorDecisionBatch("must-not-apply", [], Raises:
                    [new OrchestratorRaise("major", "plan", "rule", "orchestrator", "reason")])), "fullGate must");
            AssertSnapshot(before, run);
        }
    }

    [Fact]
    public async Task Legacy_read_does_not_write_and_next_confirm_persists_default()
    {
        await InitializeGit();
        var run = NewRun(false);
        SetSavedTiming(run, null);
        var before = Snapshot(run);
        Assert.Equal("beforeNextRound", run.ReadState().FullGate);
        var status = JsonNode.Parse(await ForgeTools.Status(SessionRoots.None, _workspace, run.RunId, CancellationToken.None))!;
        Assert.Equal("beforeNextRound", status["run"]!["fullGate"]!.GetValue<string>());
        AssertSnapshot(before, run);
        await Confirm(run, true, null);
        Assert.Equal("beforeNextRound", JsonNode.Parse(File.ReadAllText(StatePath(run)))!["fullGate"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Pending_guard_precedes_cap_grant_empty_diff_and_background_job(bool grant, bool empty)
    {
        var run = NewRun(true);
        run.WriteState(run.ReadState() with { CodeReviewRounds = 3 });
        var before = Snapshot(run);
        var vendor = new RecordingVendor("codex");
        var git = new ReviewGit(empty);
        await AssertFiltered(async () => JsonSerializer.Serialize(await new CodeReview(vendor, Prompts(), git)
            .ReviewAsync(run, Critic, grant, CancellationToken.None), ContractJson.Default.Critique), "pending full host verification");
        await AssertFiltered(() => ForgeTools.ReviewCode(new CatalogCache(), SessionRoots.None, _workspace,
            run.RunId, "critic", CancellationToken.None, vendor: "codex", userGrantedRound: grant), "pending full host verification");
        var registry = new JobRegistry();
        await AssertFiltered(() => StartReview(registry, run, vendor, grant), "pending full host verification");
        AssertSnapshot(before, run);
        Assert.Empty(vendor.Sessions);
        Assert.Equal(0, git.Reads);
        Assert.Null(registry.Get(run.Path));
    }

    [Theory]
    [InlineData("beforeNextRound", false, false)]
    [InlineData("beforeNextRound", false, true)]
    [InlineData("final", false, false)]
    [InlineData("final", false, true)]
    [InlineData("final", true, false)]
    [InlineData("final", true, true)]
    public async Task Review_annotations_and_assessments_preserve_pending(string timing, bool pending, bool empty)
    {
        var run = NewRun(pending);
        run.WriteState(run.ReadState() with { FullGate = timing });
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new VendorCritique { Verdict = "approve", Summary = "approved", Findings = [], Reopenings = [],
            UnresolvedAssessments = pending ? [new("F-0001", false, "fixed"), new("F-0002", false, "fixed")] : [] });
        var result = await new CodeReview(vendor, Prompts(), new ReviewGit(empty))
            .ReviewAsync(run, Critic, false, CancellationToken.None);
        Assert.Equal("approve", result.Verdict);
        var json = JsonNode.Parse(JsonSerializer.Serialize(result, ContractJson.Default.Critique))!.AsObject();
        Assert.Equal(timing == "final", json.ContainsKey("pendingFullGateFindingIds"));
        if (timing == "final") Assert.Equal(pending ? ["F-0001", "F-0002"] : [], result.PendingFullGateFindingIds);
        Assert.Equal(pending ? 2 : 0, run.ReadDecisionLedger().Summary.PendingFullGateFindingIds.Count);
        Assert.Contains("fullGate: " + timing, File.ReadAllText(run.FlowLogPath));
        if (pending)
        {
            Assert.Contains("pendingFullGateAttempt: A", File.ReadAllText(run.FlowLogPath));
            if (!empty)
            {
                Assert.Contains("Full host verification timing: final", vendor.Sessions.Single().PromptText);
                Assert.Contains("pending full host verification: targeted attempt A", vendor.Sessions.Single().PromptText);
            }
            run.ReadDecisionLedger().Apply(new OrchestratorDecisionBatch("partial",
                [new("F-0001", "hostVerified", "orchestrator", "full checks passed", "G1 passed covering attempt A")]), LedgerPhase.CodeReview);
            Assert.Equal(["F-0002"], run.ReadDecisionLedger().Summary.PendingFullGateFindingIds);
        }
        Assert.DoesNotContain("pendingFullGateFindingIds", JsonSerializer.Serialize(new Critique("approve", [], "plan"), ContractJson.Default.Critique));
    }

    [Fact]
    public async Task Final_background_review_matches_direct_and_fetch_retains_pending_snapshot()
    {
        await InitializeGit();
        await File.WriteAllTextAsync(Path.Combine(_workspace, "tracked.txt"), "changed");
        var run = NewRun(true);
        run.WriteState(run.ReadState() with { FullGate = "final" });
        static RecordingVendor Vendor()
        {
            var vendor = new RecordingVendor("codex");
            vendor.Enqueue(new VendorCritique { Verdict = "approve", Summary = "approved", Findings = [], Reopenings = [],
                UnresolvedAssessments = [new("F-0001", false, "fixed"), new("F-0002", false, "fixed")] });
            return vendor;
        }
        var direct = await new CodeReview(Vendor(), Prompts(), new GitClient(_workspace))
            .ReviewAsync(run, Critic, false, CancellationToken.None);
        var registry = new JobRegistry();
        var start = JsonNode.Parse(await StartReview(registry, run, Vendor(), false))!;
        var id = start["jobId"]!.GetValue<string>();
        await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, id, CancellationToken.None);
        var fetch = await ForgeTools.FetchWork(registry, SessionRoots.None, _workspace, run.RunId, id, CancellationToken.None);
        var payload = JsonNode.Parse(fetch)!["result"]!.GetValue<string>();
        var background = JsonSerializer.Deserialize(payload, ContractJson.Default.Critique)!;
        Assert.Equal(JsonSerializer.Serialize(direct, ContractJson.Default.Critique),
            JsonSerializer.Serialize(background, ContractJson.Default.Critique));
        run.ReadDecisionLedger().Apply(new OrchestratorDecisionBatch("partial", [new("F-0001", "hostVerified",
            "orchestrator", "verified", "G1 passed covering A")]), LedgerPhase.CodeReview);
        Assert.Equal(fetch, await ForgeTools.FetchWork(registry, SessionRoots.None, _workspace, run.RunId, id, CancellationToken.None));
        var status = JsonNode.Parse(await ForgeTools.Status(SessionRoots.None, _workspace, run.RunId, CancellationToken.None))!;
        Assert.Equal("[\"F-0002\"]", status["ledger"]!["pendingFullGateFindingIds"]!.ToJsonString());
        var current = await new CodeReview(new RecordingVendor("codex"), Prompts(), new ReviewGit(true))
            .ReviewAsync(run, Critic, false, CancellationToken.None);
        Assert.Equal(["F-0002"], current.PendingFullGateFindingIds);
    }

    [Fact]
    public async Task Plan_review_omits_pending_annotation_with_saved_final_timing()
    {
        var run = NewRun(true);
        run.WriteState(run.ReadState() with { FullGate = "final" });
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new VendorCritique { Verdict = "approve", Summary = "plan", Findings = [],
            Reopenings = [], UnresolvedAssessments = [] });
        var result = await new PlanReview(vendor, Prompts()).ReviewAsync(run, null, Critic, null, null,
            false, CancellationToken.None);
        Assert.DoesNotContain("pendingFullGateFindingIds", JsonSerializer.Serialize(result, ContractJson.Default.Critique));
        Assert.Equal(2, run.ReadDecisionLedger().Summary.PendingFullGateFindingIds.Count);
    }

    [Fact]
    public async Task Execution_rechecks_after_successful_admission()
    {
        var run = NewRun(false);
        var vendor = new RecordingVendor("codex");
        var registry = new JobRegistry();
        var start = await ForgeTools.StartWork(registry, SessionRoots.None, _workspace, run.RunId, "review.code",
            "critic", null, "codex", null, null, null, null, false, null, null, CancellationToken.None,
            _ => { AddPending(run); return vendor; }, Prompts());
        var id = JsonNode.Parse(start)!["jobId"]!.GetValue<string>();
        await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, id, CancellationToken.None);
        var fetch = JsonNode.Parse(await ForgeTools.FetchWork(registry, SessionRoots.None, _workspace, run.RunId, id, CancellationToken.None))!;
        Assert.Equal("failed", fetch["state"]!.GetValue<string>());
        Assert.Contains("pending full host verification", fetch["error"]!.GetValue<string>());
        Assert.Empty(vendor.Sessions);
        Assert.Equal(0, run.ReadState().CodeReviewRounds);
    }

    [Fact]
    public async Task Reconfirm_switches_timing_without_changing_plan_or_pending_and_refusal_validates_settings()
    {
        await InitializeGit();
        var run = NewRun(true);
        await Confirm(run, true, "final");
        await Confirm(run, true, null);
        Assert.Equal("final", run.ReadState().FullGate);
        var before = Snapshot(run);
        await Assert.ThrowsAsync<ArgumentRejectedException>(() => ForgeTools.ConfirmPlan(SessionRoots.None, _workspace,
            run.RunId, "different", false, CancellationToken.None, builderRoots: ["relative"]));
        AssertSnapshot(before, run);
        await Confirm(run, true, "beforeNextRound");
        Assert.Equal(before.Plan, File.ReadAllBytes(run.PlanPath));
        Assert.Equal(before.Ledger, File.ReadAllBytes(run.DecisionLedgerPath));
        Assert.Throws<ArgumentRejectedException>(() => CodeReview.RequireReady(run, run.ReadState()));
    }

    [Theory]
    [InlineData("beforeNextRound")]
    [InlineData("final")]
    public async Task Empty_diffs_do_not_claim_the_next_real_round_in_flow(string timing)
    {
        var run = NewRun(false);
        run.WriteState(run.ReadState() with { FullGate = timing });
        var vendor = new RecordingVendor("codex");
        var emptyReview = new CodeReview(vendor, Prompts(), new ReviewGit(true));
        await emptyReview.ReviewAsync(run, Critic, false, CancellationToken.None);
        await emptyReview.ReviewAsync(run, Critic, false, CancellationToken.None);
        Assert.Equal(0, run.ReadState().CodeReviewRounds);
        Assert.Empty(vendor.Sessions);
        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Equal(2, flow.Split("empty diff (no round consumed)").Length - 1);
        Assert.DoesNotContain("— round", flow);
        vendor.Enqueue(new VendorCritique { Verdict = "approve", Summary = "approved", Findings = [],
            Reopenings = [], UnresolvedAssessments = [] });
        await new CodeReview(vendor, Prompts(), new ReviewGit(false))
            .ReviewAsync(run, Critic, false, CancellationToken.None);
        Assert.Equal(1, run.ReadState().CodeReviewRounds);
        Assert.Equal(1, File.ReadAllText(run.FlowLogPath).Split("Code review — round 1").Length - 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("final")]
    [InlineData("beforeNextRound")]
    public async Task Plan_confirmation_decisions_omit_code_verification_context(string? timing)
    {
        await InitializeGit();
        var run = NewRun(true);
        var ledger = run.ReadDecisionLedger();
        var finding = ledger.AddFinding(new Finding("minor", "plan", "plan rule"), LedgerPhase.PlanReview);
        var batch = new OrchestratorDecisionBatch("confirm",
            [new(finding.FindingId, "defer", "orchestrator", "later")]);
        await Confirm(run, true, timing, batch);
        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("Plan confirmation — decisions applied", flow);
        Assert.DoesNotContain("fullGate:", flow);
        Assert.DoesNotContain("pendingFullGate", flow);
        Assert.Equal(timing ?? "beforeNextRound", run.ReadState().FullGate);
        await Confirm(run, true, null, batch);
        Assert.Equal(flow, File.ReadAllText(run.FlowLogPath));

        var codeBatch = new OrchestratorDecisionBatch("code",
            [new("F-0001", "hostVerified", "orchestrator", "verified", "full checks covering A")]);
        var response = ledger.Apply(codeBatch, LedgerPhase.CodeReview);
        run.AppendFlowDecisionBatch("Code decisions", codeBatch, response, LedgerPhase.CodeReview);
        flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("fullGate: " + (timing ?? "beforeNextRound"), flow);
        Assert.Contains("pendingFullGateFindingIds: F-0002", flow);
    }

    private RunDirectory NewRun(bool pending)
    {
        var run = RunDirectory.Create(_workspace, Guid.NewGuid().ToString("n"));
        run.WriteState(new RunState(run.RunId, _workspace, "Text", DateTimeOffset.Now, 0, 5,
            BaselineHead: _head, Approved: true, PendingGateFailure: "old failure"));
        run.WriteBaseline(new Baseline(_head, ""));
        run.WritePlan(Plan);
        run.ReadDecisionLedger();
        if (pending) AddPending(run);
        return run;
    }

    private static void AddPending(RunDirectory run)
    {
        var ledger = run.ReadDecisionLedger();
        ledger.AddFindings([new Finding("major", "one.cs", "rule one"), new Finding("major", "two.cs", "rule two")], LedgerPhase.CodeReview);
        ledger.RecordFixAttempt("A", ["F-0001", "F-0002"], new BuildResult("done", [],
            new Verification("unavailable", "server gate"), "fixed",
            Gate: new GateRun("passed", "Fix gate", "exit 0", 0, "ok", 0.01, null)), true, "targeted");
    }

    private Task<string> Confirm(RunDirectory run, bool approved, string? timing, OrchestratorDecisionBatch? decisions = null) =>
        ForgeTools.ConfirmPlan(SessionRoots.None, _workspace, run.RunId, Plan, approved, CancellationToken.None,
            decisions: decisions, fullGate: timing);

    private Task<string> StartReview(JobRegistry registry, RunDirectory run, IVendor vendor, bool grant) =>
        ForgeTools.StartWork(registry, SessionRoots.None, _workspace, run.RunId, "review.code", "critic", null, "codex",
            null, null, null, null, grant, null, null, CancellationToken.None, _ => vendor, Prompts());

    private async Task InitializeGit()
    {
        Directory.CreateDirectory(_workspace);
        var git = new GitClient(_workspace);
        await git.OutputAsync(["init", "-q"], CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_workspace, "tracked.txt"), "original");
        await git.OutputAsync(["add", "tracked.txt"], CancellationToken.None);
        await git.OutputAsync(["-c", "user.name=Tests", "-c", "user.email=tests@example.invalid", "commit", "-qm", "initial"], CancellationToken.None);
        _head = (await git.OutputAsync(["rev-parse", "HEAD"], CancellationToken.None)).Trim();
    }

    private static bool Valid(string? timing) => timing is null or "beforeNextRound" or "final";
    private static string StatePath(RunDirectory run) => Path.Combine(run.Path, "state.json");
    private static void SetSavedTiming(RunDirectory run, string? timing)
    {
        var node = JsonNode.Parse(File.ReadAllText(StatePath(run)))!.AsObject();
        if (timing is null) node.Remove("fullGate");
        else node["fullGate"] = timing;
        AtomicFile.Write(StatePath(run), node.ToJsonString());
    }

    private static (byte[] State, byte[] Plan, byte[] Ledger) Snapshot(RunDirectory run) =>
        (File.ReadAllBytes(StatePath(run)), File.ReadAllBytes(run.PlanPath), File.ReadAllBytes(run.DecisionLedgerPath));
    private static void AssertSnapshot((byte[] State, byte[] Plan, byte[] Ledger) before, RunDirectory run)
    {
        Assert.Equal(before.State, File.ReadAllBytes(StatePath(run)));
        Assert.Equal(before.Plan, File.ReadAllBytes(run.PlanPath));
        Assert.Equal(before.Ledger, File.ReadAllBytes(run.DecisionLedgerPath));
    }

    private static async Task AssertFiltered(Func<Task<string>> action, string reason)
    {
        var result = await ToolErrors.Surfaced(async (_, _) => new CallToolResult {
            Content = [new TextContentBlock { Text = await action() }] })(null!, CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Contains(reason, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    private static PromptLibrary Prompts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "prompts"))) directory = directory.Parent;
        return new PromptLibrary(Path.Combine(directory!.FullName, "prompts"));
    }

    private sealed class ReviewGit(bool empty) : IReviewGit
    {
        public int Reads { get; private set; }
        public Task<ReviewWindow> ReadReviewWindowAsync(string baselineHead, IReadOnlyList<string> excludedPaths, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(new ReviewWindow(baselineHead, baselineHead, false, ["one.cs"],
                empty ? [] : [new ReviewFile("one.cs", "ordinary change", false)]));
        }
    }
}
