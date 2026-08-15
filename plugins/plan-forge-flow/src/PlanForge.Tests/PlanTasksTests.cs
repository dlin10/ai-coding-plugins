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
}
