using System.Text.Json;
using PlanForge.Acts;
using PlanForge.Mcp;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

public sealed class PlanShapeTests : IDisposable
{
    private const string Draft = "# P\n## Requirements\nR1 R2 R1\n## Approach\n" +
                                 "1. A AlphaTests\n2. B BetaMatrixTests\n## Gates\nDone\n";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Write_and_patch_report_exact_shape_in_results_and_flow_log()
    {
        const string runId = "20260101-000000-abcdef";
        var run = RunDirectory.Create(_workspace, runId);
        run.WriteState(new RunState(runId, _workspace, "Text", DateTimeOffset.Now, 0, 5));

        var writtenJson = await ForgeTools.WritePlan(SessionRoots.None, _workspace, runId, Draft, CancellationToken.None);
        var written = JsonSerializer.Deserialize(writtenJson, ForgeToolJson.Default.PlanWriteResult)!;
        Assert.Equal(new PlanShape(92, 2, 37, 21, 25, 49, 1.96, 2, 2, 1, false), written.Shape);
        Assert.Contains("## Plan written", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
        Assert.Contains("- Requirements: 25; Approach: 49; ratio: 1.96", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);

        var task = written.Outline!.Targets.Single(target => target.Label == "2. B BetaMatrixTests");
        var edit = new PlanEdit("replace", task.Id, task.Hash, "2. C GammaMatrixTests\n");
        var patchedJson = await ForgeTools.PatchPlan(SessionRoots.None, _workspace, runId,
                                                    written.Outline.PlanHash, [edit], CancellationToken.None);
        var patched = JsonSerializer.Deserialize(patchedJson, ForgeToolJson.Default.PlanPatchResult)!;
        Assert.Equal(new PlanShape(93, 2, 38, 22, 25, 50, 2.00, 2, 2, 1, false), patched.Shape);
        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("## Plan patched", flow, StringComparison.Ordinal);
        Assert.Contains("- Requirements: 25; Approach: 50; ratio: 2.00", flow, StringComparison.Ordinal);
    }

    [Fact]
    public void The_split_threshold_uses_approach_characters()
    {
        const string prefix = "## Approach\n1. A\n";
        var small = prefix + new string('x', 22_000 - prefix.Length - 1);
        var large = small + "x";

        Assert.False(PlanShape.Of(small, PlanOutline.Of(small)).SplitSuggested);
        Assert.True(PlanShape.Of(large, PlanOutline.Of(large)).SplitSuggested);
    }

    [Fact]
    public void An_empty_level_two_heading_ends_the_approach_as_the_build_parses_it()
    {
        const string approach = "## Approach\n1. A\n";
        var plan = approach + "##\n" + new string('x', 23_000);

        var shape = PlanShape.Of(plan, PlanOutline.Of(plan));

        Assert.Equal(approach.Length, shape.ApproachCharacters);
        Assert.Equal(1, shape.TaskCount);
        Assert.False(shape.SplitSuggested);
    }

    [Fact]
    public void Before_round_one_the_first_draft_rule_stands_and_each_draft_becomes_the_checkpoint()
    {
        var (small, smallCheckpoint) = ShapeOf(21_649).Offer(0, null);
        var (large, largeCheckpoint) = ShapeOf(22_000).Offer(0, 21_649);

        Assert.False(small.SplitSuggested);
        Assert.Equal(21_649, smallCheckpoint);
        Assert.True(large.SplitSuggested);
        Assert.Null(large.SplitReason);
        Assert.Equal(22_000, largeCheckpoint);
    }

    [Fact]
    public void A_plan_that_crosses_the_threshold_after_round_one_is_offered_a_split()
    {
        var (shape, checkpoint) = ShapeOf(34_730).Offer(1, 21_649);

        Assert.True(shape.SplitSuggested);
        Assert.Equal("crossed", shape.SplitReason);
        Assert.Equal(21_649, shape.SplitCheckpoint);
        Assert.Equal(34_730, checkpoint);
    }

    [Fact]
    public void After_an_offer_only_a_doubling_of_the_checkpoint_offers_again()
    {
        var (below, belowCheckpoint) = ShapeOf(68_666).Offer(6, 34_730);
        var (doubled, doubledCheckpoint) = ShapeOf(70_787).Offer(7, 34_730);

        Assert.False(below.SplitSuggested);
        Assert.Null(below.SplitReason);
        Assert.Equal(34_730, belowCheckpoint);
        Assert.True(doubled.SplitSuggested);
        Assert.Equal("doubled", doubled.SplitReason);
        Assert.Equal(34_730, doubled.SplitCheckpoint);
        Assert.Equal(70_787, doubledCheckpoint);
    }

    [Fact]
    public void Doubling_a_plan_that_stays_under_the_threshold_offers_nothing()
    {
        var (shape, checkpoint) = ShapeOf(10_000).Offer(3, 4_000);

        Assert.False(shape.SplitSuggested);
        Assert.Equal(4_000, checkpoint);
    }

    [Fact]
    public void A_run_with_no_checkpoint_starts_measuring_instead_of_offering()
    {
        var (shape, checkpoint) = ShapeOf(90_000).Offer(5, null);

        Assert.False(shape.SplitSuggested);
        Assert.Equal(90_000, checkpoint);
    }

    /// <summary>
    /// The Approach of every Shape block in the stopped run 20261028-115553-01bf54 (concurrency-hunter
    /// 5d H2), with the plan-review rounds run when it was measured. Its Flow log said "Offer a split:
    /// yes" after all but the first, and no question was ever asked.
    /// </summary>
    [Fact]
    public void Replaying_the_approach_series_of_run_01bf54_offers_exactly_four_splits()
    {
        (int Rounds, int Approach)[] series =
        [
            (0, 21_649), (1, 34_730), (2, 42_464), (3, 50_900), (4, 57_513), (5, 60_499), (6, 68_666),
            (7, 70_787), (7, 70_786), (7, 95_760), (7, 95_780), (7, 96_340), (8, 126_826), (8, 126_929),
            (9, 160_354), (9, 163_278), (10, 178_011), (10, 187_343), (10, 188_132), (11, 215_335),
            (12, 254_374), (12, 255_164), (13, 269_607), (14, 287_084), (15, 296_071), (15, 296_089),
            (16, 303_265), (17, 309_596), (17, 287_264), (17, 287_810), (18, 292_311), (19, 327_748),
            (19, 328_572), (19, 328_848), (20, 355_333), (20, 355_732)
        ];
        int? checkpoint = null;
        var offers = new List<string>();

        foreach (var (rounds, approach) in series)
        {
            var (shape, next) = ShapeOf(approach).Offer(rounds, checkpoint);
            checkpoint = next;
            if (shape.SplitSuggested) offers.Add($"{approach}:{shape.SplitReason}");
        }

        Assert.Equal(36, series.Length);
        Assert.Equal(["34730:crossed", "70787:doubled", "160354:doubled", "327748:doubled"], offers);
    }

    [Fact]
    public async Task A_write_that_doubles_the_approach_after_round_one_offers_a_split_and_keeps_the_approval_withdrawn()
    {
        var run = NewRun(reviewRounds: 1, approved: true, checkpoint: 34_730);

        var json = await ForgeTools.WritePlan(SessionRoots.None, _workspace, run.RunId, PlanWithApproach(70_000),
                                              CancellationToken.None);
        var result = JsonSerializer.Deserialize(json, ForgeToolJson.Default.PlanWriteResult)!;

        Assert.True(result.Shape.SplitSuggested);
        Assert.Equal("doubled", result.Shape.SplitReason);
        Assert.Equal(34_730, result.Shape.SplitCheckpoint);
        Assert.Contains("- Offer a split: yes — doubled since 34,730", File.ReadAllText(run.FlowLogPath),
                        StringComparison.Ordinal);
        var state = run.ReadState();
        Assert.Equal(70_000, state.SplitCheckpoint);
        Assert.False(state.Approved);
    }

    [Fact]
    public async Task A_patch_offers_again_only_once_the_approach_has_doubled_since_the_last_offer()
    {
        var run = NewRun(reviewRounds: 7, approved: false, checkpoint: 70_787);
        var writtenJson = await ForgeTools.WritePlan(SessionRoots.None, _workspace, run.RunId,
                                                    PlanWithApproach(100_000), CancellationToken.None);
        var written = JsonSerializer.Deserialize(writtenJson, ForgeToolJson.Default.PlanWriteResult)!;
        Assert.False(written.Shape.SplitSuggested);
        Assert.Equal(70_787, run.ReadState().SplitCheckpoint);

        var doubled = await GrowTaskTwo(run, written.Outline!, by: 41_600);
        Assert.True(doubled.Shape.SplitSuggested);
        Assert.Equal("doubled", doubled.Shape.SplitReason);
        Assert.Equal(70_787, doubled.Shape.SplitCheckpoint);
        Assert.Equal(doubled.Shape.ApproachCharacters, run.ReadState().SplitCheckpoint);

        var next = await GrowTaskTwo(run, doubled.Outline, by: 100);
        Assert.False(next.Shape.SplitSuggested);
        Assert.Null(next.Shape.SplitReason);
        Assert.Equal(doubled.Shape.ApproachCharacters, run.ReadState().SplitCheckpoint);
        var offers = File.ReadLines(run.FlowLogPath)
                         .Where(line => line.StartsWith("- Offer a split:", StringComparison.Ordinal))
                         .ToArray();
        Assert.Equal(["- Offer a split: no", "- Offer a split: yes — doubled since 70,787", "- Offer a split: no"], offers);
    }

    private RunDirectory NewRun(int reviewRounds, bool approved, int? checkpoint)
    {
        const string runId = "20260101-000000-abcdef";
        var run = RunDirectory.Create(_workspace, runId);
        run.WriteState(new RunState(runId, _workspace, "Text", DateTimeOffset.Now, reviewRounds, 25,
                                    Approved: approved, SplitCheckpoint: checkpoint));
        return run;
    }

    private async Task<Mcp.PlanPatchResult> GrowTaskTwo(RunDirectory run, PlanOutline outline, int by)
    {
        var task = outline.Targets.Single(target => target.Kind == "task" &&
                                                    target.Label.StartsWith("2. B", StringComparison.Ordinal));
        var edit = new PlanEdit("replace", task.Id, task.Hash, "2. B " + new string('y', task.Characters - 5 + by));
        var json = await ForgeTools.PatchPlan(SessionRoots.None, _workspace, run.RunId, outline.PlanHash, [edit],
                                              CancellationToken.None);
        return JsonSerializer.Deserialize(json, ForgeToolJson.Default.PlanPatchResult)!;
    }

    private static PlanShape ShapeOf(int approachCharacters)
    {
        var plan = PlanWithApproach(approachCharacters);
        return PlanShape.Of(plan, PlanOutline.Of(plan));
    }

    private static string PlanWithApproach(int approachCharacters)
    {
        const string head = "## Approach\n1. A\n2. B ";
        return "# P\n" + head + new string('x', approachCharacters - head.Length);
    }
}
