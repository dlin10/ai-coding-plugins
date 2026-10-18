using System.Text.RegularExpressions;
using PlanForge.Acts;
using Xunit;

namespace PlanForge.Tests;

public sealed class PlanOutlineMatrixTests
{
    private enum Feature
    {
        NoHeading, PreambleAndHeadings, BlankPreamble, StartsWithHeading, DuplicateHeadings,
        Subheading, FencedHeading, UnspacedHeading, IndentedHeading, TaskSubheading,
        IndentedTasks, FencedSectionEnd, FencedTask, BareSectionEnd, NonBreakingSectionEnd,
        BareSectionEndAtEof, BareTaskAtEof, SpacedTaskAtEof, BareTaskBeforeGates,
        SpacedTaskBeforeTask, TwoApproaches, AppendixAfterApproach, NoFinalNewline,
        Unicode, NumberedListOutsideApproach
    }

    private sealed record Row(Feature Feature, string Plan, (string Kind, string Label)[] Targets, int? ParsedTasks = null);

    private static readonly string[] LineBreaks = ["\n", "\r\n"];
    private static readonly Row[] Rows =
    [
        new(Feature.NoHeading, "Intro\nBody\n", [("preamble", "Intro")]),
        new(Feature.PreambleAndHeadings, "Intro\n# Title\nBody\n## Context\nMore\n",
            [("preamble", "Intro"), ("heading", "# Title"), ("heading", "## Context")]),
        new(Feature.BlankPreamble, "\n \t\n# Title\n", [("preamble", "(blank)"), ("heading", "# Title")]),
        new(Feature.StartsWithHeading, "# Title\nBody\n", [("heading", "# Title")]),
        new(Feature.DuplicateHeadings, "# Same\nA\n# Same\nB\n", [("heading", "# Same"), ("heading", "# Same")]),
        new(Feature.Subheading, "## Context\nA\n### Detail\nB\n", [("heading", "## Context"), ("heading", "### Detail")]),
        new(Feature.FencedHeading, "# Title\n```\n# Inside\n```\n", [("heading", "# Title"), ("heading", "# Inside")]),
        new(Feature.UnspacedHeading, "#text\nBody\n", [("preamble", "#text")]),
        new(Feature.IndentedHeading, " # Title\nBody\n", [("preamble", "# Title")]),
        new(Feature.TaskSubheading, "## Approach\n1. Alpha\n### Detail\nBody\n2. Beta\n",
            [("heading", "## Approach"), ("task", "1. Alpha"), ("task", "2. Beta")], 2),
        new(Feature.IndentedTasks, "## Approach\n\n  1. Alpha\n\n\t2. Beta\n\n",
            [("heading", "## Approach"), ("task", "1. Alpha"), ("task", "2. Beta")], 2),
        new(Feature.FencedSectionEnd, "## Approach\n1. Alpha\n```\n## Inside\n```\n2. Beta\n",
            [("heading", "## Approach"), ("task", "1. Alpha"), ("heading", "## Inside")], 1),
        new(Feature.FencedTask, "## Approach\n1. Alpha\n2. Beta\n3. Gamma\n4. Delta\n5. Epsilon\n6. Zeta\n```\n7. step\n```\n8. Omega\n",
            [("heading", "## Approach"), ("task", "1. Alpha"), ("task", "2. Beta"), ("task", "3. Gamma"),
             ("task", "4. Delta"), ("task", "5. Epsilon"), ("task", "6. Zeta"), ("task", "7. step"), ("task", "8. Omega")], 8),
        new(Feature.BareSectionEnd, "## Approach\n1. Alpha\n##\nTail\n",
            [("heading", "## Approach"), ("task", "1. Alpha"), ("heading", "##")], 1),
        new(Feature.NonBreakingSectionEnd, "## Approach\n1. Alpha\n##\u00A0\nTail\n",
            [("heading", "## Approach"), ("task", "1. Alpha"), ("heading", "##")], 1),
        new(Feature.BareSectionEndAtEof, "## Approach\n1. Alpha\n##",
            [("heading", "## Approach"), ("task", "1. Alpha")], 1),
        new(Feature.BareTaskAtEof, "## Approach\n1. Alpha\n2.",
            [("heading", "## Approach"), ("task", "1. Alpha")], 1),
        new(Feature.SpacedTaskAtEof, "## Approach\n1. Alpha\n2. ",
            [("heading", "## Approach"), ("task", "1. Alpha")], 1),
        new(Feature.BareTaskBeforeGates, "## Approach\n1. Alpha\n2.\n\n## Gates\n",
            [("heading", "## Approach"), ("task", "1. Alpha"), ("heading", "## Gates")], 1),
        new(Feature.SpacedTaskBeforeTask, "## Approach\n1. Alpha\n2. \n3. Omega",
            [("heading", "## Approach"), ("task", "1. Alpha"), ("task", "2.")], 2),
        new(Feature.TwoApproaches, "## Approach\n1. Alpha\n### Detail\n## Approach\n1. Beta\n",
            [("heading", "## Approach"), ("heading", "### Detail"), ("heading", "## Approach")]),
        new(Feature.AppendixAfterApproach, "## Approach\n1. Alpha\n# Appendix\nBody\n",
            [("heading", "## Approach"), ("task", "1. Alpha")], 1),
        new(Feature.NoFinalNewline, "# Title\n## Context\nBody", [("heading", "# Title"), ("heading", "## Context")]),
        new(Feature.Unicode, "# План 😀\n## Approach\n1. Задача 🚀\n2. Проверка 🧪\n",
            [("heading", "# План 😀"), ("heading", "## Approach"), ("task", "1. Задача 🚀"), ("task", "2. Проверка 🧪")], 2),
        new(Feature.NumberedListOutsideApproach, "# Title\n1. Outside\n2. Also outside\n## Context\n1. Again\n",
            [("heading", "# Title"), ("heading", "## Context")])
    ];

    [Fact]
    public void Every_cell_partitions_the_plan_exactly()
    {
        foreach (var row in Rows)
        foreach (var newline in LineBreaks)
        {
            var plan = row.Plan.Replace("\n", newline, StringComparison.Ordinal);
            var outline = PlanOutline.Of(plan);
            Assert.Equal(row.Targets, outline.Targets.Select(target => (target.Kind, target.Label)));
            Assert.Equal(plan, string.Concat(outline.Targets.Select(target => plan.Substring(target.Start, target.Length))));
            Assert.Equal(plan.Length, outline.Characters);
            var position = 0;
            foreach (var target in outline.Targets)
            {
                Assert.Equal(position, target.Start);
                Assert.True(target.Start == 0 || plan[target.Start - 1] == '\n', $"{row.Feature}, {newline.Length}: {target.Id}");
                Assert.False(target.Start > 0 && plan[target.Start] == '\n' && plan[target.Start - 1] == '\r');
                Assert.Equal(target.Length, target.Characters);
                Assert.Equal(PlanOutline.Hash(plan.Substring(target.Start, target.Length)), target.Hash);
                position += target.Length;
                Assert.False(position < plan.Length && plan[position] == '\n' && plan[position - 1] == '\r');
            }
            Assert.Equal(plan.Length, position);
        }
    }

    [Fact]
    public void Every_task_target_is_the_task_the_build_parses()
    {
        foreach (var row in Rows)
        foreach (var newline in LineBreaks)
        {
            if (row.ParsedTasks is null) continue;
            var plan = row.Plan.Replace("\n", newline, StringComparison.Ordinal);
            var tasks = PlanTasks.Parse(plan);
            Assert.Equal(row.ParsedTasks.Value, tasks.Count);

            var targets = PlanOutline.Of(plan).Targets.Where(target => target.Kind == "task").ToArray();
            Assert.Equal(tasks.Count, targets.Length);
            for (var index = 0; index < tasks.Count; index++)
            {
                var text = plan.Substring(targets[index].Start, targets[index].Length).Replace("\r\n", "\n", StringComparison.Ordinal);
                var body = Regex.Replace(text, @"^\s*\d+\.\s+", "", RegexOptions.CultureInvariant);
                Assert.StartsWith(tasks[index].Text, body, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Every_axis_value_occurs_in_some_cell()
    {
        Assert.Equal(Enum.GetValues<Feature>().Order(), Rows.Select(row => row.Feature).Distinct().Order());
        Assert.Equal(25, Rows.Length);
        Assert.Equal(new[] { "\n", "\r\n" }, LineBreaks);
        var cells = (from row in Rows from newline in LineBreaks select (row.Feature, newline)).ToArray();
        Assert.Equal(50, cells.Length);
        Assert.Equal(50, cells.Distinct().Count());
    }
}
