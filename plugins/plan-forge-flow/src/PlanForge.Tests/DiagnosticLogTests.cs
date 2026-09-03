using System.Diagnostics;
using System.Text.Json;
using PlanForge.Diagnostics;
using PlanForge.Infrastructure;
using PlanForge.Mcp;
using PlanForge.Repo;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The run's operational log. The property under test throughout is the one the old run folder
/// failed: that a run which produced no result still explains itself afterwards.
/// </summary>
public sealed class DiagnosticLogTests : IDisposable
{
    private const string Plan =
        """
        # Title

        ## Approach

        1. First task.
        """;

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));
    private readonly GitClient _git;

    public DiagnosticLogTests()
    {
        Directory.CreateDirectory(_repo);
        _git = new GitClient(_repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_tool_call_records_its_arguments_and_its_result()
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);

        await ForgeTools.ConfirmPlan(SessionRoots.None, _repo, run.RunId, Plan, approved: true, ct);

        var entries = Read(run);
        var call = Single(entries, "tool.call");
        Assert.Equal("forge.plan.confirm", Field(call, "tool"));
        Assert.Equal("True", Field(call, "approved"));
        Assert.Contains("First task", Field(call, "plan"), StringComparison.Ordinal);

        Assert.Equal("forge.plan.confirm", Field(Single(entries, "tool.result"), "tool"));
    }

    /// <summary>
    /// The case the run folder used to lose entirely: an act that throws wrote nothing at all,
    /// because both existing logs record results and a failure has none.
    /// </summary>
    [Fact]
    public async Task A_failing_tool_call_records_the_exception()
    {
        var ct = CancellationToken.None;
        var run = RunDirectory.Create(_repo, "20260101-000000-nostate");

        await Assert.ThrowsAnyAsync<IOException>(
            () => ForgeTools.ConfirmPlan(SessionRoots.None, _repo, run.RunId, Plan, approved: true, ct));

        var failure = Single(Read(run), "tool.failed");
        Assert.Equal("forge.plan.confirm", Field(failure, "tool"));
        Assert.Contains("state.json", Field(failure, "error"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PlanForge.Run.RunDirectory", Field(failure, "stack"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The acceptance criterion of the issue this log was written for: a vendor that rejects what
    /// it was asked has to be diagnosable from the file, without reproducing the run. The command
    /// line is the part that was missing — an argument nobody can see is how a bad model id reads
    /// as an unexplained failure.
    /// </summary>
    [Fact]
    public async Task A_rejected_process_leaves_its_command_line_and_its_exit_in_the_log()
    {
        var run = RunDirectory.Create(_repo, "20260101-000000-process");
        var spec = Rejecting();

        using (RunLog.Use(run.Log))
        {
            await Assert.ThrowsAsync<VendorException>(
                () => StreamingProcess.CollectAsync(spec, TimeSpan.FromMinutes(1), CancellationToken.None));
        }

        var entries = Read(run);
        var start = Single(entries, "process.start");
        Assert.Equal(spec.FileName, Field(start, "exec"));
        Assert.Contains("gpt-5.6-sol-xhigh", Field(start, "args"), StringComparison.Ordinal);
        Assert.Equal(_repo, Field(start, "cwd"));

        var exit = Single(entries, "process.exit");
        Assert.Equal("3", Field(exit, "exitCode"));
        Assert.Contains("unknown model", Field(exit, "stderrTail"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_orchestrator_appends_through_the_tool_rather_than_by_hand()
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);

        var path = await ForgeTools.AppendLog(SessionRoots.None, _repo, run.RunId, "retrying the critic", ct,
            level: "warn", detail: "the first attempt returned no object");

        Assert.Equal(run.DiagnosticLogPath, path);

        var note = Single(Read(run), "note");
        Assert.Equal("warn", note.GetProperty("level").GetString());
        Assert.Equal("orchestrator", note.GetProperty("source").GetString());
        Assert.Equal("retrying the critic", Field(note, "message"));
        Assert.Equal("the first attempt returned no object", Field(note, "detail"));
    }

    /// <summary>
    /// The twenty-minute timeout that swallowed a critique delivered in two: cursor-agent finished
    /// and exited, an MCP server it had spawned kept the inherited stdout handle, and a reader
    /// waiting for EOF was waiting on a process that was never the one under the timeout. What the
    /// vendor wrote still arrives; the wait ends when the vendor does.
    /// </summary>
    [Fact]
    public async Task A_child_holding_the_pipe_open_does_not_extend_the_wait_past_the_process()
    {
        var run = RunDirectory.Create(_repo, "20260101-000000-lingering");
        var clock = Stopwatch.StartNew();

        IReadOnlyList<string> lines;
        using (RunLog.Use(run.Log))
        {
            lines = await StreamingProcess.CollectAsync(Lingering(), TimeSpan.FromMinutes(1), CancellationToken.None);
        }

        Assert.Equal("done", lines.Single());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10),
            $"the read waited {clock.Elapsed} on a pipe the process no longer owned");
        Assert.Equal("0", Field(Single(Read(run), "process.exit"), "exitCode"));
    }

    /// <summary>
    /// A plan draft is tens of kilobytes, and a log that swallowed it whole would cost more than it
    /// explains. Cut rather than omitted: the head still identifies which draft was under review.
    /// </summary>
    [Fact]
    public async Task A_long_argument_is_truncated_rather_than_dropped()
    {
        var ct = CancellationToken.None;
        var run = await StartRunAsync(ct);
        var huge = Plan + new string('x', 20_000);

        await ForgeTools.ConfirmPlan(SessionRoots.None, _repo, run.RunId, huge, approved: false, ct);

        var logged = Field(Single(Read(run), "tool.call"), "plan");
        Assert.StartsWith("# Title", logged, StringComparison.Ordinal);
        Assert.EndsWith("[truncated]", logged, StringComparison.Ordinal);
        Assert.True(logged.Length < huge.Length, "the draft was logged whole");
    }

    /// <summary>
    /// Stands in for a vendor CLI refusing a model id: it names the id on stderr and exits non-zero,
    /// which is exactly the shape the log has to make legible.
    /// </summary>
    private ProcessSpec Rejecting()
    {
        const string Message = "unknown model gpt-5.6-sol-xhigh";
        return OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd.exe",
                ["/c", $"echo {Message} 1>&2 & exit 3", "--model", "gpt-5.6-sol-xhigh"], _repo, string.Empty)
            : new ProcessSpec("/bin/sh",
                ["-c", $"echo '{Message}' 1>&2; exit 3", "--model", "gpt-5.6-sol-xhigh"], _repo, string.Empty);
    }

    /// <summary>
    /// Stands in for a vendor that outlives itself: it writes its one line and exits at once, while
    /// a background child it started holds the stdout handle it inherited for far longer.
    /// </summary>
    private ProcessSpec Lingering()
    {
        const int Seconds = 15;
        return OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd.exe",
                ["/c", $"echo done&start /b powershell -NoProfile -Command Start-Sleep -Seconds {Seconds}"],
                _repo, string.Empty)
            : new ProcessSpec("/bin/sh", ["-c", $"echo done; sleep {Seconds} &"], _repo, string.Empty);
    }

    private static IReadOnlyList<JsonElement> Read(RunDirectory run) =>
        File.ReadAllLines(run.DiagnosticLogPath)
            .Where(line => line.Length > 0)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

    private static JsonElement Single(IEnumerable<JsonElement> entries, string name) =>
        entries.Single(entry => entry.GetProperty("event").GetString() == name);

    private static string Field(JsonElement entry, string name) =>
        entry.GetProperty("fields").GetProperty(name).GetString()!;

    private async Task<RunDirectory> StartRunAsync(CancellationToken ct)
    {
        await _git.OutputAsync(["init", "-q"], ct);
        await _git.OutputAsync(["config", "user.email", "tests@example.invalid"], ct);
        await _git.OutputAsync(["config", "user.name", "PlanForge Tests"], ct);
        await File.WriteAllTextAsync(Path.Combine(_repo, "tracked.txt"), "original\n", ct);
        await _git.OutputAsync(["add", "tracked.txt"], ct);
        await _git.OutputAsync(["commit", "-qm", "initial"], ct);

        var run = RunDirectory.Create(_repo, "20260101-000000-abcdef");
        var baseline = await Baseline.CaptureAsync(_git, ct);
        run.WriteBaseline(baseline);
        run.WriteState(new RunState(run.RunId, _repo, "Text", DateTimeOffset.Now,
            ReviewRounds: 0, ReviewRoundCap: 5, BaselineHead: baseline.Head));

        return run;
    }
}
