using PlanForge.Acts;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// What makes a gate executable is where the code sits, not that code appears somewhere in it:
/// the run behind docs/adr/0015 had gates that opened with prose and named test files in
/// backticks, and running those as commands would have failed every one of them for nothing.
/// </summary>
public sealed class PlanGatesTests
{
    [Fact]
    public void The_code_right_after_the_gate_label_is_the_command()
    {
        var gate = PlanGates.TaskGate("**Do it.** Change the thing. **Gate:** `dotnet test src/X.slnx --filter \"FullyQualifiedName~Foo\"` is green without touching tests. (R1)");

        Assert.NotNull(gate);
        Assert.Equal("Gate", gate.Label);
        Assert.Equal("dotnet test src/X.slnx --filter \"FullyQualifiedName~Foo\"", gate.Command);
    }

    [Fact]
    public void A_gate_that_opens_with_prose_is_not_executable_even_when_it_names_a_file_in_backticks()
    {
        const string task = "**Do it.** **Gate:** тесты `Rules/CrossServiceGapTests.cs` зелёные; `dotnet test` проходит. (R4)";

        Assert.Null(PlanGates.TaskGate(task));
        Assert.True(PlanGates.HasGate(task));
    }

    [Fact]
    public void A_task_without_a_gate_label_has_no_gate()
    {
        Assert.Null(PlanGates.TaskGate("**Do it.** Change the thing, run `dotnet test`. (R1)"));
        Assert.False(PlanGates.HasGate("**Do it.** Change the thing, run `dotnet test`. (R1)"));
    }

    [Fact]
    public void A_fenced_block_after_the_label_is_the_command_one_line_per_line()
    {
        const string task =
            """
            **Do it.** Change the thing.
            **Gate:**
            ```powershell
            dotnet build src/X.slnx --nologo
            dotnet test src/X.slnx --no-build
            ```
            (R1)
            """;

        var gate = PlanGates.TaskGate(task);

        Assert.NotNull(gate);
        Assert.Equal("dotnet build src/X.slnx --nologo\ndotnet test src/X.slnx --no-build", gate.Command);
    }

    [Fact]
    public void The_label_tolerates_the_colon_outside_the_bold()
    {
        var gate = PlanGates.TaskGate("**Do it.** **Gate**: `git status --porcelain` prints nothing.");

        Assert.Equal("git status --porcelain", gate?.Command);
    }

    [Fact]
    public void Run_wide_gates_are_the_executable_entries_of_the_gates_section_in_order()
    {
        const string plan =
            """
            Builder: codex / gpt / high

            ## Requirements

            1. **R1.** Something.

            ## Gates

            1. **G1.** `dotnet test src/X.slnx` passes. (R1)
            2. **G2.** the build is warnings-clean, judged by reading the output of `dotnet build`. (R1)
            3. **G3.** `dotnet build src/X.slnx -warnaserror` passes. (R1)

            ## Approach

            1. **Task.** Do it. **Gate:** `exit 0`
            """;

        var gates = PlanGates.RunWideGates(plan);

        Assert.Equal(["G1", "G3"], gates.Select(gate => gate.Label));
        Assert.Equal(["dotnet test src/X.slnx", "dotnet build src/X.slnx -warnaserror"], gates.Select(gate => gate.Command));
        Assert.True(PlanGates.HasRunWideGates(plan));
    }

    [Fact]
    public void A_plan_without_a_gates_section_has_no_run_wide_gates()
    {
        const string plan = "## Approach\n\n1. **Task.** Do it. **Gate:** `exit 0`\n";

        Assert.Empty(PlanGates.RunWideGates(plan));
        Assert.False(PlanGates.HasRunWideGates(plan));
    }

    [Fact]
    public void A_gates_section_whose_entries_are_all_conditions_is_stated_but_not_executable()
    {
        const string plan = "## Gates\n\n1. **G1.** every public type has a doc comment. (R1)\n\n## Approach\n\n1. Task.\n";

        Assert.Empty(PlanGates.RunWideGates(plan));
        Assert.True(PlanGates.HasRunWideGates(plan));
    }
}
