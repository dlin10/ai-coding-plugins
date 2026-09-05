using PlanForge.Acts;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The gate runner's contract is its exit code: what the host's PowerShell says about a command is
/// what the task is worth. These drive real processes, so they hold the exact codes rather than
/// pass/fail alone — the builder is told the code, and a retry that reads `1` for a `3` is misled.
/// </summary>
public sealed class GateRunnerTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public GateRunnerTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_command_that_exits_zero_passes_with_its_output()
    {
        var run = await RunAsync("Write-Output 'all green'");

        Assert.Equal("passed", run.Outcome);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("all green", run.Output, StringComparison.Ordinal);
        Assert.NotNull(run.Seconds);
    }

    [Fact]
    public async Task A_native_exit_code_is_reported_as_the_gate_failing_with_that_code()
    {
        var run = await RunAsync("Write-Output 'about to fail'; cmd /c exit 7");

        Assert.Equal("failed", run.Outcome);
        Assert.Equal(7, run.ExitCode);
        Assert.Contains("about to fail", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_powershell_error_fails_the_gate_with_exit_code_one_and_the_message()
    {
        var run = await RunAsync("throw 'the fixture is missing'");

        Assert.Equal("failed", run.Outcome);
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("the fixture is missing", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_script_stops_at_the_first_failing_line_and_reports_its_code()
    {
        var run = await RunAsync("cmd /c exit 4\nWrite-Output 'never reached'");

        Assert.Equal("failed", run.Outcome);
        Assert.Equal(4, run.ExitCode);
        Assert.DoesNotContain("never reached", run.Output ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_explicit_exit_inside_the_gate_is_its_verdict()
    {
        var run = await RunAsync("if ((Get-ChildItem).Count -ge 0) { exit 9 }");

        Assert.Equal("failed", run.Outcome);
        Assert.Equal(9, run.ExitCode);
    }

    [Fact]
    public async Task The_environment_from_forge_begin_reaches_the_command()
    {
        var environment = new Dictionary<string, string> { ["FORGE_GATE_PROBE"] = "Server=sql;Database=x" };

        var run = await RunAsync("if ($env:FORGE_GATE_PROBE -ne 'Server=sql;Database=x') { exit 5 }; Write-Output $env:FORGE_GATE_PROBE",
                                 environment);

        Assert.Equal("passed", run.Outcome);
        Assert.Contains("Server=sql;Database=x", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_command_runs_from_the_workspace_root()
    {
        var run = await RunAsync("(Get-Location).Path");

        Assert.Equal("passed", run.Outcome);
        Assert.Equal(Path.GetFullPath(_workspace).TrimEnd(Path.DirectorySeparatorChar),
                     run.Output?.Trim().TrimEnd(Path.DirectorySeparatorChar), ignoreCase: true);
    }

    [Fact]
    public async Task A_command_that_outlives_the_timeout_is_killed_and_reported_as_a_timeout()
    {
        var run = await GateRunner.RunAsync(new GateCommand("Gate", "Write-Output 'started'; Start-Sleep -Seconds 60"),
                                            _workspace, null, TimeSpan.FromSeconds(3), CancellationToken.None);

        Assert.Equal("timeout", run.Outcome);
        Assert.Null(run.ExitCode);
        Assert.Contains("did not finish", run.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quotes_dollars_and_newlines_in_the_command_survive_the_trip()
    {
        var run = await RunAsync("$name = \"it's `\"quoted`\"\"\nWrite-Output \"value: $name\"");

        Assert.Equal("passed", run.Outcome);
        Assert.Contains("value: it's \"quoted\"", run.Output, StringComparison.Ordinal);
    }

    private Task<GateRun> RunAsync(string command, IReadOnlyDictionary<string, string>? environment = null) =>
        GateRunner.RunAsync(new GateCommand("Gate", command), _workspace, environment, Timeout, CancellationToken.None);
}
