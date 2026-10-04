using PlanForge.Prompts;
using Xunit;

namespace PlanForge.Tests;

public sealed class MatrixCheckSkillTests
{
    [Fact]
    public void The_matrix_check_is_made_while_the_approach_is_drafted_before_the_impact_pass()
    {
        Contains("### The matrix check", "before the Impact pass and before the first `forge.plan.write`",
                 "decide yourself whether the plan needs a matrix task", Skill());
        Contains("until the matrix check below, the Impact pass and the Evidence check",
                 "made its matrix check, and before the first `forge.plan.write`, ask the Scout one Impact pass", Skill());
    }

    [Fact]
    public void Every_rule_the_plan_introduces_or_changes_is_listed_with_its_axes()
    {
        Contains("List every rule the plan introduces or changes", "the choice of a name or a form",
                 "the order in which reasons are given", "the axes its answer depends on", Skill());
    }

    [Fact]
    public void A_rule_needs_a_matrix_when_any_of_four_conditions_holds()
    {
        Contains("A rule needs a matrix when any one of these holds",
                 "a combination of two or more axes, with about six or more values across them",
                 "the plan lists its cases one by one, and there are more than about ten",
                 "a neighbouring rule of the same kind already has a matrix in the codebase",
                 "a missed cell is a silent fault", Skill());
    }

    [Fact]
    public void A_needed_matrix_is_in_the_first_draft_and_the_first_show_says_what_was_decided()
    {
        Contains("gets its matrix task in the first draft", "Make the same check for a rule a later revision introduces",
                 "In the turn of the first `forge.plan.write`", "say in one line which rules you checked, what you decided for each and why",
                 Skill());
    }

    [Fact]
    public void The_matrix_task_names_its_axes_exclusions_and_an_expectation_table_from_the_requirements()
    {
        Contains("### The matrix task", "The critic judges a matrix task with the rest of the plan",
                 "its own test class in the ordinary suite", "never behind an environment flag",
                 "every excluded combination", "`Allowed`", "with the reason it is left out",
                 "writes the expectation table into the plan from the requirements", "citing its R-item",
                 "every branch of `Expected(cell)` cites a requirement, never the code under test", Skill());
    }

    [Fact]
    public void The_matrix_task_lists_wider_cells_by_name_and_fails_on_an_unknown_name()
    {
        Contains("the cells known to be wider", "each by name, with the reason", "never exact",
                 "a name on the list that matches no cell fails the test",
                 "becomes an open question in the project's own catalogue or design document", Skill());
    }

    [Fact]
    public void The_matrix_task_tests_narrowing_expectations_and_axis_coverage_and_replaces_listed_cases()
    {
        Contains("no cell narrows unsafely", "every cell off the wider list gets its expected answer",
                 "every value of every axis occurs in some cell", "replaces the one-by-one cases of the same rule",
                 "Only cases outside its axes stay listed by hand", Skill());
    }

    [Fact]
    public void Review_adds_a_missing_matrix_from_the_second_round_without_waiting_for_the_cap()
    {
        Contains("From the second round on", "more than half of them are combinations or edge cases of one rule",
                 "add one in this revision", "do not wait for the cap", "say so in `revision`", Skill());
    }

    [Fact]
    public void The_plan_review_critic_reports_combinations_of_one_rule_as_one_finding_recommending_a_matrix()
    {
        var prompts = new PromptLibrary(Path.Combine(RepositoryRoot(), "prompts"));
        Contains("combinations of its axes", "one finding that names the axes, lists every combination you found",
                 "recommend a matrix task", prompts.LoadPlanReviewCritic("codex"));
        DoesNotContain("recommend a matrix task", prompts.LoadCodeReviewCritic("codex"));
    }

    private static void Contains(params string[] values)
    {
        var text = Collapse(values[^1]);
        foreach (var value in values[..^1])
            Assert.Contains(Collapse(value), text, StringComparison.Ordinal);
    }

    private static void DoesNotContain(string value, string text) =>
        Assert.DoesNotContain(Collapse(value), Collapse(text), StringComparison.Ordinal);

    private static string Skill() => File.ReadAllText(Path.Combine(RepositoryRoot(), "skills", "forge", "SKILL.md"));

    private static string Collapse(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "skills", "forge", "SKILL.md")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return PromptLibrary.Locate(null);
    }
}
