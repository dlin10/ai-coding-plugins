using System.Text;
using PlanForge.Acts;
using PlanForge.Evidence;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Checks plan-unit contracts beyond the section-entry matrix.</summary>
public sealed class PlanUnitsTests
{
    /// <summary>The skill's template yields section leads, obligations, exclusions, gates and tasks.</summary>
    [Fact]
    public void The_skill_plan_template_gives_the_expected_units()
    {
        const string plan = """
            # Work
            Builder: codex / model / high

            ## Requirements

            1. **R1.** What must be true once the work is done.
            2. **R2.** What must not change.

            **Out of scope.** What this run deliberately does not do.

            ## Gates

            1. **G1.** `dotnet test src/X.slnx --nologo` passes. (R1, R2)

            **Fix gate:** `dotnet test src/X.slnx --filter "FullyQualifiedName~FooTests"` (R1)

            ## Approach

            1. **First task.** What to change. **Gate:** `check` (R1)
            2. **Second task.** What to verify. **Gate:** `check` (R2)
            """;

        Assert.Equal(new[] { ("s0", "other"), ("s1", "other"), ("R1", "requirement"), ("R2", "requirement"),
                             ("X", "exclusion"), ("s2", "other"), ("G1", "gate"), ("F", "fixGate"),
                             ("s3", "other"), ("T1", "task"), ("T2", "task") },
                     PlanUnits.Of(plan).Select(unit => (unit.Id, unit.Kind)));
    }

    /// <summary>Task units have exactly the outline task targets' original boundaries.</summary>
    /// <param name="newline">The line ending used by the original plan.</param>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Task_boundaries_match_the_outline(string newline)
    {
        var plan = "Intro\n## Requirements\n1. **R1.** Keep\n## Approach\n\n  1. Alpha\n### Detail\nBody\n\n\t2. Beta\n## Closing\nEnd"
            .Replace("\n", newline, StringComparison.Ordinal);
        var targets = PlanOutline.Of(plan).Targets.Where(target => target.Kind == "task").ToArray();
        var units = PlanUnits.Of(plan).Where(unit => unit.Kind == "task").ToArray();

        Assert.Equal(2, units.Length);
        Assert.Equal(targets.Select(target => (target.Start, target.Length)), units.Select(unit => (unit.Start, unit.Length)));
        Assert.Equal(new[] { "T1", "T2" }, units.Select(unit => unit.Id));
    }

    /// <summary>Unrefined outline targets retain their original ids and become other units.</summary>
    [Fact]
    public void A_preamble_heading_and_two_tasks_keep_the_outline_ids()
    {
        const string plan = "Preamble\n# Heading\nBody\n## Approach\n1. Alpha\n2. Beta\n";

        Assert.Equal(new[] { ("s0", "other"), ("s1", "other"), ("s2", "other"), ("T1", "task"), ("T2", "task") },
                     PlanUnits.Of(plan).Select(unit => (unit.Id, unit.Kind)));
    }

    /// <summary>A heading inside a fenced example cannot select the Gates section.</summary>
    [Fact]
    public void A_fenced_gates_heading_before_the_real_section_is_not_chosen()
    {
        const string plan = "Preamble\n```markdown\n## Gates\n1. **G99.** example\n```\n## Gates\n1. **G1.** `real`\n**Fix gate:** `fix`\n";
        var units = PlanUnits.Of(plan);

        Assert.Equal(new[] { "G1", "F" }, units.Where(unit => unit.Kind is "gate" or "fixGate").Select(unit => unit.Id));
        Assert.Contains(units, unit => unit.Kind == "other" && unit.Text.Contains("G99", StringComparison.Ordinal));
        Assert.Equal("fix", PlanGates.FixGate(plan).Command);
    }

    /// <summary>Section discovery scans past tasks rather than inspecting only the Builder Brief.</summary>
    [Fact]
    public void Requirements_after_the_approach_end_it_and_are_refined()
    {
        const string plan = "## Approach\n1. Alpha\n2. Beta\n## Requirements\n1. **R1.** Later requirement\n";
        var units = PlanUnits.Of(plan);
        var headingStart = plan.IndexOf("## Requirements", StringComparison.Ordinal);

        Assert.Contains(PlanOutline.Of(plan).Targets, target => target.Start == headingStart);
        Assert.Equal(new[] { "s0", "T1", "T2", "s3", "R1" }, units.Select(unit => unit.Id));
        Assert.Equal(headingStart, units.Single(unit => unit.Id == "T2").Start + units.Single(unit => unit.Id == "T2").Length);
        Assert.Equal("requirement", units[^1].Kind);
    }

    /// <summary>Deeper headings do not end the Gates section or change either gate parser.</summary>
    [Fact]
    public void Gates_span_multiple_outline_targets_and_agree_with_both_gate_readers()
    {
        const string plan = "Preamble\n## Gates\n1. **G1.** `full1`\n### Verification\nDetails\n2. **G2.** `full2`\n**Fix gate:** `short`\n## Approach\n1. Task\n";
        var units = PlanUnits.Of(plan);

        Assert.Equal(2, PlanOutline.Of(plan).Targets.Count(target => target.Label is "## Gates" or "### Verification"));
        Assert.Equal(new[] { "G1", "G2", "F" }, units.Where(unit => unit.Kind is "gate" or "fixGate").Select(unit => unit.Id));
        Assert.Contains("### Verification\nDetails\n", units.Single(unit => unit.Id == "G1").Text, StringComparison.Ordinal);
        Assert.Equal(new[] { ("G1", "full1"), ("G2", "full2") }, PlanGates.RunWideGates(plan).Select(gate => (gate.Label, gate.Command)));
        Assert.Equal(new GateCommand("Fix gate", "short"), PlanGates.FixGate(plan));
        Assert.StartsWith("**Fix gate:** `short`", units.Single(unit => unit.Id == "F").Text, StringComparison.Ordinal);
    }

    /// <summary>A fenced level-two heading leaves subsequent requirements and exclusions in the section.</summary>
    [Fact]
    public void A_fenced_example_between_requirements_does_not_end_the_section()
    {
        const string plan = "## Requirements\n2. **R2.** Second\n```markdown\n## Example\n```\n3. **R3.** Third\n**Out of scope.** Excluded\n";
        var units = PlanUnits.Of(plan);

        Assert.Equal(new[] { ("s0", "other"), ("R2", "requirement"), ("R3", "requirement"), ("X", "exclusion") },
                     units.Select(unit => (unit.Id, unit.Kind)));
        Assert.EndsWith("```markdown\n## Example\n```\n", units.Single(unit => unit.Id == "R2").Text, StringComparison.Ordinal);
    }

    /// <summary>All four exclusion spellings start a unit only outside fences.</summary>
    [Fact]
    public void Exclusion_spellings_are_case_insensitive_and_ignore_fenced_labels()
    {
        foreach (var label in new[] { "**Out of scope.**", "**Out of scope:**", "**Out of scope**:", "**OUT OF SCOPE.**" })
        {
            var plan = $"## Requirements\n1. **R1.** Kept\n~~~markdown\n{label} Example\n~~~\n{label} Excluded\n";
            var units = PlanUnits.Of(plan);
            var exclusion = Assert.Single(units, unit => unit.Kind == "exclusion");

            Assert.Equal("X", exclusion.Id);
            Assert.Equal($"{label} Excluded\n", exclusion.Text);
            Assert.Contains($"{label} Example", units.Single(unit => unit.Id == "R1").Text, StringComparison.Ordinal);
        }
    }

    /// <summary>Repeated exclusion labels are disambiguated in document order.</summary>
    [Fact]
    public void Two_exclusion_blocks_get_unique_ids()
    {
        var units = PlanUnits.Of("## Requirements\n**Out of scope.** First\n**Out of scope:** Second\n");

        Assert.Equal(new[] { "X", "X~2" }, units.Where(unit => unit.Kind == "exclusion").Select(unit => unit.Id));
    }

    /// <summary>A fenced Fix gate label stays inside the unit before it.</summary>
    [Fact]
    public void A_fix_gate_label_inside_a_fence_starts_nothing()
    {
        const string plan = "## Gates\n1. **G1.** Full\n```markdown\n**Fix gate:** `example`\n```\n**Fix gate:** `real`\n";
        var units = PlanUnits.Of(plan);

        Assert.Equal("**Fix gate:** `real`\n", Assert.Single(units, unit => unit.Kind == "fixGate").Text);
        Assert.Contains("**Fix gate:** `example`", units.Single(unit => unit.Id == "G1").Text, StringComparison.Ordinal);
    }

    /// <summary>Labels use the first non-blank trimmed line and stop at eighty characters.</summary>
    [Fact]
    public void Labels_are_trimmed_and_cut_to_eighty_characters()
    {
        var longText = new string('x', 100);
        var units = PlanUnits.Of($"\n  {longText}  \n## Requirements\n  1. **R1.** {longText}  \n## Approach\n  1. {longText}  \n");

        Assert.Equal(longText[..80], units[0].Label);
        Assert.Equal(("1. **R1.** " + longText)[..80], units.Single(unit => unit.Id == "R1").Label);
        Assert.Equal(("1. " + longText)[..80], units.Single(unit => unit.Id == "T1").Label);
        Assert.All(units, unit => Assert.InRange(unit.Label.Length, 1, 80));
    }

    /// <summary>Units retain CRLFs, whitespace, Unicode and a final unterminated line exactly.</summary>
    [Fact]
    public void A_crlf_plan_keeps_its_exact_bytes_in_its_units()
    {
        const string plan = "  План 😀\r\n\r\n## Requirements\r\n\t1. **R1.** Keep\r\n### Details\r\nBody\r\n2. **R2:** Keep more\r\n**Out of scope**: Skip\r\n## Gates\r\n1. **G1.** `check`\r\n**Fix gate:** `fix`\r\n## Approach\r\n1. Alpha\r\n2. Beta";
        var units = PlanUnits.Of(plan);

        Assert.Equal(Encoding.UTF8.GetBytes(plan), units.SelectMany(unit => Encoding.UTF8.GetBytes(unit.Text)).ToArray());
        Assert.Equal(new[] { "s0", "s1", "R1", "R2", "X", "s3", "G1", "F", "s4", "T1", "T2" }, units.Select(unit => unit.Id));
        Assert.All(units, unit => Assert.Equal(plan.Substring(unit.Start, unit.Length), unit.Text));
    }

    /// <summary>Empty plans have no units and blank plans retain the outline's blank label.</summary>
    [Fact]
    public void Empty_and_blank_plans_remain_exact_partitions()
    {
        Assert.Empty(PlanUnits.Of(""));
        var blank = Assert.Single(PlanUnits.Of(" \n\t\r\n"));

        Assert.Equal(("s0", "other", "(blank)", " \n\t\r\n"), (blank.Id, blank.Kind, blank.Label, blank.Text));
    }

    /// <summary>Only the first real section is refined, and a level-one or level-two heading ends it.</summary>
    [Fact]
    public void Section_endings_and_later_duplicate_sections_keep_outline_targets()
    {
        foreach (var heading in new[] { "# End", "## End" })
        foreach (var section in new[] { "Requirements", "Gates" })
        {
            var prefix = section == "Requirements" ? "R" : "G";
            var plan = $"## {section}\n1. **{prefix}1.** First\n{heading}\n2. **{prefix}2.** Outside\n## {section}\n3. **{prefix}3.** Later\n";
            var units = PlanUnits.Of(plan);

            Assert.Single(units, unit => unit.Kind != "other");
            Assert.Equal($"{prefix}1", units[1].Id);
            Assert.Contains(units, unit => unit.Kind == "other" && unit.Text.Contains($"{prefix}2", StringComparison.Ordinal));
            Assert.Contains(units, unit => unit.Kind == "other" && unit.Text.Contains($"{prefix}3", StringComparison.Ordinal));
        }
    }

    /// <summary>Inline gate fences share the gate parser's character and minimum-length rule.</summary>
    [Fact]
    public void Inline_gate_fences_hide_headings_and_labels_until_the_matching_close()
    {
        const string plan = "## Gates\n1. **G1.** ````markdown\n## Example\n```\n2. **G99.** Example\n`````\n2. **G2.** `full`\n**Fix gate:** ~~~~ps\n## Also example\n~~~\n**Fix gate:** ignored\n~~~~\n## Closing\n";
        var units = PlanUnits.Of(plan);

        Assert.Equal(new[] { "G1", "G2", "F" }, units.Where(unit => unit.Kind is "gate" or "fixGate").Select(unit => unit.Id));
        Assert.Contains("G99", units.Single(unit => unit.Id == "G1").Text, StringComparison.Ordinal);
        Assert.Contains("**Fix gate:** ignored", units.Single(unit => unit.Id == "F").Text, StringComparison.Ordinal);
        Assert.Equal("## Also example\n~~~\n**Fix gate:** ignored", PlanGates.FixGate(plan).Command);
    }

    /// <summary>Requirements discovery follows gate-command fences even before its own section.</summary>
    [Fact]
    public void Requirements_inside_an_earlier_gate_command_fence_are_not_chosen()
    {
        foreach (var label in new[] { "1. **G1.**", "**Fix gate:**" })
        {
            var plan = $"## Gates\n{label} ````markdown\n## Requirements\n1. **R99.** Example\n`````\n## Requirements\n1. **R1.** Real\n";
            var units = PlanUnits.Of(plan);

            Assert.Equal("R1", Assert.Single(units, unit => unit.Kind == "requirement").Id);
            Assert.Contains(units, unit => unit.Kind is "gate" or "fixGate" && unit.Text.Contains("R99", StringComparison.Ordinal));
            Assert.Equal(plan, string.Concat(units.Select(unit => unit.Text)));
        }
    }

    /// <summary>A section with no entries still keeps its entire lead text as one unit.</summary>
    [Fact]
    public void A_section_without_entries_keeps_one_lead_unit_across_subheadings()
    {
        const string plan = "## Requirements\nLead\n### Notes\nMore\n## Gates\nLead\n### Verification\nMore\n";
        var units = PlanUnits.Of(plan);

        Assert.Equal(new[] { "s0", "s2" }, units.Select(unit => unit.Id));
        Assert.All(units, unit => Assert.Equal("other", unit.Kind));
        Assert.Equal(plan, string.Concat(units.Select(unit => unit.Text)));
    }
}
