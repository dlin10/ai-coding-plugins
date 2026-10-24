using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Acts;
using PlanForge.Advisors;
using PlanForge.Evidence;
using PlanForge.Infrastructure;
using PlanForge.Mcp;
using PlanForge.Prompts;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// Once a Run has used the Evidence map, a plan changed after a review round that no complete map
/// call has followed is warned about by the plan writers and by the next round. Nothing refuses.
/// </summary>
public sealed class EvidenceMapCurrencyTests
{
    /// <summary>A plan the fixture's map has never judged.</summary>
    private const string WIDER_PLAN = "## Approach\n\n1. Build WidgetApi and WidgetCli.\n";

    /// <summary>A third plan, written after the map has judged the second one.</summary>
    private const string NARROWER_PLAN = "## Approach\n\n1. Build WidgetCli.\n";

    /// <summary>The Flow heading of a round that reviewed a plan the map had not seen.</summary>
    private const string FLOW_HEADING = "## Evidence map not current";

    /// <summary>A Run that never called the map is not warned, however its plan and rounds move.</summary>
    [Fact]
    public async Task A_run_that_never_used_the_map_is_not_warned()
    {
        using var fixture = new EvidenceToolRun();
        Assert.Null(await Review(fixture));
        Assert.Null(await Write(fixture, WIDER_PLAN));
        Assert.Null(await Review(fixture));
        Assert.DoesNotContain(FLOW_HEADING, AtomicFile.Read(fixture.Run.FlowLogPath));
    }

    /// <summary>Patching the plan in answer to the map, before the round it precedes, is the protocol.</summary>
    [Fact]
    public async Task Changes_made_after_the_map_and_before_its_round_are_not_warned()
    {
        using var fixture = new EvidenceToolRun();
        await Map(fixture);
        Assert.Null(await Write(fixture, WIDER_PLAN));
        Assert.Null(await Patch(fixture, "1. Build WidgetApi, WidgetCli and WidgetDocs.\n"));
        Assert.Null(await Review(fixture));
    }

    /// <summary>A round leaves the map behind; every writer and the next round say so until it runs again.</summary>
    [Fact]
    public async Task A_plan_changed_after_a_round_is_warned_until_the_map_runs_again()
    {
        using var fixture = new EvidenceToolRun();
        await Map(fixture);
        var mapped = PlanOutline.Hash(fixture.Run.ReadPlan());
        Assert.Null(await Review(fixture));

        var written = await Write(fixture, WIDER_PLAN);
        Assert.NotNull(written);
        Assert.Contains(mapped, written);
        Assert.Contains(PlanOutline.Hash(WIDER_PLAN), written);
        Assert.Contains("forge.evidence.map", written);
        Assert.NotNull(await Patch(fixture, "1. Build WidgetApi, WidgetCli and WidgetDocs.\n"));

        Assert.NotNull(await Review(fixture));
        Assert.Contains(FLOW_HEADING + " — plan review round 2", AtomicFile.Read(fixture.Run.FlowLogPath));

        await Map(fixture);
        Assert.Null(await Write(fixture, NARROWER_PLAN));
        Assert.Null(await Review(fixture));
        Assert.Equal(2, AtomicFile.Read(fixture.Run.FlowLogPath).Split(FLOW_HEADING).Length);
    }

    /// <summary>A round that leaves the plan as the map saw it needs no second map.</summary>
    [Fact]
    public async Task An_unchanged_plan_needs_no_new_map()
    {
        using var fixture = new EvidenceToolRun();
        await Map(fixture);
        Assert.Null(await Review(fixture));
        Assert.Null(await Review(fixture));
        Assert.DoesNotContain(FLOW_HEADING, AtomicFile.Read(fixture.Run.FlowLogPath));
    }

    /// <summary>A map that was called but never completed is a map the plan has not had.</summary>
    [Fact]
    public async Task A_map_that_never_completed_is_warned()
    {
        using var fixture = new EvidenceToolRun();
        fixture.Advisor.Handler = (_, _) => throw new AdvisorException(AdvisorFailureKind.Unavailable);
        Assert.False(JsonNode.Parse(await fixture.Call())!["complete"]!.GetValue<bool>());

        var warning = await Write(fixture, WIDER_PLAN);
        Assert.NotNull(warning);
        Assert.Contains("complete", warning);
    }

    /// <summary>The stored plan mark is untrusted input like the rest of the file.</summary>
    [Fact]
    public void A_malformed_plan_mark_is_refused()
    {
        using var fixture = new EvidenceToolRun();
        var store = EvidenceStore.Load(fixture.Run.EvidenceMapPath);
        Assert.Throws<JsonException>(() => store.Write([], new(), new Dictionary<string, EvidenceItemMark>(), true,
                                                       new EvidencePlanMark("NOT-A-PLAN-HASH", 0)));
        Assert.Throws<JsonException>(() => store.Write([], new(), new Dictionary<string, EvidenceItemMark>(), true,
                                                       new EvidencePlanMark(PlanOutline.Hash(WIDER_PLAN), -1)));
        Assert.False(File.Exists(fixture.Run.EvidenceMapPath));
    }

    /// <summary>Runs one complete map over the fixture's current plan.</summary>
    /// <param name="fixture">The Run and its fake Advisor.</param>
    private static async Task Map(EvidenceToolRun fixture)
    {
        fixture.Advisor.Choose = question => FakeAdvisor.Valid(question, "insufficient_context");
        var result = JsonNode.Parse(await fixture.Call())!;
        Assert.True(result["complete"]!.GetValue<bool>());
    }

    /// <summary>Rewrites the plan through the tool and returns its warning, if any.</summary>
    /// <param name="fixture">The Run whose plan is replaced.</param>
    /// <param name="plan">The complete new plan.</param>
    private static async Task<string?> Write(EvidenceToolRun fixture, string plan) =>
        WarningOf(await ForgeTools.WritePlan(SessionRoots.None, fixture.Root, fixture.Run.RunId, plan, CancellationToken.None));

    /// <summary>Replaces the plan's one task through the tool and returns its warning, if any.</summary>
    /// <param name="fixture">The Run whose plan is patched.</param>
    /// <param name="task">The replacement task text.</param>
    private static async Task<string?> Patch(EvidenceToolRun fixture, string task)
    {
        var outline = PlanOutline.Of(fixture.Run.ReadPlan());
        var target = outline.Targets.Single(candidate => candidate.Kind == "task");
        return WarningOf(await ForgeTools.PatchPlan(SessionRoots.None, fixture.Root, fixture.Run.RunId, outline.PlanHash,
                                                    [new PlanEdit("replace", target.Id, target.Hash, task)], CancellationToken.None));
    }

    /// <summary>Runs one plan-review round as forge.plan.review does and returns its warning, if any.</summary>
    /// <param name="fixture">The Run under review.</param>
    private static async Task<string?> Review(EvidenceToolRun fixture)
    {
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new Critique("approve", [], "good"));
        var revision = fixture.Run.ReadState().ReviewRounds > 0 ? "revised" : null;
        var review = new PlanReview(vendor, new PromptLibrary());
        var critique = await review.ReviewAsync(fixture.Run, null, new Selection("critic", null), revision, null, false,
                                                CancellationToken.None);
        return WarningOf(EvidenceMapCurrency.Attach(JsonSerializer.Serialize(critique, ContractJson.Default.Critique),
                                                    review.EvidenceMapWarning));
    }

    /// <summary>Reads the optional warning beside a tool or act result.</summary>
    /// <param name="json">The serialized result.</param>
    private static string? WarningOf(string json) => JsonNode.Parse(json)!["evidenceMapWarning"]?.GetValue<string>();
}
