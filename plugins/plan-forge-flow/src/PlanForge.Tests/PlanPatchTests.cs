using System.Text;
using System.Text.Json;
using PlanForge.Acts;
using Xunit;

namespace PlanForge.Tests;

public sealed class PlanPatchTests
{
    private const string Plan = "Intro\n# Brief\nBuild carefully.\n## Requirements\nR1\n## Approach\n1. First\n2. Second\n## Gates\nCheck\n";

    [Fact]
    public void Replaces_inserts_and_deletes_several_targets_at_once()
    {
        var edits = new[] { Edit(Plan, 1, "replace", "# Brief\nNew"), Edit(Plan, 2, "delete"),
            Edit(Plan, 6, "insertAfter", "## Notes\nExtra") };
        var result = Apply(Plan, edits);

        Assert.Equal("Intro\n# Brief\nNew\n## Approach\n1. First\n2. Second\n## Gates\nCheck\n## Notes\nExtra\n", result.Plan);
        Assert.Equal(new[] { "replace", "delete", "insertAfter" }, result.Changes.Select(change => change.Op));
        Assert.Equal(new[] { "s1", "s2", "s6" }, result.Changes.Select(change => change.Target));
        Assert.Equal(new[] { "# Brief", "## Requirements", "## Gates" }, result.Changes.Select(change => change.Label));
    }

    [Fact]
    public void Replaces_the_preamble_and_the_builder_brief()
    {
        var result = Apply(Plan, Edit(Plan, 0, "replace", "New intro"), Edit(Plan, 1, "replace", "# Brief\nNew instructions"));

        Assert.StartsWith("New intro\n# Brief\nNew instructions\n## Requirements\n", result.Plan, StringComparison.Ordinal);
        Assert.Equal(PlanTasks.Parse(Plan), PlanTasks.Parse(result.Plan));
    }

    [Fact]
    public void Untouched_targets_keep_their_bytes()
    {
        var plan = Plan.Replace("\n", "\r\n", StringComparison.Ordinal) + "## Tail\n odd \t\n";
        var result = Apply(plan, Edit(plan, 1, "replace", "# Brief\nRevised"));
        var before = PlanOutline.Of(plan);
        var after = PlanOutline.Of(result.Plan);

        foreach (var index in new[] { 0, 2, 3, 4, 5, 6, 7 })
            Assert.Equal(Encoding.UTF8.GetBytes(Text(plan, before.Targets[index])),
                Encoding.UTF8.GetBytes(Text(result.Plan, after.Targets[index])));
    }

    [Fact]
    public void Unicode_text_survives_a_patch()
    {
        var result = Apply(Plan, Edit(Plan, 0, "replace", "План 😀 שלום 日本語"));

        Assert.StartsWith("План 😀 שלום 日本語\n", result.Plan, StringComparison.Ordinal);
        Assert.Equal(PlanOutline.Hash(result.Plan), PlanOutline.Of(result.Plan).PlanHash);
    }

    [Fact]
    public void A_stale_base_hash_is_refused_with_the_current_hash()
    {
        var error = Assert.Throws<PlanPatchRejectedException>(() => PlanPatch.Apply(Plan, "0000000000000000", [Edit(Plan, 0)]));

        Assert.Equal("stale_base", error.Reason);
        Assert.Contains(PlanOutline.Hash(Plan), error.Message, StringComparison.Ordinal);
        Assert.Contains("forge.plan.outline", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Sizes.PlanBefore);
        Assert.Null(error.Sizes.Removed);
        Assert.Null(error.Sizes.Written);
        Assert.Null(error.Sizes.PlanAfter);
    }

    [Fact]
    public void A_stale_target_hash_is_refused()
    {
        var error = Reject(Plan, "stale_target", Edit(Plan, 0) with { TargetHash = "0000000000000000" });

        Assert.Contains("Edit 0, target s0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_target_is_refused()
    {
        Reject(Plan, "unknown_target", Edit(Plan, 0) with { Target = "s99" });
    }

    [Fact]
    public void A_null_edit_or_field_is_refused()
    {
        var valid = Edit(Plan, 0);
        foreach (var edit in new PlanEdit?[] { null, valid with { Op = null }, valid with { Target = null }, valid with { TargetHash = null } })
        {
            var error = Reject(Plan, "invalid_edit", edit);
            Assert.Contains("Edit 0", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_empty_edit_list_is_refused()
    {
        foreach (var edits in new IReadOnlyList<PlanEdit?>?[] { null, [] })
        {
            var error = Assert.Throws<PlanPatchRejectedException>(() => PlanPatch.Apply(Plan, null, edits));
            Assert.Equal("no_edits", error.Reason);
            Assert.NotNull(error.Sizes.PayloadSize);
            Assert.Equal(new PlanPatchSize(0, 0), error.Sizes.Supplied);
        }
    }

    [Fact]
    public void Inserting_a_task_without_renumbering_breaks_the_shape_and_is_refused()
    {
        Reject(Plan, "shape_broken", Edit(Plan, 4, "insertAfter", "2. Added"));
    }

    [Fact]
    public void Inserting_a_task_and_renumbering_the_rest_is_accepted()
    {
        var result = Apply(Plan, Edit(Plan, 4, "insertAfter", "2. Added"), Edit(Plan, 5, "replace", "3. Second"));

        Assert.Equal(new[] { 1, 2, 3 }, PlanTasks.Parse(result.Plan).Select(task => task.Number));
    }

    [Fact]
    public void Editing_across_requirements_approach_and_gates_boundaries()
    {
        var result = Apply(Plan, Edit(Plan, 2, "replace", "## Requirements\nNew R1"),
            Edit(Plan, 3, "replace", "## Approach\nSetup"), Edit(Plan, 6, "replace", "## Gates\nNew check"));

        Assert.Equal("Intro\n# Brief\nBuild carefully.\n## Requirements\nNew R1\n## Approach\nSetup\n1. First\n2. Second\n## Gates\nNew check\n", result.Plan);
    }

    [Fact]
    public void A_replayed_patch_is_a_stale_base()
    {
        var edits = new[] { Edit(Plan, 0) };
        var result = Apply(Plan, edits);
        var error = Assert.Throws<PlanPatchRejectedException>(() => PlanPatch.Apply(result.Plan, PlanOutline.Hash(Plan), edits));

        Assert.Equal("stale_base", error.Reason);
        Assert.Contains(PlanOutline.Hash(result.Plan), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_insertion_after_a_terminated_final_target_adds_no_extra_break()
    {
        var result = Apply(Plan, Edit(Plan, 6, "insertAfter", "## Tail\nDone\n"));

        Assert.Equal(Plan + "## Tail\nDone\n", result.Plan);
    }

    [Fact]
    public void Sizes_count_removed_supplied_and_written_text_in_characters_and_bytes()
    {
        var plan = Plan.Replace("\n", "\r\n", StringComparison.Ordinal);
        PlanEdit?[] edits = [Edit(plan, 0, "replace", "Новый 😀\nтекст"), Edit(plan, 2, "delete"),
            Edit(plan, 6, "insertAfter", "## 尾\n終\n")];
        var result = Apply(plan, edits);
        var json = JsonSerializer.Serialize<IReadOnlyList<PlanEdit?>>(edits, PlanForge.Mcp.ToolArgumentJson.ArgumentOptions);
        var removed = Text(plan, PlanOutline.Of(plan).Targets[0]) + Text(plan, PlanOutline.Of(plan).Targets[2]);

        AssertSize(json, result.Sizes.PayloadSize);
        AssertSize("Новый 😀\nтекст## 尾\n終\n", result.Sizes.Supplied);
        AssertSize(plan, result.Sizes.PlanBefore);
        AssertSize(removed, result.Sizes.Removed);
        AssertSize("Новый 😀\r\nтекст\r\n## 尾\r\n終\r\n", result.Sizes.Written);
        AssertSize(result.Plan, result.Sizes.PlanAfter);
        Assert.NotEqual(result.Sizes.Supplied, result.Sizes.Written);
    }

    [Fact]
    public void A_leading_byte_order_mark_never_reaches_the_result()
    {
        var result = Apply(Plan, Edit(Plan, 0, "replace", "\uFEFF\uFEFFNew intro"));

        Assert.StartsWith("New intro\n", result.Plan, StringComparison.Ordinal);
        AssertSize("New intro\n", result.Sizes.Written);
        AssertSize(result.Plan, result.Sizes.PlanAfter);
    }

    [Fact]
    public void A_shape_refusal_quotes_no_plan_text()
    {
        var digits = new string('\u0661', 90);
        var error = Reject(Plan, "shape_broken", Edit(Plan, 4, "replace", digits + ". Private content"));

        Assert.DoesNotContain(digits, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Private content", error.Message, StringComparison.Ordinal);
        Assert.Contains("unreadable task number", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.NotNull(error.Sizes.PlanAfter);
    }

    [Fact]
    public void Malformed_supplied_values_are_described_by_length()
    {
        const string paragraph = "Private content pasted into the wrong field must never escape into an answer.\nSecond private line longer than eighty characters.";
        var valid = Edit(Plan, 0);
        var errors = new[]
        {
            Assert.Throws<PlanPatchRejectedException>(() => PlanPatch.Apply(Plan, paragraph, [valid])),
            Reject(Plan, "unknown_target", valid with { Target = paragraph }),
            Reject(Plan, "stale_target", valid with { TargetHash = paragraph }),
            Reject(Plan, "invalid_edit", valid with { Op = paragraph })
        };

        foreach (var error in errors)
        {
            Assert.Contains($"of {paragraph.Length} characters", error.Message, StringComparison.Ordinal);
            foreach (var line in paragraph.Split('\n')) Assert.DoesNotContain(line, error.Message, StringComparison.Ordinal);
        }
        Assert.Equal("s12", PlanPatch.Shown("s12", PlanValueKind.Id));
        Assert.Equal("a malformed ID of 2 characters", PlanPatch.Shown("s١", PlanValueKind.Id));
        Assert.Equal("a missing hash", PlanPatch.Shown(null, PlanValueKind.Hash));
    }

    [Fact]
    public void Validation_checks_single_edits_before_pairwise_conflicts()
    {
        Reject(Plan, "invalid_edit", Edit(Plan, 0), Edit(Plan, 0), Edit(Plan, 1) with { Content = " " });
    }

    [Fact]
    public void Change_summaries_carry_only_base_labels_bounded_to_eighty_characters()
    {
        var plan = "# " + new string('x', 120) + "\nSecret body\n";
        var result = Apply(plan, Edit(plan, 0, "replace", "# New\nOther private body"));
        var change = Assert.Single(result.Changes);

        Assert.Equal(plan[..80], change.Label);
        Assert.Equal("s0", change.Target);
        Assert.Equal("replace", change.Op);
    }

    internal static PlanEdit Edit(string plan, int index, string op = "replace", string? content = "Updated")
    {
        var target = PlanOutline.Of(plan).Targets[index];
        return new PlanEdit(op, target.Id, target.Hash, op == "delete" ? null : content);
    }

    internal static PlanPatchResult Apply(string plan, params PlanEdit?[] edits) =>
        PlanPatch.Apply(plan, PlanOutline.Hash(plan), edits);

    internal static PlanPatchRejectedException Reject(string plan, string reason, params PlanEdit?[] edits)
    {
        var error = Assert.Throws<PlanPatchRejectedException>(() => Apply(plan, edits));
        Assert.Equal(reason, error.Reason);
        return error;
    }

    private static string Text(string plan, PlanTarget target) => plan.Substring(target.Start, target.Length);

    private static void AssertSize(string text, PlanPatchSize? size) =>
        Assert.Equal(new PlanPatchSize(text.Length, Encoding.UTF8.GetByteCount(text)), size);
}
