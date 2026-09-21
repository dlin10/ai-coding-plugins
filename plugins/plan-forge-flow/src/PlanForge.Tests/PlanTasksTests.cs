using PlanForge.Acts;
using Xunit;

namespace PlanForge.Tests;

public sealed class PlanTasksTests
{
    private const string Plan =
        """
        # Title

        ## Context

        Numbered lines here must not be mistaken for tasks.

        ## Approach

        1. First task.
        2. Second task,
           continued on another line.

        ## Verification

        1. Not a task.
        """;

    [Fact]
    public void Parses_the_numbered_tasks_of_the_approach_section()
    {
        var tasks = PlanTasks.Parse(Plan);

        Assert.Equal(2, tasks.Count);
        Assert.Equal("First task.", tasks[0].Text);
        Assert.Contains("continued on another line.", tasks[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_plan_without_an_approach_section() =>
        Assert.Throws<PlanShapeException>(() => PlanTasks.Parse("# Title\n\n1. Task.\n"));

    [Fact]
    public void Rejects_tasks_that_are_not_numbered_in_order() =>
        Assert.Throws<PlanShapeException>(() => PlanTasks.Parse("## Approach\n\n1. One.\n3. Three.\n"));

    [Fact]
    public void Rejects_an_approach_section_with_no_tasks() =>
        Assert.Throws<PlanShapeException>(() => PlanTasks.Parse("## Approach\n\nProse only.\n"));

    [Fact]
    public void Builder_brief_plan_LF_extraction_excludes_heading_and_tasks()
    {
        const string plan = "# Title\n\n## Approach\n\n1. First task.\n";

        Assert.Equal("# Title\n\n", PlanTasks.Brief(plan));
        Assert.Equal(string.Empty, PlanTasks.Brief("## Approach\n\n1. First task.\n"));
    }

    [Fact]
    public void Builder_brief_plan_CRLF_extraction_preserves_line_endings()
    {
        const string plan = "# Title\r\n\r\n## Approach\r\n\r\n1. First task.\r\n";

        Assert.Equal("# Title\r\n\r\n", PlanTasks.Brief(plan));
    }
}
