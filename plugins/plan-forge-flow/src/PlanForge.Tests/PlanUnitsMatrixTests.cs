using PlanForge.Acts;
using PlanForge.Evidence;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Exercises every combination of section-entry recognition and outline splitting.</summary>
public sealed class PlanUnitsMatrixTests
{
    /// <summary>The real section placed first in a cell's plan.</summary>
    private enum Section
    {
        /// <summary>The Requirements section.</summary>
        Requirements,
        /// <summary>The Gates section.</summary>
        Gates
    }

    /// <summary>The label spellings and placements recognized or rejected by the grammar.</summary>
    private enum EntryForm
    {
        /// <summary>A period inside the bold entry label.</summary>
        DotInside,
        /// <summary>A colon inside the bold entry label.</summary>
        ColonInside,
        /// <summary>A colon after the bold entry label.</summary>
        ColonOutside,
        /// <summary>An entry without a numbered list prefix.</summary>
        Unnumbered,
        /// <summary>An entry with a bullet prefix.</summary>
        Bulleted,
        /// <summary>A label written in lowercase.</summary>
        LowerCase,
        /// <summary>An entry inside a fenced block.</summary>
        Fenced,
        /// <summary>A label after other text on a line.</summary>
        LaterInLine
    }

    /// <summary>The text between the first entry and the cell's entry.</summary>
    private enum Preceding
    {
        /// <summary>No intervening text.</summary>
        Nothing,
        /// <summary>An intervening deeper heading.</summary>
        Subheading,
        /// <summary>A heading-like line inside a fenced block.</summary>
        FencedHeading
    }

    /// <summary>One combination of all the independent axes.</summary>
    /// <param name="Section">The section appearing first.</param>
    /// <param name="Form">The second entry's form in both sections.</param>
    /// <param name="Preceding">The text preceding the second entry in both sections.</param>
    /// <param name="Newline">The original plan's line ending.</param>
    /// <param name="Repeated">Whether the second label repeats the first.</param>
    private sealed record Cell(Section Section, EntryForm Form, Preceding Preceding, string Newline, bool Repeated);

    /// <summary>The complete Cartesian product, without sampling.</summary>
    private static readonly Cell[] Cells =
        (from section in Enum.GetValues<Section>()
         from form in Enum.GetValues<EntryForm>()
         from preceding in Enum.GetValues<Preceding>()
         from newline in new[] { "\n", "\r\n" }
         from repeated in new[] { false, true }
         select new Cell(section, form, preceding, newline, repeated)).ToArray();

    /// <summary>Every cell preserves exact text, offsets, unique ids and hashes.</summary>
    [Fact]
    public void Every_cell_partitions_the_plan_exactly()
    {
        foreach (var cell in Cells)
        {
            var plan = Plan(cell);
            var units = PlanUnits.Of(plan);
            Assert.Equal(plan, string.Concat(units.Select(unit => unit.Text)));
            Assert.Equal(units.Count, units.Select(unit => unit.Id).Distinct(StringComparer.Ordinal).Count());
            var position = 0;
            foreach (var unit in units)
            {
                Assert.Equal(position, unit.Start);
                Assert.Equal(unit.Text.Length, unit.Length);
                Assert.Equal(plan.Substring(position, unit.Length), unit.Text);
                Assert.Equal(PlanOutline.Hash(unit.Text), unit.Hash);
                position += unit.Length;
            }
            Assert.Equal(plan.Length, position);
        }
    }

    /// <summary>Only numbered uppercase labels in the first three forms start a new unit.</summary>
    [Fact]
    public void Every_cell_gets_its_expected_units()
    {
        foreach (var cell in Cells)
        {
            var units = PlanUnits.Of(Plan(cell));
            var startsUnit = cell.Form is EntryForm.DotInside or EntryForm.ColonInside or EntryForm.ColonOutside;
            var extraOutlineTarget = cell.Preceding == Preceding.Nothing ? 0 : 1;
            var expected = new List<(string Id, string Kind)> { ("s0", "other") };
            var sections = cell.Section == Section.Requirements
                ? new[] { Section.Requirements, Section.Gates }
                : new[] { Section.Gates, Section.Requirements };
            for (var index = 0; index < sections.Length; index++)
            {
                var prefix = sections[index] == Section.Requirements ? "R" : "G";
                var kind = sections[index] == Section.Requirements ? "requirement" : "gate";
                expected.Add(($"s{1 + index * (1 + extraOutlineTarget)}", "other"));
                expected.Add(($"{prefix}1", kind));
                if (startsUnit) expected.Add(($"{prefix}{(cell.Repeated ? "1~2" : "2")}", kind));

                var first = Assert.Single(units, unit => unit.Id == $"{prefix}1");
                var preceding = Before(cell.Preceding).Replace("\n", cell.Newline, StringComparison.Ordinal);
                Assert.Contains(preceding, first.Text, StringComparison.Ordinal);
                var entry = Entry(cell.Form, prefix, cell.Repeated).Replace("\n", cell.Newline, StringComparison.Ordinal);
                if (startsUnit)
                    Assert.Equal(entry, Assert.Single(units, unit => unit.Id == $"{prefix}{(cell.Repeated ? "1~2" : "2")}").Text);
                else
                    Assert.EndsWith(entry, first.Text, StringComparison.Ordinal);
            }
            expected.Add(($"s{3 + 2 * extraOutlineTarget}", "other"));
            expected.Add(("T1", "task"));
            expected.Add(("T2", "task"));
            expected.Add(($"s{6 + 2 * extraOutlineTarget}", "other"));
            Assert.True(expected.SequenceEqual(units.Select(unit => (unit.Id, unit.Kind))), cell.ToString());
        }
    }

    /// <summary>All values of every axis occur, and every combination is present exactly once.</summary>
    [Fact]
    public void Every_axis_value_occurs_in_some_cell()
    {
        Assert.Equal(Enum.GetValues<Section>(), Cells.Select(cell => cell.Section).Distinct().ToArray());
        Assert.Equal(Enum.GetValues<EntryForm>(), Cells.Select(cell => cell.Form).Distinct().ToArray());
        Assert.Equal(Enum.GetValues<Preceding>(), Cells.Select(cell => cell.Preceding).Distinct().ToArray());
        Assert.Equal(new[] { "\n", "\r\n" }, Cells.Select(cell => cell.Newline).Distinct().ToArray());
        Assert.Equal(new[] { false, true }, Cells.Select(cell => cell.Repeated).Distinct().ToArray());
        Assert.Equal(192, Cells.Length);
        Assert.Equal(Cells.Length, Cells.Distinct().Count());
    }

    /// <summary>Builds both sections, followed by two outline tasks and a closing section.</summary>
    /// <param name="cell">The axis values determining the plan.</param>
    private static string Plan(Cell cell)
    {
        var requirements = "## Requirements\n1. **R1.** first\n" + Before(cell.Preceding) + Entry(cell.Form, "R", cell.Repeated);
        var gates = "## Gates\n1. **G1.** first\n" + Before(cell.Preceding) + Entry(cell.Form, "G", cell.Repeated);
        var sections = cell.Section == Section.Requirements ? requirements + gates : gates + requirements;
        return ("Preamble\n" + sections + "## Approach\n1. First task\n2. Second task\n## Closing\nEnd\n")
            .Replace("\n", cell.Newline, StringComparison.Ordinal);
    }

    /// <summary>Supplies the text that must remain inside the first entry's unit.</summary>
    /// <param name="preceding">The preceding-text axis value.</param>
    private static string Before(Preceding preceding) => preceding switch
    {
        Preceding.Nothing => "",
        Preceding.Subheading => "### Notes\nDetails\n",
        Preceding.FencedHeading => "```markdown\n## Notes\n```\n",
        _ => throw new ArgumentOutOfRangeException(nameof(preceding))
    };

    /// <summary>Spells one candidate entry without consulting the production patterns.</summary>
    /// <param name="form">The entry's form or placement.</param>
    /// <param name="prefix">R for requirements or G for gates.</param>
    /// <param name="repeated">Whether its label repeats the first entry.</param>
    private static string Entry(EntryForm form, string prefix, bool repeated)
    {
        var label = prefix + (repeated ? "1" : "2");
        return form switch
        {
            EntryForm.DotInside => $"2. **{label}.** next\n",
            EntryForm.ColonInside => $"2. **{label}:** next\n",
            EntryForm.ColonOutside => $"2. **{label}**: next\n",
            EntryForm.Unnumbered => $"**{label}.** next\n",
            EntryForm.Bulleted => $"- **{label}.** next\n",
            EntryForm.LowerCase => $"2. **{label.ToLowerInvariant()}.** next\n",
            EntryForm.Fenced => $"```markdown\n2. **{label}.** next\n```\n",
            EntryForm.LaterInLine => $"mention 2. **{label}.** next\n",
            _ => throw new ArgumentOutOfRangeException(nameof(form))
        };
    }
}
