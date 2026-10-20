using System.Diagnostics;
using System.Text;
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

    private const string SpendLimit = "hit your individual spend limit";

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

        await ForgeTools.ConfirmPlan(SessionRoots.None, _repo, run.RunId, approved: true, ct, plan: Plan);

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
            () => ForgeTools.ConfirmPlan(SessionRoots.None, _repo, run.RunId, approved: true, ct, plan: Plan));

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

    /// <summary>
    /// Run 20260905-144900-e42174: the claude CLI blocked by an individual spend limit says so on
    /// stdout and exits 1 with nothing on stderr, and the failure reached the orchestrator as
    /// <c>claude.exe exited 1: </c> — an exit code and an empty colon. What the CLI had already
    /// said cost a reproduction of the vendor call by hand.
    /// </summary>
    [Fact]
    public async Task A_process_that_fails_with_nothing_on_stderr_is_explained_by_its_stdout()
    {
        var run = RunDirectory.Create(_repo, "20260101-000000-refusal");

        VendorException error;
        using (RunLog.Use(run.Log))
        {
            error = await Assert.ThrowsAsync<VendorException>(
                () => StreamingProcess.CollectAsync(Refusing(), TimeSpan.FromMinutes(1), CancellationToken.None));
        }

        Assert.Contains(SpendLimit, error.Message, StringComparison.Ordinal);
        Assert.Contains(SpendLimit, Field(Single(Read(run), "process.exit"), "stdoutTail"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The stdout tail is the fallback, not an addition. Where stderr speaks it is the better
    /// account of the failure, and a caller holding stdout of its own — <c>GateRunner</c> — would
    /// otherwise be handed its own output back a second time. The log keeps both regardless: it has
    /// room the message does not.
    /// </summary>
    [Fact]
    public async Task A_process_that_speaks_on_stderr_is_reported_by_stderr_alone()
    {
        var run = RunDirectory.Create(_repo, "20260101-000000-both");

        VendorException error;
        using (RunLog.Use(run.Log))
        {
            error = await Assert.ThrowsAsync<VendorException>(
                () => StreamingProcess.CollectAsync(Muttering(), TimeSpan.FromMinutes(1), CancellationToken.None));
        }

        Assert.Contains("unknown model", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("chatter", error.Message, StringComparison.Ordinal);

        var exit = Single(Read(run), "process.exit");
        Assert.Contains("unknown model", Field(exit, "stderrTail"), StringComparison.Ordinal);
        Assert.Contains("chatter", Field(exit, "stdoutTail"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A vendor writes megabytes before it fails, and the whole of it in an exception message would
    /// cost more than it explains. The tail rather than the head, for the reason a process dying
    /// says why in its last lines.
    /// </summary>
    [Fact]
    public async Task A_long_stdout_tail_is_cut_to_its_end_rather_than_carried_whole()
    {
        var run = RunDirectory.Create(_repo, "20260101-000000-verbose");
        var path = Path.Combine(_repo, "chatter.txt");
        await File.WriteAllLinesAsync(path,
            Enumerable.Range(0, 500).Select(index => $"line {index} " + new string('x', 200)));

        VendorException error;
        using (RunLog.Use(run.Log))
        {
            error = await Assert.ThrowsAsync<VendorException>(
                () => StreamingProcess.CollectAsync(Replaying(path), TimeSpan.FromMinutes(1), CancellationToken.None));
        }

        Assert.Contains("line 499", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("line 0 ", error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Length < 4000, $"the message carried {error.Message.Length} characters");
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

    [Fact]
    public void Run_files_keep_non_ascii_text_as_written()
    {
        var run = RunDirectory.Create(_repo, "20260101-000000-readable");

        run.Log.Write("info", "test", "note", ("message", "повтор критика"));
        run.WriteState(new RunState(run.RunId, _repo, "Text", DateTimeOffset.Now, 0, 5)
                       with { BuilderInstructions = "используй ponytail-net" });

        var log = AtomicFile.Read(run.DiagnosticLogPath);
        Assert.Contains("повтор критика", log, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", log, StringComparison.Ordinal);
        var state = File.ReadAllText(Path.Combine(run.Path, "state.json"));
        Assert.Contains("используй ponytail-net", state, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", state, StringComparison.Ordinal);
    }

    /// <summary>
    /// The twenty-minute timeout that swallowed a critique delivered in two: cursor-agent finished
    /// and exited, an MCP server it had spawned kept the inherited stdout handle, and a reader
    /// waiting for EOF was waiting on a process that was never the one under the timeout. What the
    /// vendor wrote still arrives; the wait ends when the vendor does.
    /// </summary>
    /// <remarks>
    /// The child holds the pipe until the test lets it go, after the read has returned, so a read
    /// that waits for EOF never returns at all and fails on the one-minute timeout, however fast or
    /// slow the machine. The time bound is left to the drains, five seconds for stdout and two for
    /// stderr, and to launching the process: the release run of 0.44.1 took 12.5 seconds for the
    /// correct read on a loaded runner, against a ten-second bound and a fifteen-second child.
    /// </remarks>
    [Fact]
    public async Task A_child_holding_the_pipe_open_does_not_extend_the_wait_past_the_process()
    {
        var run = RunDirectory.Create(_repo, "20260101-000000-lingering");
        var hold = Path.Combine(_repo, "hold-the-pipe");
        File.WriteAllText(hold, string.Empty);
        var clock = Stopwatch.StartNew();

        IReadOnlyList<string> lines;
        try
        {
            using (RunLog.Use(run.Log))
            {
                lines = await StreamingProcess.CollectAsync(Lingering(hold), TimeSpan.FromMinutes(1), CancellationToken.None);
            }
        }
        finally
        {
            File.Delete(hold);
        }

        Assert.Equal("done", lines.Single());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20),
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

        await ForgeTools.ConfirmPlan(SessionRoots.None, _repo, run.RunId, approved: false, ct, plan: huge);

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
    /// Stands in for a vendor CLI that refuses on stdout: it says why, writes nothing at all to
    /// stderr, and exits non-zero — the shape an individual spend limit takes.
    /// </summary>
    private ProcessSpec Refusing() =>
        OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd.exe", ["/c", $"echo {SpendLimit} & exit 1"], _repo, string.Empty)
            : new ProcessSpec("/bin/sh", ["-c", $"echo '{SpendLimit}'; exit 1"], _repo, string.Empty);

    /// <summary>Writes to both streams, so which one the message quotes is the thing under test.</summary>
    private ProcessSpec Muttering() =>
        OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd.exe", ["/c", "echo chatter on stdout & echo unknown model 1>&2 & exit 3"],
                _repo, string.Empty)
            : new ProcessSpec("/bin/sh", ["-c", "echo 'chatter on stdout'; echo 'unknown model' 1>&2; exit 3"],
                _repo, string.Empty);

    /// <summary>Replays a file to stdout and then fails, with nothing on stderr to explain it.</summary>
    private ProcessSpec Replaying(string path) =>
        OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd.exe", ["/c", $"type {path} & exit 1"], _repo, string.Empty)
            : new ProcessSpec("/bin/sh", ["-c", $"cat '{path}'; exit 1"], _repo, string.Empty);

    /// <summary>
    /// Stands in for a vendor that outlives itself: it writes its one line and exits at once, while
    /// a background child it started holds the stdout handle it inherited until
    /// <paramref name="hold"/> is deleted — two minutes at most, should the test die first.
    /// </summary>
    /// <param name="hold">The file whose existence keeps the child, and so the pipe, alive.</param>
    private ProcessSpec Lingering(string hold)
    {
        if (!OperatingSystem.IsWindows())
            return new ProcessSpec("/bin/sh",
                ["-c", $"echo done; ( i=0; while [ -e '{hold}' ] && [ $i -lt 600 ]; do sleep 0.2; i=$((i+1)); done ) &"],
                _repo, string.Empty);

        // Encoded, so no quote in the script has to survive cmd.exe's own parsing.
        var script = $"$hold = '{hold.Replace("'", "''", StringComparison.Ordinal)}'; $until = (Get-Date).AddMinutes(2); "
                     + "while ((Test-Path -LiteralPath $hold) -and (Get-Date) -lt $until) { Start-Sleep -Milliseconds 200 }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return new ProcessSpec("cmd.exe",
            ["/c", $"echo done&start /b powershell -NoProfile -EncodedCommand {encoded}"],
            _repo, string.Empty);
    }

    /// <summary>
    /// Through <see cref="AtomicFile.Read"/> rather than <c>File.ReadAllLines</c>, which cannot open
    /// a file an <see cref="AtomicFile.Append"/> handle is holding: the append shares Read, and a
    /// reader sharing only Read refuses to coexist with the writer's Write access. The appender is
    /// not this test — <c>RunLog.Current</c> falls back to the last log any tool call served, so a
    /// parallel test class spawning a process writes into whichever run folder that was.
    /// </summary>
    private static IReadOnlyList<JsonElement> Read(RunDirectory run) =>
        AtomicFile.Read(run.DiagnosticLogPath)
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
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
