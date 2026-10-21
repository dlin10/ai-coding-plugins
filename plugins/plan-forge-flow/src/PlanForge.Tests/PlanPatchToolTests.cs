using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using PlanForge.Acts;
using PlanForge.Diagnostics;
using PlanForge.Infrastructure;
using PlanForge.Mcp;
using PlanForge.Prompts;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class PlanPatchToolTests : IDisposable
{
    private const string FirstBody = "Unique first task body hidden from patch answers.";
    private const string SecondBody = "Unique second task body hidden from patch answers.";
    private const string NewBody = "Unique replacement body hidden from patch answers.";
    private const string Plan = "# Export plan\nBrief for the exporter.\n\n## Approach\n\n" +
                                "1. Snapshot orders.\n" + FirstBody + "\n\n" +
                                "2. Publish orders.\n" + SecondBody + "\n";
    private const string Replacement = "1. Snapshot updated orders.\n" + NewBody + "\n";
    private const string Staged = "# Export plan\nBrief for the exporter.\n\n## Requirements\n\n- Orders export nightly.\n\n" +
                                  "## Approach\n\n1. Snapshot orders.\n\n2. Publish orders.\n\n3. Notify subscribers.\n\n" +
                                  "4. Archive snapshots.\n\n## Gates\n\n- G1 the suite passes.\n";

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_patch_answers_with_the_new_outline_and_no_plan_text_beyond_labels()
    {
        var run = NewRun();
        var outline = PlanOutline.Of(run.ReadPlan());
        var edit = Edit(outline);

        var json = await Patch(run, outline.PlanHash, [edit]);
        var result = JsonSerializer.Deserialize(json, ForgeToolJson.Default.PlanPatchResult)!;

        Assert.Equal(run.RunId, result.RunId);
        Assert.Equal(outline.PlanHash, result.BaseHash);
        Assert.Equal(OutlineJson(PlanOutline.Of(run.ReadPlan())), OutlineJson(result.Outline));
        Assert.Equal(new PlanPatchChange("replace", edit.Target!, "1. Snapshot orders."), Assert.Single(result.Changed));
        Assert.Equal(run.PlanPath, result.Documents.Plan!.Path);
        Assert.Equal(run.FlowLogPath, result.Documents.FlowLog!.Path);
        AssertNoBodies(json);
        AssertNoBodies(File.ReadAllText(run.FlowLogPath));
        AssertNoBodies(AtomicFile.Read(run.DiagnosticLogPath));
        Assert.Equal(PlanPatchSizes.Payload([edit]).PayloadSize, result.Sizes.PayloadSize);
        Assert.Equal(new PlanPatchSize(Plan.Length, Encoding.UTF8.GetByteCount(Plan)), result.Sizes.PlanBefore);
        Assert.Equal(new PlanPatchSize(Replacement.Length, Encoding.UTF8.GetByteCount(Replacement)), result.Sizes.Written);
    }

    [Fact]
    public async Task The_outline_tool_lists_the_written_plan()
    {
        var run = NewRun(written: false);
        await ForgeTools.WritePlan(SessionRoots.None, _workspace, run.RunId, Plan, CancellationToken.None);

        var result = await Outline(run);

        Assert.Equal(run.RunId, result.RunId);
        Assert.Equal(OutlineJson(PlanOutline.Of(Plan)), OutlineJson(result.Outline));
        Assert.Equal(new[] { "heading", "heading", "task", "task" }, result.Outline.Targets.Select(target => target.Kind));
        AssertNoBodies(JsonSerializer.Serialize(result, ForgeToolJson.Default.PlanOutlineResult));
    }

    [Fact]
    public async Task The_outline_tool_refuses_a_run_with_no_plan()
    {
        var run = NewRun(written: false);
        var before = Protected(run);

        var error = await Assert.ThrowsAsync<ArgumentRejectedException>(() => Outline(run));

        Assert.Contains("forge.plan.write", error.Message, StringComparison.Ordinal);
        AssertProtected(run, before);
    }

    [Fact]
    public async Task A_patch_with_no_plan_written_is_refused()
    {
        var run = NewRun(written: false);
        var before = Protected(run);

        var error = await Assert.ThrowsAsync<PlanPatchRejectedException>(() => Patch(run, "0000000000000000", []));

        Assert.Equal("no_plan", error.Reason);
        Assert.Contains("forge.plan.write", error.Message, StringComparison.Ordinal);
        AssertProtected(run, before);
        Assert.Null(error.Sizes.PlanBefore);
    }

    [Theory]
    [InlineData("\uFEFF")]
    [InlineData("\uFEFF\uFEFF  \n")]
    [InlineData("\uFEFF\n\t")]
    public async Task Blank_plans_have_no_outline_and_cannot_be_patched(string draft)
    {
        var run = NewRun(written: false);
        var json = await ForgeTools.WritePlan(SessionRoots.None, _workspace, run.RunId, draft, CancellationToken.None);
        Assert.Null(JsonSerializer.Deserialize(json, ForgeToolJson.Default.PlanWriteResult)!.Outline);
        var before = Protected(run);

        await Assert.ThrowsAsync<ArgumentRejectedException>(() => Outline(run));
        var error = await Assert.ThrowsAsync<PlanPatchRejectedException>(() => Patch(run, "0000000000000000", []));

        Assert.Equal("no_plan", error.Reason);
        Assert.NotNull(error.Sizes.PlanBefore);
        AssertProtected(run, before);
    }

    [Fact]
    public async Task A_draft_starting_with_a_byte_order_mark_has_one_outline_from_write_to_patch()
    {
        var run = NewRun(written: false);
        var json = await ForgeTools.WritePlan(SessionRoots.None, _workspace, run.RunId, "\uFEFF" + Plan, CancellationToken.None);
        var written = JsonSerializer.Deserialize(json, ForgeToolJson.Default.PlanWriteResult)!.Outline!;
        var read = (await Outline(run)).Outline;
        Assert.Equal(OutlineJson(written), OutlineJson(read));
        Assert.Equal(PlanOutline.Hash(Plan), written.PlanHash);

        var answer = await Patch(run, written.PlanHash, [Edit(written)]);

        Assert.Equal(OutlineJson((await Outline(run)).Outline),
            OutlineJson(JsonSerializer.Deserialize(answer, ForgeToolJson.Default.PlanPatchResult)!.Outline));
    }

    [Fact]
    public async Task A_patch_inserting_a_byte_order_mark_first_answers_with_the_outline_read_back()
    {
        var run = NewRun();
        var outline = PlanOutline.Of(run.ReadPlan());
        var target = outline.Targets[0];
        var json = await Patch(run, outline.PlanHash,
            [new PlanEdit("insertBefore", target.Id, target.Hash, "\uFEFF\uFEFF# New brief\n")]);

        Assert.False(run.ReadPlan().StartsWith('\uFEFF'));
        Assert.Equal(OutlineJson((await Outline(run)).Outline),
            OutlineJson(JsonSerializer.Deserialize(json, ForgeToolJson.Default.PlanPatchResult)!.Outline));
    }

    /// <summary>A patch into the first task reaches every built task, so it keeps what a whole write keeps: nothing.</summary>
    [Fact]
    public async Task A_patch_into_the_first_task_leaves_the_state_a_whole_write_would()
    {
        var patched = NewRun("patched");
        var written = NewRun("written");
        Approve(patched);
        Approve(written);
        var outline = PlanOutline.Of(Plan);

        await Patch(patched, outline.PlanHash, [Edit(outline)]);
        await ForgeTools.WritePlan(SessionRoots.None, _workspace, written.RunId, patched.ReadPlan(), CancellationToken.None);

        var patchedState = patched.ReadState();
        var writtenState = written.ReadState() with { RunId = patchedState.RunId, StartedAt = patchedState.StartedAt };
        Assert.Equal(JsonSerializer.Serialize(patchedState, ForgeJson.Default.RunState),
                     JsonSerializer.Serialize(writtenState, ForgeJson.Default.RunState));
        Assert.False(patchedState.Approved);
        Assert.Equal(0, patchedState.TasksCompleted);
        Assert.Equal(string.Empty, patchedState.BuilderSessionId);
        Assert.Contains("the plan was patched with forge.plan.patch", File.ReadAllText(patched.FlowLogPath), StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #168: run B3c rebuilt 42 built tasks across 8 reopenings, every one of them a patch to
    /// an unbuilt task, the title, Requirements or Gates. Such a patch keeps the progress, the
    /// hand-over and the owed gate failure, and still takes the approval and the session back.
    /// </summary>
    [Theory]
    [InlineData("title")]
    [InlineData("requirements")]
    [InlineData("gates")]
    [InlineData("unbuilt task")]
    [InlineData("last task deleted")]
    [InlineData("task appended")]
    public async Task A_patch_that_changes_no_built_task_keeps_the_progress(string change)
    {
        var run = NewStagedRun();
        var outline = PlanOutline.Of(Staged);

        var json = await Patch(run, outline.PlanHash, StagedEdits(outline, change));

        var result = JsonSerializer.Deserialize(json, ForgeToolJson.Default.PlanPatchResult)!;
        var state = run.ReadState();
        Assert.False(state.Approved);
        Assert.Equal(string.Empty, state.BuilderSessionId);
        Assert.Null(state.BuilderSessionScope);
        Assert.Equal(2, state.TasksCompleted);
        Assert.Equal(new[] { 1, 2 }, state.TaskChanges!.Select(task => task.TaskNumber));
        Assert.Equal("previous failure", state.PendingGateFailure);
        Assert.Equal(2, result.TasksKept);
        Assert.Equal(3, result.FirstTaskOwed);
        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("The approval no longer holds because the plan was patched with forge.plan.patch.", flow, StringComparison.Ordinal);
        Assert.Contains("Tasks 1–2 stay built.", flow, StringComparison.Ordinal);
        Assert.Contains("it will start from task 3.", flow, StringComparison.Ordinal);
    }

    /// <summary>
    /// A patch that changes task k's text sends the build back to k − 1 tasks, whether it rewrites
    /// the task, inserts or removes one before it, or inserts prose after the task before — which
    /// the build reads as part of that task.
    /// </summary>
    [Theory]
    [InlineData("task 2 rewritten", 1)]
    [InlineData("task 1 rewritten", 0)]
    [InlineData("prose after task 1", 0)]
    [InlineData("task inserted at 2", 1)]
    [InlineData("task 2 removed", 1)]
    public async Task A_patch_into_a_built_task_rebuilds_from_that_task(string change, int kept)
    {
        var run = NewStagedRun();
        var outline = PlanOutline.Of(Staged);

        var json = await Patch(run, outline.PlanHash, StagedEdits(outline, change));

        var result = JsonSerializer.Deserialize(json, ForgeToolJson.Default.PlanPatchResult)!;
        var state = run.ReadState();
        Assert.False(state.Approved);
        Assert.Equal(string.Empty, state.BuilderSessionId);
        Assert.Equal(kept, state.TasksCompleted);
        Assert.Equal(Enumerable.Range(1, kept), (state.TaskChanges ?? []).Select(task => task.TaskNumber));
        if (kept == 0) Assert.Null(state.TaskChanges);
        Assert.Null(state.PendingGateFailure);
        Assert.Equal(kept, result.TasksKept);
        Assert.Equal(kept + 1, result.FirstTaskOwed);
        Assert.Contains($"Build progress was reset from 2 completed tasks to {kept}.", File.ReadAllText(run.FlowLogPath),
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// The second patch finds the approval already gone, and still takes back the built tasks it
    /// reaches: an earlier patch that kept the progress is no licence for a later one.
    /// </summary>
    [Fact]
    public async Task A_later_patch_into_a_built_task_rebuilds_although_the_approval_is_gone()
    {
        var run = NewStagedRun();
        await Patch(run, PlanOutline.Hash(Staged), StagedEdits(PlanOutline.Of(Staged), "gates"));
        var outline = PlanOutline.Of(run.ReadPlan());

        var json = await Patch(run, outline.PlanHash, StagedEdits(outline, "task 2 rewritten"));

        Assert.Equal(1, run.ReadState().TasksCompleted);
        Assert.Equal(1, JsonSerializer.Deserialize(json, ForgeToolJson.Default.PlanPatchResult)!.TasksKept);
        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("No approval was standing when the plan was patched with forge.plan.patch. " +
                        "Build progress was reset from 2 completed tasks to 1.", flow, StringComparison.Ordinal);
    }

    /// <summary>
    /// The kept progress survives the rest of the revision: a round over the plan on disk changes no
    /// text, while a whole write — and a round handed a draft — still keeps nothing.
    /// </summary>
    [Theory]
    [InlineData("round over the plan on disk", 2)]
    [InlineData("round handed a draft", 0)]
    [InlineData("whole write", 0)]
    public async Task After_a_patch_that_kept_the_progress_only_a_whole_draft_takes_it_back(string next, int kept)
    {
        var run = NewStagedRun();
        await Patch(run, PlanOutline.Hash(Staged), StagedEdits(PlanOutline.Of(Staged), "gates"));
        var patched = run.ReadPlan();
        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "reviewed"));

        if (next == "whole write")
            await ForgeTools.WritePlan(SessionRoots.None, _workspace, run.RunId, patched, CancellationToken.None);
        else
            await new PlanReview(critic, new PromptLibrary()).ReviewAsync(run, next == "round handed a draft" ? patched : null,
                new Selection("critic", null), "Revised the gates.", null, false, CancellationToken.None);

        Assert.Equal(kept, run.ReadState().TasksCompleted);
    }

    /// <summary>
    /// What the re-approval keeps is what the next build stands on: it runs the first task owed, and
    /// its fresh session is told what the kept tasks changed.
    /// </summary>
    [Fact]
    public async Task The_build_after_reapproval_runs_the_first_owed_task_with_the_kept_hand_over()
    {
        var run = NewStagedRun();
        var outline = PlanOutline.Of(Staged);
        await Patch(run, outline.PlanHash, StagedEdits(outline, "unbuilt task"));
        run.WriteState(run.ReadState() with { Approved = true });
        var builder = new RecordingVendor("codex");
        builder.Enqueue(new BuildResult("done", ["third.cs"], new Verification("passed", "read the condition"), "built"));

        var result = await new Build(builder, new PromptLibrary()).NextAsync(run, new Selection("builder", null), CancellationToken.None);

        Assert.Equal(3, result.TasksCompleted);
        var prompt = Assert.Single(builder.Sessions).PromptText;
        Assert.Contains("# Task 3 of 4", prompt, StringComparison.Ordinal);
        Assert.Contains("Notify every subscriber.", prompt, StringComparison.Ordinal);
        Assert.Contains("- Task 1: first.cs", prompt, StringComparison.Ordinal);
        Assert.Contains("- Task 2: second.cs", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("no_plan")]
    [InlineData("no_edits")]
    [InlineData("stale_base")]
    [InlineData("invalid_edit")]
    [InlineData("unknown_target")]
    [InlineData("stale_target")]
    [InlineData("conflicting_edits")]
    [InlineData("blank_result")]
    [InlineData("no_change")]
    [InlineData("shape_broken")]
    public async Task A_refused_patch_changes_no_protected_file(string reason)
    {
        var run = NewRun(written: reason != "no_plan");
        Approve(run);
        if (reason == "blank_result") run.WritePlan("# Title\n");
        var outline = PlanOutline.Of(reason == "no_plan" ? Plan : run.ReadPlan());
        var edit = reason == "blank_result"
            ? new PlanEdit("delete", "s0", outline.Targets[0].Hash) : Edit(outline);
        IReadOnlyList<PlanEdit?> edits = reason switch
        {
            "no_edits" => [],
            "invalid_edit" => [edit with { Content = " " }],
            "unknown_target" => [edit with { Target = "s999" }],
            "stale_target" => [edit with { TargetHash = "0000000000000000" }],
            "conflicting_edits" => [edit, edit],
            "no_change" => [edit with { Content = Plan.Substring(outline.Targets[2].Start, outline.Targets[2].Length) }],
            "shape_broken" => [edit with { Content = "9. Wrong number.\n" }],
            _ => [edit]
        };
        var before = Protected(run);

        var error = await Assert.ThrowsAsync<PlanPatchRejectedException>(() =>
            Patch(run, reason == "stale_base" ? "0000000000000000" : outline.PlanHash, edits));

        Assert.Equal(reason, error.Reason);
        AssertProtected(run, before);
    }

    [Fact]
    public async Task A_refusal_message_and_its_log_carry_no_plan_text()
    {
        var run = NewRun();
        var outline = PlanOutline.Of(Plan);
        var digits = new string('\u0661', 90);
        var result = await Surfaced(() => Patch(run, outline.PlanHash, [Edit(outline) with { Content = digits + ". Invalid.\n" }]));

        Assert.True(result.IsError);
        Assert.Contains("unreadable task number", Answer(result), StringComparison.Ordinal);
        Assert.DoesNotContain(digits, Answer(result), StringComparison.Ordinal);
        Assert.DoesNotContain(digits, AtomicFile.Read(run.DiagnosticLogPath), StringComparison.Ordinal);
        var failure = Assert.Single(Entries(run), entry => entry.Event == "tool.failed");
        Assert.DoesNotContain(digits, failure.Fields!["stack"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("baseHash")]
    [InlineData("target")]
    [InlineData("targetHash")]
    [InlineData("op")]
    public async Task Malformed_supplied_values_never_reach_answers_or_logs(string field)
    {
        const string marker = "First private paragraph line that must never reach a patch answer or its logs.\n" +
                              "Second private paragraph line that must never reach a patch answer or its logs.";
        var run = NewRun();
        var outline = PlanOutline.Of(Plan);
        var edit = Edit(outline);
        edit = field switch
        {
            "target" => edit with { Target = marker },
            "targetHash" => edit with { TargetHash = marker },
            "op" => edit with { Op = marker },
            _ => edit
        };
        var before = Protected(run);

        var result = await Surfaced(() => Patch(run, field == "baseHash" ? marker : outline.PlanHash, [edit]));

        Assert.True(result.IsError);
        var log = AtomicFile.Read(run.DiagnosticLogPath);
        foreach (var line in marker.Split('\n'))
        {
            Assert.DoesNotContain(line, Answer(result), StringComparison.Ordinal);
            Assert.DoesNotContain(line, log, StringComparison.Ordinal);
        }
        Assert.Contains("characters", Answer(result), StringComparison.Ordinal);
        Assert.Single(Entries(run), entry => entry.Event == "tool.call");
        Assert.Single(Entries(run), entry => entry.Event == "tool.failed");
        Assert.Single(Entries(run), entry => entry.Event == "plan.patch");
        AssertProtected(run, before);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"target\":\"s2\",\"targetHash\":\"0000000000000000\"}]")]
    [InlineData("[{\"op\":\"replace\",\"targetHash\":\"0000000000000000\"}]")]
    [InlineData("[{\"op\":\"replace\",\"target\":\"s2\"}]")]
    public async Task Edits_bound_from_json_with_missing_fields_are_refused_without_change(string json)
    {
        var run = NewRun();
        var before = Protected(run);
        var edits = JsonSerializer.Deserialize<IReadOnlyList<PlanEdit?>>(json, ToolArgumentJson.ArgumentOptions)!;

        var error = await Assert.ThrowsAsync<PlanPatchRejectedException>(() => Patch(run, PlanOutline.Hash(Plan), edits));

        Assert.Equal("invalid_edit", error.Reason);
        AssertProtected(run, before);
    }

    [Fact]
    public async Task An_unreadable_plan_fails_the_patch_unchanged()
    {
        var run = NewRun();
        var before = Protected(run);
        PlanPatchFailedException error;
        using (Hold(run.PlanPath, FileShare.None))
            error = await Assert.ThrowsAsync<PlanPatchFailedException>(() => Patch(run, PlanOutline.Hash(Plan), [Edit(PlanOutline.Of(Plan))]));

        Assert.Equal("failed_unchanged", error.Outcome);
        Assert.Contains("plan is unchanged", error.Message, StringComparison.Ordinal);
        Assert.Null(error.Sizes.PlanBefore);
        Assert.Null(error.InnerException);
        AssertProtected(run, before);
    }

    [Fact]
    public async Task A_state_that_cannot_be_written_fails_the_patch_and_changes_nothing()
    {
        var run = NewRun();
        Approve(run);
        var before = Protected(run);
        PlanPatchFailedException error;
        using (Hold(Path.Combine(run.Path, "state.json"), FileShare.Read))
            error = await Assert.ThrowsAsync<PlanPatchFailedException>(() => Patch(run, PlanOutline.Hash(Plan), [Edit(PlanOutline.Of(Plan))]));

        Assert.Equal("failed_unchanged", error.Outcome);
        Assert.Contains("approval was not withdrawn", error.Message, StringComparison.Ordinal);
        AssertProtected(run, before);
    }

    [Fact]
    public async Task A_failed_reopening_entry_leaves_the_plan_unchanged()
    {
        var run = NewRun();
        Approve(run);
        var before = Protected(run);
        PlanPatchFailedException error;
        using (Hold(run.FlowLogPath, FileShare.Read))
            error = await Assert.ThrowsAsync<PlanPatchFailedException>(() => Patch(run, PlanOutline.Hash(Plan), [Edit(PlanOutline.Of(Plan))]));

        Assert.Equal("failed_unchanged", error.Outcome);
        Assert.Contains("approval was already withdrawn", error.Message, StringComparison.Ordinal);
        Assert.False(run.ReadState().Approved);
        AssertProtected(run, before, exceptState: true);
    }

    [Fact]
    public async Task A_failed_plan_write_leaves_the_approval_withdrawn()
    {
        var run = NewRun();
        Approve(run);
        var before = File.ReadAllBytes(run.PlanPath);
        PlanPatchFailedException error;
        using (Hold(run.PlanPath, FileShare.Read))
            error = await Assert.ThrowsAsync<PlanPatchFailedException>(() => Patch(run, PlanOutline.Hash(Plan), [Edit(PlanOutline.Of(Plan))]));

        Assert.Equal("failed_unchanged", error.Outcome);
        Assert.Contains("approval was already withdrawn", error.Message, StringComparison.Ordinal);
        Assert.False(run.ReadState().Approved);
        Assert.Equal(before, File.ReadAllBytes(run.PlanPath));
    }

    [Fact]
    public async Task A_failed_flow_entry_reports_the_applied_patch()
    {
        var run = NewRun();
        var outline = PlanOutline.Of(Plan);
        PlanPatchFailedException error;
        using (Hold(run.FlowLogPath, FileShare.Read))
            error = await Assert.ThrowsAsync<PlanPatchFailedException>(() => Patch(run, outline.PlanHash, [Edit(outline)]));

        Assert.Equal("failed_after_patch", error.Outcome);
        Assert.Contains("plan was patched", error.Message, StringComparison.Ordinal);
        Assert.Contains((await Outline(run)).Outline.PlanHash, error.Message, StringComparison.Ordinal);
        Assert.Contains(NewBody, run.ReadPlan(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        AssertNoBodies(error.ToString());
    }

    [Fact]
    public async Task A_diagnostic_log_that_cannot_be_written_does_not_change_the_outcome()
    {
        var run = NewRun();
        File.WriteAllText(run.DiagnosticLogPath, "");
        string json;
        using (Hold(run.DiagnosticLogPath, FileShare.Read))
            json = await Patch(run, PlanOutline.Hash(Plan), [Edit(PlanOutline.Of(Plan))]);

        Assert.Contains(NewBody, run.ReadPlan(), StringComparison.Ordinal);
        Assert.Equal(PlanOutline.Hash(run.ReadPlan()), JsonSerializer.Deserialize(json, ForgeToolJson.Default.PlanPatchResult)!.Outline.PlanHash);
    }

    [Fact]
    public async Task A_replayed_patch_changes_nothing()
    {
        var run = NewRun();
        var outline = PlanOutline.Of(Plan);
        var edits = new[] { Edit(outline) };
        await Patch(run, outline.PlanHash, edits);
        var before = Protected(run);

        var error = await Assert.ThrowsAsync<PlanPatchRejectedException>(() => Patch(run, outline.PlanHash, edits));

        Assert.Equal("stale_base", error.Reason);
        Assert.Contains(PlanOutline.Hash(run.ReadPlan()), error.Message, StringComparison.Ordinal);
        AssertProtected(run, before);
    }

    [Fact]
    public async Task The_later_of_two_patches_from_one_base_is_refused()
    {
        var run = NewRun();
        var outline = PlanOutline.Of(Plan);
        await Patch(run, outline.PlanHash, [Edit(outline)]);
        var before = Protected(run);
        var second = outline.Targets.Last();

        var error = await Assert.ThrowsAsync<PlanPatchRejectedException>(() => Patch(run, outline.PlanHash,
            [new PlanEdit("replace", second.Id, second.Hash, "2. A different publication.\n")]));

        Assert.Equal("stale_base", error.Reason);
        AssertProtected(run, before);
    }

    [Fact]
    public async Task The_next_critic_receives_the_whole_patched_plan()
    {
        var run = NewRun();
        await Patch(run, PlanOutline.Hash(Plan), [Edit(PlanOutline.Of(Plan))]);
        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "reviewed"));

        await new PlanReview(critic, new PromptLibrary()).ReviewAsync(run, null, new Selection("critic", null),
            "Revised the first task.", null, false, CancellationToken.None);

        Assert.Contains(run.ReadPlan(), Assert.Single(critic.Sessions).PromptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_patch_spends_no_review_round_and_leaves_the_cap()
    {
        var run = NewRun();
        run.WriteState(run.ReadState() with { ReviewRounds = 5, ReviewRoundCap = 5, GrantedReviewRounds = 2 });
        var state = run.ReadState();

        await Patch(run, PlanOutline.Hash(Plan), [Edit(PlanOutline.Of(Plan))]);

        Assert.Equal(state, run.ReadState());
        Assert.False(Directory.Exists(Path.Combine(run.Path, "jobs")));
    }

    [Fact]
    public async Task The_build_reads_the_patched_tasks_and_brief()
    {
        var run = NewRun();
        var outline = PlanOutline.Of(Plan);
        var title = outline.Targets[0];
        await Patch(run, outline.PlanHash,
            [Edit(outline), new PlanEdit("replace", title.Id, title.Hash, "# Updated exporter\nUpdated exporter brief.\n")]);
        run.WriteState(run.ReadState() with { Approved = true });
        var builder = new RecordingVendor("codex");
        builder.Enqueue(new BuildResult("done", [], new Verification("passed", "read the condition"), "built"));

        var result = await new Build(builder, new PromptLibrary()).NextAsync(run, new Selection("builder", null), CancellationToken.None);

        Assert.Equal(2, result.TaskCount);
        Assert.Equal(1, result.TasksCompleted);
        var prompt = Assert.Single(builder.Sessions).PromptText;
        Assert.Contains("Updated exporter brief.", prompt, StringComparison.Ordinal);
        Assert.Contains(NewBody, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(FirstBody, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_applied_patch_is_recorded_in_the_flow_log_without_plan_text_beyond_labels()
    {
        var run = NewRun();
        var outline = PlanOutline.Of(Plan);
        var edit = Edit(outline);
        var json = await Patch(run, outline.PlanHash, [edit]);
        var result = JsonSerializer.Deserialize(json, ForgeToolJson.Default.PlanPatchResult)!;
        var flow = File.ReadAllText(run.FlowLogPath);

        Assert.Equal(1, flow.Split("## Plan patched", StringSplitOptions.None).Length - 1);
        Assert.Contains(outline.PlanHash, flow, StringComparison.Ordinal);
        Assert.Contains(result.Outline.PlanHash, flow, StringComparison.Ordinal);
        Assert.Contains("replace " + edit.Target + ": 1. Snapshot orders.", flow, StringComparison.Ordinal);
        foreach (var (name, size) in result.Sizes.Measurements())
            Assert.Contains($"{name}: {size.Characters} characters, {size.Utf8Bytes} UTF-8 bytes", flow, StringComparison.Ordinal);
        AssertNoBodies(flow);
    }

    [Theory]
    [InlineData("applied")]
    [InlineData("no_plan")]
    [InlineData("stale_base")]
    [InlineData("shape_broken")]
    [InlineData("failed_unchanged")]
    [InlineData("failed_after_patch")]
    public async Task Every_patch_outcome_emits_one_diagnostic_event_with_the_sizes_that_exist(string outcome)
    {
        var run = NewRun(written: outcome != "no_plan");
        var outline = PlanOutline.Of(Plan);
        var edit = Edit(outline);
        if (outcome == "shape_broken") edit = edit with { Content = "9. Broken order.\n" };
        PlanPatchSizes sizes;
        using (outcome == "failed_unchanged" ? Hold(run.PlanPath, FileShare.None)
               : outcome == "failed_after_patch" ? Hold(run.FlowLogPath, FileShare.Read) : null)
        {
            try
            {
                var json = await Patch(run, outcome == "stale_base" ? "0000000000000000" : outline.PlanHash, [edit]);
                sizes = JsonSerializer.Deserialize(json, ForgeToolJson.Default.PlanPatchResult)!.Sizes;
            }
            catch (PlanPatchRejectedException error) { sizes = error.Sizes; }
            catch (PlanPatchFailedException error) { sizes = error.Sizes; }
        }

        var entry = Assert.Single(Entries(run), entry => entry.Event == "plan.patch");
        Assert.Equal(outcome, entry.Fields!["outcome"]);
        Assert.Equal("1", entry.Fields["editCount"]);
        Assert.True(long.Parse(entry.Fields["elapsedMs"], CultureInfo.InvariantCulture) >= 0);
        var hasPlan = outcome is not ("no_plan" or "failed_unchanged");
        var hasResult = outcome is "applied" or "shape_broken" or "failed_after_patch";
        Assert.Equal(hasPlan, sizes.PlanBefore is not null);
        Assert.Equal(hasResult, sizes.Removed is not null);
        Assert.Equal(hasResult, sizes.Written is not null);
        Assert.Equal(hasResult, sizes.PlanAfter is not null);
        Assert.Equal(3 + 2 * (2 + (hasPlan ? 1 : 0) + (hasResult ? 3 : 0)), entry.Fields.Count);
        foreach (var (name, value) in sizes.LogFields()) Assert.Equal(value, entry.Fields[name]);
        AssertNoBodies(AtomicFile.Read(run.DiagnosticLogPath));
        var call = Assert.Single(Entries(run), record => record.Event == "tool.call");
        Assert.Equal(PlanPatchSizes.Payload([edit]).PayloadSize.Characters.ToString(CultureInfo.InvariantCulture), call.Fields!["payloadCharacters"]);
        Assert.Equal(PlanPatchSizes.Payload([edit]).PayloadSize.Utf8Bytes.ToString(CultureInfo.InvariantCulture), call.Fields["payloadUtf8Bytes"]);
        Assert.False(call.Fields.ContainsKey("edits"));
    }

    private RunDirectory NewRun(string id = "patch", bool written = true)
    {
        var run = RunDirectory.Create(_workspace, id);
        run.WriteState(new RunState(id, _workspace, "Text", DateTimeOffset.Now, 1, 5));
        if (written) run.WritePlan(Plan);
        File.WriteAllText(run.FlowLogPath, "Existing audit.\n");
        return run;
    }

    private static void Approve(RunDirectory run) => run.WriteState(run.ReadState() with
    {
        Approved = true, TasksCompleted = 2, BuilderSessionId = "builder-session", BuilderVendor = "codex",
        BuilderSessionScope = "task 2", PendingGateFailure = "previous failure",
        TaskChanges = [new TaskChange(1, ["first.cs"]), new TaskChange(2, ["second.cs"])],
        BuilderInstructions = "Keep the exporter synchronous.", FullGate = "final"
    });

    private RunDirectory NewStagedRun()
    {
        var run = NewRun("staged", written: false);
        run.WritePlan(Staged);
        Approve(run);
        return run;
    }

    /// <summary>The edits that make one named change to <see cref="Staged"/>.</summary>
    /// <param name="outline">The outline of the plan the edits are written against.</param>
    /// <param name="change">The change, as the tests name it.</param>
    /// <returns>The edits.</returns>
    private static PlanEdit[] StagedEdits(PlanOutline outline, string change)
    {
        PlanEdit At(string label, string op, string? content)
        {
            var target = outline.Targets.Single(target => target.Label == label);
            return new PlanEdit(op, target.Id, target.Hash, content);
        }

        return change switch
        {
            "title" => [At("# Export plan", "replace", "# Updated exporter\nUpdated exporter brief.\n\n")],
            "requirements" => [At("## Requirements", "replace", "## Requirements\n\n- Orders export hourly.\n\n")],
            "gates" => [At("## Gates", "replace", "## Gates\n\n- G1 the suite and the linter pass.\n")],
            "unbuilt task" => [At("3. Notify subscribers.", "replace", "3. Notify every subscriber.\n\n")],
            "last task deleted" => [At("4. Archive snapshots.", "delete", null)],
            "task appended" => [At("## Gates", "insertBefore", "5. Compress archives.\n\n")],
            "task 2 rewritten" => [At("2. Publish orders.", "replace", "2. Publish signed orders.\n\n")],
            "task 1 rewritten" => [At("1. Snapshot orders.", "replace", "1. Snapshot paid orders.\n\n")],
            "prose after task 1" => [At("1. Snapshot orders.", "insertAfter", "Keep the snapshot read-only.\n\n")],
            "task inserted at 2" =>
            [
                At("2. Publish orders.", "insertBefore", "2. Validate orders.\n\n"),
                At("2. Publish orders.", "replace", "3. Publish orders.\n\n"),
                At("3. Notify subscribers.", "replace", "4. Notify subscribers.\n\n"),
                At("4. Archive snapshots.", "replace", "5. Archive snapshots.\n\n")
            ],
            "task 2 removed" =>
            [
                At("2. Publish orders.", "delete", null),
                At("3. Notify subscribers.", "replace", "2. Notify subscribers.\n\n"),
                At("4. Archive snapshots.", "replace", "3. Archive snapshots.\n\n")
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, null)
        };
    }

    private static PlanEdit Edit(PlanOutline outline)
    {
        var target = outline.Targets.First(target => target.Kind == "task");
        return new PlanEdit("replace", target.Id, target.Hash, Replacement);
    }

    private Task<string> Patch(RunDirectory run, string hash, IReadOnlyList<PlanEdit?> edits) =>
        ForgeTools.PatchPlan(SessionRoots.None, _workspace, run.RunId, hash, edits, CancellationToken.None);

    private async Task<PlanOutlineResult> Outline(RunDirectory run) =>
        JsonSerializer.Deserialize(await ForgeTools.OutlinePlan(SessionRoots.None, _workspace, run.RunId, CancellationToken.None),
                                   ForgeToolJson.Default.PlanOutlineResult)!;

    private static string OutlineJson(PlanOutline outline) => JsonSerializer.Serialize(outline, ForgeToolJson.Default.PlanOutline);

    private static Dictionary<string, byte[]?> Protected(RunDirectory run) =>
        new[] { run.PlanPath, Path.Combine(run.Path, "state.json"), run.DecisionLedgerPath, run.FlowLogPath }
            .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);

    private static void AssertProtected(RunDirectory run, Dictionary<string, byte[]?> before, bool exceptState = false)
    {
        foreach (var (path, bytes) in Protected(run))
            if (!exceptState || Path.GetFileName(path) != "state.json") Assert.Equal(before[path], bytes);
    }

    private static FileStream Hold(string path, FileShare share) => new(path, FileMode.Open, FileAccess.Read, share);

    private static IReadOnlyList<LogEntry> Entries(RunDirectory run) => AtomicFile.Read(run.DiagnosticLogPath)
        .Split('\n')
        .Select(line => line.TrimEnd('\r'))
        .Where(line => line.Length > 0)
        .Select(line => JsonSerializer.Deserialize(line, DiagnosticJson.Default.LogEntry)!).ToArray();

    private static void AssertNoBodies(string text)
    {
        foreach (var marker in new[] { FirstBody, SecondBody, NewBody }) Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
    }

    private static ValueTask<CallToolResult> Surfaced(Func<Task<string>> call) => ToolErrors.Surfaced(async (_, _) =>
    {
        var json = await call();
        return new CallToolResult { Content = [new TextContentBlock { Text = json }] };
    })(null!, CancellationToken.None);

    private static string Answer(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
