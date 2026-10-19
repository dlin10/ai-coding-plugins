using System.Security.Cryptography;
using System.Text;
using PlanForge.Acts;
using Xunit;

namespace PlanForge.Tests;

public sealed class PlanOutlineTests
{
    [Fact]
    public void Concatenating_the_targets_reproduces_the_plan()
    {
        foreach (var plan in new[] { "", "\n\n", "Intro\n# Title\n## Approach\n\n1. Alpha\n2. Beta\n## Gates\nCheck" })
        foreach (var newline in new[] { "\n", "\r\n" })
        {
            var text = plan.Replace("\n", newline, StringComparison.Ordinal);
            var outline = PlanOutline.Of(text);
            Assert.Equal(text, string.Concat(outline.Targets.Select(target => text.Substring(target.Start, target.Length))));
            Assert.Equal(text.Length, outline.Characters);
        }
    }

    [Fact]
    public void The_hash_is_the_sha256_prefix_of_the_utf8_text()
    {
        const string text = "# План 😀\r\n";
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

        Assert.Equal("ba7816bf8f01cfea", PlanOutline.Hash("abc"));
        Assert.Equal(expected, PlanOutline.Hash(text));
        Assert.Equal(expected, PlanOutline.Of(text).PlanHash);
        Assert.Equal(text.Length, PlanOutline.Of(text).Characters);
        Assert.NotEqual(PlanOutline.Hash(text), PlanOutline.Hash(text.Replace("\r\n", "\n", StringComparison.Ordinal)));
    }

    [Fact]
    public void Ids_follow_document_order()
    {
        var outline = PlanOutline.Of("Intro\n# Same\n## Approach\n1. Alpha\n2. Beta\n## Same\n");

        Assert.Equal(new[] { "s0", "s1", "s2", "s3", "s4", "s5" }, outline.Targets.Select(target => target.Id));
    }

    [Fact]
    public void An_empty_preamble_is_not_listed()
    {
        var targets = PlanOutline.Of("# Title\nBody\n").Targets;

        Assert.Equal("heading", Assert.Single(targets).Kind);
        Assert.Equal(0, targets[0].Start);
        Assert.Empty(PlanOutline.Of("").Targets);
    }

    [Fact]
    public void A_blank_preamble_is_labelled_blank()
    {
        var preamble = PlanOutline.Of(" \n\t\n# Title\n").Targets[0];

        Assert.Equal("preamble", preamble.Kind);
        Assert.Equal("(blank)", preamble.Label);
        Assert.Equal(4, preamble.Length);
    }

    [Fact]
    public void A_plan_without_headings_is_one_preamble()
    {
        const string plan = "\n  Intro  \nA body\n";
        var target = Assert.Single(PlanOutline.Of(plan).Targets);

        Assert.Equal("preamble", target.Kind);
        Assert.Equal("Intro", target.Label);
        Assert.Equal(plan.Length, target.Length);
    }

    [Fact]
    public void A_heading_line_inside_a_fence_starts_a_target_as_the_build_sees_it()
    {
        var targets = PlanOutline.Of("# Title\n```markdown\n# Inside\n```\n").Targets;

        Assert.Equal(new[] { "# Title", "# Inside" }, targets.Select(target => target.Label));
        Assert.All(targets, target => Assert.Equal("heading", target.Kind));
    }

    [Fact]
    public void A_task_heading_inside_the_approach_stays_in_its_task()
    {
        const string plan = "## Approach\n1. Alpha\n### Detail\nBody\n2. Beta\n";
        var targets = PlanOutline.Of(plan).Targets;

        Assert.Equal(new[] { "heading", "task", "task" }, targets.Select(target => target.Kind));
        Assert.Contains("### Detail", plan.Substring(targets[1].Start, targets[1].Length), StringComparison.Ordinal);
    }

    [Fact]
    public void The_line_that_ends_the_approach_starts_a_target()
    {
        const string plan = "## Approach\n1. Alpha\n##\nTail\n";
        var target = PlanOutline.Of(plan).Targets[^1];

        Assert.Equal("heading", target.Kind);
        Assert.Equal("##", target.Label);
        Assert.Equal(plan.IndexOf("##\n", StringComparison.Ordinal), target.Start);
    }

    [Fact]
    public void Two_approach_headings_divide_by_headings_alone()
    {
        var targets = PlanOutline.Of("## Approach\n1. Alpha\n### Detail\n## Approach\n1. Beta\n").Targets;

        Assert.Equal(new[] { "## Approach", "### Detail", "## Approach" }, targets.Select(target => target.Label));
        Assert.All(targets, target => Assert.Equal("heading", target.Kind));
    }

    [Fact]
    public void Labels_are_trimmed_and_cut_to_eighty_characters()
    {
        var label = new string('x', 100);
        var outline = PlanOutline.Of($"\n  {label}  \n# {label}  \n## Approach\n   1. {label}  \n");

        Assert.Equal(label[..80], outline.Targets[0].Label);
        Assert.Equal(("# " + label)[..80], outline.Targets[1].Label);
        Assert.Equal(("1. " + label)[..80], outline.Targets[3].Label);
        Assert.All(outline.Targets, target => Assert.InRange(target.Label.Length, 1, 80));
    }

    [Fact]
    public void Locate_finds_nothing_without_exactly_one_approach_heading()
    {
        Assert.Null(PlanTasks.Locate("# Plan\n1. Alpha\n"));
        Assert.Null(PlanTasks.Locate("## Approach\n1. Alpha\n## Approach\n2. Beta\n"));
        Assert.NotNull(PlanTasks.Locate("## Approach\nNo tasks\n"));
        Assert.NotNull(PlanTasks.Locate("## Approach\n99999999999999999999. Large\n٢. Unicode\n"));
    }

    [Fact]
    public void Locate_offsets_start_lines_and_keep_crlf_pairs_whole()
    {
        const string plan = "# Title\r\n\r\n## Approach\r\n\r\n  1. Alpha\r\n\r\n\t2. Beta\r\n##\r\nTail";
        var layout = Assert.IsType<PlanTaskLayout>(PlanTasks.Locate(plan));

        Assert.Equal(plan.IndexOf("## Approach", StringComparison.Ordinal), layout.Start);
        Assert.Equal(plan.IndexOf("##\r\n", StringComparison.Ordinal), layout.End);
        Assert.Equal(new[] { plan.IndexOf("  1.", StringComparison.Ordinal), plan.IndexOf("\t2.", StringComparison.Ordinal) }, layout.TaskStarts);
        foreach (var offset in layout.TaskStarts.Append(layout.Start).Append(layout.End))
        {
            Assert.True(offset == 0 || plan[offset - 1] == '\n');
            Assert.NotEqual('\n', plan[offset]);
        }
    }

    [Fact]
    public void Locate_matches_tasks_as_the_build_does_on_crlf_text()
    {
        const string plan = "## Approach\r\n\r\n  1. Alpha\r\n\r\n\t2. Beta\r\n### Detail\r\n## Gates\r\n";
        var layout = Assert.IsType<PlanTaskLayout>(PlanTasks.Locate(plan));

        Assert.Equal(PlanTasks.Parse(plan).Count, layout.TaskStarts.Count);
        Assert.Equal(2, layout.TaskStarts.Count);
        Assert.StartsWith("  1. Alpha", plan[layout.TaskStarts[0]..], StringComparison.Ordinal);
        Assert.StartsWith("\t2. Beta", plan[layout.TaskStarts[1]..], StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_means_whitespace_after_leading_byte_order_marks()
    {
        foreach (var text in new string?[] { null, "", "\uFEFF", "\uFEFF\uFEFF \n", "\uFEFF\n\t" })
            Assert.True(PlanOutline.IsBlank(text));
        foreach (var text in new[] { "\uFEFF x", " \uFEFF" })
            Assert.False(PlanOutline.IsBlank(text));
    }
}
