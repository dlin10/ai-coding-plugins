using System.Text.Json;
using PlanForge.Vendors;
using PlanForge.Vendors.Codex;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// A codex Worker whose commands never started answered without reading the repository; see
/// docs/adr/0031. The stream lines are recorded from codex-cli 0.162.1 on 2026-10-12, whose sandbox
/// setup failed before every command of run 20261012-163837-6fc7b1.
/// </summary>
public sealed class CodexShellTests : IDisposable
{
    private const string UNSTARTED =
        """{"type":"item.completed","item":{"id":"item_2","type":"command_execution","command":"\"C:\\\\Program Files\\\\PowerShell\\\\7\\\\pwsh.exe\" -Command 'cmd /c echo probe-ok .'","aggregated_output":"Failed to create unified exec process: helper_unknown_error: setup refresh had errors","exit_code":-1,"status":"failed"}}""";

    private const string STARTED =
        """{"type":"item.completed","item":{"id":"item_3","type":"command_execution","command":"pwsh -Command 'Get-Content PLAN.md'","aggregated_output":"# Plan","exit_code":0,"status":"completed"}}""";

    // A command that ran and failed is still a command the shell started.
    private const string RAN_AND_FAILED =
        """{"type":"item.completed","item":{"id":"item_4","type":"command_execution","command":"pwsh -Command 'rg missing'","aggregated_output":"","exit_code":1,"status":"failed"}}""";

    private const string SETUP_LINE =
        """[2026-10-12T13:46:54.549666700+00:00] setup refresh completed with errors: ["runtime read/execute validation failed: validate runtime read/execute access on C:\\Users\\Admin\\AppData\\Local\\OpenAI\\Codex\\runtimes\\cua_node\\cfb32733c877621e\\bin\\node_repl.exe: open ACL target for root-only update: The process cannot access the file because it is being used by another process. (os error 32)"]""";

    private readonly string _home = Path.Combine(Path.GetTempPath(), "planforge-codex-home", Guid.NewGuid().ToString("n"));
    private readonly ShellHealth _health = new();

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("Critic")]
    [InlineData("Scout")]
    public void A_sandboxed_run_whose_every_command_failed_to_start_is_refused_in_codex_s_words(string role)
    {
        var session = Session(Enum.Parse<VendorRole>(role));
        Observe(session, UNSTARTED, UNSTARTED);

        var failure = session.JudgeShell(() => "node_repl.exe: (os error 32)");

        Assert.NotNull(failure);
        Assert.IsAssignableFrom<VendorException>(failure);
        Assert.Contains($"could not start a shell for the {role}", failure.Message, StringComparison.Ordinal);
        Assert.Contains("none of the 2 command(s) it tried started", failure.Message, StringComparison.Ordinal);
        Assert.Contains("\"Failed to create unified exec process: helper_unknown_error: setup refresh had errors\"",
                        failure.Message, StringComparison.Ordinal);
        Assert.Contains("codex's sandbox log last recorded \"node_repl.exe: (os error 32)\"", failure.Message,
                        StringComparison.Ordinal);
        Assert.Contains("codex doctor --json", failure.Message, StringComparison.Ordinal);
        Assert.Equal("helper_unknown_error", failure.ErrorCode);
        Assert.Equal(failure.Diagnosis, _health.Degraded("codex"));
    }

    [Theory]
    [InlineData(STARTED)]
    [InlineData(RAN_AND_FAILED)]
    public void A_run_that_started_one_command_had_a_shell_and_ends_an_earlier_failure(string started)
    {
        _health.Failed("codex", "an earlier Scout had no shell");
        var session = Session(VendorRole.Critic);
        Observe(session, UNSTARTED, started);

        Assert.Null(session.JudgeShell(() => throw new InvalidOperationException("the log is read only for a failure")));
        Assert.Null(_health.Degraded("codex"));
    }

    [Fact]
    public void A_run_that_tried_no_command_says_nothing_about_its_shell()
    {
        _health.Failed("codex", "an earlier Scout had no shell");
        var session = Session(VendorRole.Scout);

        Assert.Null(session.JudgeShell(() => throw new InvalidOperationException("the log is read only for a failure")));
        Assert.Equal("an earlier Scout had no shell", _health.Degraded("codex"));
    }

    [Fact]
    public void A_builder_without_a_shell_is_refused_but_says_nothing_about_the_sandbox()
    {
        var session = Session(VendorRole.Builder);
        Observe(session, UNSTARTED);

        var failure = session.JudgeShell(() => throw new InvalidOperationException("a Builder runs outside the sandbox"));

        Assert.NotNull(failure);
        Assert.Contains("could not start a shell for the Builder", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sandbox", failure.Message, StringComparison.Ordinal);
        Assert.Null(_health.Degraded("codex"));
    }

    [Fact]
    public void A_builder_s_started_command_does_not_end_a_sandbox_failure()
    {
        _health.Failed("codex", "a Scout had no shell");
        var session = Session(VendorRole.Builder);
        Observe(session, STARTED);

        Assert.Null(session.JudgeShell(() => null));
        Assert.Equal("a Scout had no shell", _health.Degraded("codex"));
    }

    [Theory]
    [InlineData("Failed to create unified exec process: helper_unknown_error: setup refresh had errors", "helper_unknown_error")]
    [InlineData("Failed to create unified exec process: program not found", null)]
    [InlineData("Failed to create unified exec process: ", null)]
    [InlineData("Unified exec process failed: helper_unknown_error", null)]
    public void Only_a_bare_identifier_after_the_launch_failure_is_an_error_code(string diagnostic, string? expected) =>
        Assert.Equal(expected, CodexSandbox.ErrorCode(diagnostic));

    [Fact]
    public void A_failed_setup_refresh_line_names_what_blocked_it()
    {
        var error = CodexSandbox.SetupError(SETUP_LINE);

        Assert.NotNull(error);
        Assert.StartsWith("runtime read/execute validation failed", error, StringComparison.Ordinal);
        Assert.Contains(@"cua_node\cfb32733c877621e\bin\node_repl.exe", error, StringComparison.Ordinal);
        Assert.EndsWith("(os error 32)", error, StringComparison.Ordinal);
        Assert.Null(CodexSandbox.SetupError(
            "[2026-10-12T13:47:10.219865+00:00] setup refresh: processed 0 write roots (read roots delegated); errors=[]"));
    }

    [Fact]
    public void The_last_failed_refresh_of_the_newest_dated_log_is_the_one_reported()
    {
        var sandbox = Directory.CreateDirectory(Path.Combine(_home, ".sandbox")).FullName;
        File.WriteAllLines(Path.Combine(sandbox, "sandbox.2026-10-10.log"),
                           [SETUP_LINE.Replace("node_repl.exe", "older.exe", StringComparison.Ordinal)]);
        File.WriteAllLines(Path.Combine(sandbox, "sandbox.log"),
                           [SETUP_LINE.Replace("node_repl.exe", "legacy.exe", StringComparison.Ordinal)]);
        File.WriteAllLines(Path.Combine(sandbox, "sandbox.2026-10-12.log"),
        [
            SETUP_LINE.Replace("node_repl.exe", "first.exe", StringComparison.Ordinal),
            "[2026-10-12 21:48:40.361 codex-windows-sandbox-setup.exe] setup binary completed",
            SETUP_LINE
        ]);

        var error = CodexSandbox.LastSetupError(_home);

        Assert.NotNull(error);
        Assert.Contains(@"\bin\node_repl.exe", error, StringComparison.Ordinal);
        Assert.Null(CodexSandbox.LastSetupError(Path.Combine(_home, "missing")));
    }

    private CodexCliSession Session(VendorRole role) =>
        new(new RoleSpec(role, "contract"), new Selection("gpt-test", null), null, shellHealth: _health);

    private static void Observe(CodexCliSession session, params string[] lines)
    {
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            session.Observe(document.RootElement);
        }
    }
}
