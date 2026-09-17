using System.Text.Json;
using PlanForge.Diagnostics;
using PlanForge.Vendors;
using PlanForge.Vendors.Claude;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// Issue #91: `claude -p` ends at the final result and kills what the model left running in the
/// background about five seconds later, exiting 0. The streams below are the ones Claude Code
/// 2.1.273 produced on 2026-09-17, with ids, timings and the long fields cut. See docs/adr/0018.
/// </summary>
public sealed class ClaudeBackgroundTaskTests
{
    private const string Backgrounded =
        """
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_013K","name":"Bash","input":{"command":"sleep 60 && echo finished > done.txt","description":"Run a 60-second sleep and write finished to a file","run_in_background":true}}]}}
        {"type":"system","subtype":"task_summary","detail":"Running a 60-second sleep and writing finished to a file"}
        {"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"be988ggq8","task_type":"local_bash","description":"Run a 60-second sleep and write finished to a file"}]}
        {"type":"system","subtype":"task_started","task_id":"be988ggq8","tool_use_id":"toolu_013K","description":"Run a 60-second sleep and write finished to a file","is_backgrounded":true,"task_type":"local_bash"}
        {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_013K","type":"tool_result","content":"Command running in background with ID: be988ggq8.","is_error":false}]}}
        {"type":"assistant","message":{"content":[{"type":"text","text":"started"}]}}
        {"type":"result","subtype":"success","is_error":false,"result":"started"}
        {"type":"system","subtype":"background_tasks_changed","tasks":[]}
        {"type":"system","subtype":"task_updated","task_id":"be988ggq8","patch":{"status":"killed","end_time":1789624774987}}
        {"type":"system","subtype":"task_notification","task_id":"be988ggq8","tool_use_id":"toolu_013K","status":"stopped","summary":"Run a 60-second sleep and write finished to a file"}
        {"type":"system","subtype":"task_summary","detail":null}
        """;

    private const string Foreground =
        """
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01Mu","name":"Bash","input":{"command":"ping -n 91 127.0.0.1 > /dev/null; echo pinged","timeout":180000,"run_in_background":false}}]}}
        {"type":"system","subtype":"task_started","task_id":"bko2hz714","tool_use_id":"toolu_01Mu","description":"Ping localhost 91 times and echo result","is_backgrounded":false,"task_type":"local_bash"}
        {"type":"tool_progress","tool_use_id":"toolu_01Mu-heartbeat-0","tool_name":"Bash","parent_tool_use_id":"toolu_01Mu","elapsed_time_seconds":30,"heartbeat":true}
        {"type":"system","subtype":"task_notification","task_id":"bko2hz714","tool_use_id":"toolu_01Mu","status":"completed","summary":"Ping localhost 91 times and echo result"}
        {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_01Mu","type":"tool_result","content":"pinged","is_error":false}]}}
        {"type":"assistant","message":{"content":[{"type":"text","text":"pinged"}]}}
        {"type":"result","subtype":"success","is_error":false,"result":"pinged"}
        """;

    [Fact]
    public void A_task_still_running_when_the_turn_ended_is_named_by_its_command()
    {
        using var log = new ScopedLog();

        var session = Observe(Backgrounded);

        Assert.Equal(["sleep 60 && echo finished > done.txt"], session.KilledBackgroundTasks);
    }

    /// <summary>
    /// These messages carry no `message` property, so the session used to drop every one of them
    /// unlogged whenever the process exited 0.
    /// </summary>
    [Fact]
    public void The_kill_reaches_the_run_log()
    {
        using var log = new ScopedLog();

        Observe(Backgrounded);

        var killed = Assert.Single(log.All("vendor.task"),
            entry => entry.GetProperty("fields").GetProperty("status").GetString() == "killed");
        Assert.Equal("be988ggq8", killed.GetProperty("fields").GetProperty("task").GetString());
        Assert.Equal("sleep 60 && echo finished > done.txt",
                     killed.GetProperty("fields").GetProperty("command").GetString());
    }

    [Fact]
    public void A_foreground_command_is_never_counted()
    {
        using var log = new ScopedLog();

        Assert.Empty(Observe(Foreground).KilledBackgroundTasks);
    }

    [Fact]
    public void A_background_task_that_finished_before_the_answer_is_not_counted()
    {
        using var log = new ScopedLog();

        var session = Observe(Before(Backgrounded,
            """{"type":"system","subtype":"task_notification","task_id":"be988ggq8","status":"completed"}"""));

        Assert.Empty(session.KilledBackgroundTasks);
    }

    /// <summary>A task the model stopped itself, before it answered, was a decision rather than a loss.</summary>
    [Fact]
    public void A_background_task_the_model_stopped_before_answering_is_not_counted()
    {
        using var log = new ScopedLog();

        var session = Observe(Before(Backgrounded,
            """{"type":"system","subtype":"task_notification","task_id":"be988ggq8","status":"stopped"}"""));

        Assert.Empty(session.KilledBackgroundTasks);
    }

    /// <summary>
    /// Documented rather than measured: `-p` waits for a background subagent, because its result is
    /// part of the output, so one that completes after the result line was not lost.
    /// </summary>
    [Fact]
    public void A_task_that_completes_after_the_result_is_not_counted()
    {
        using var log = new ScopedLog();

        var session = Observe(
            """
            {"type":"system","subtype":"task_started","task_id":"a1","tool_use_id":"toolu_A","description":"Review the diff","is_backgrounded":true,"task_type":"local_agent"}
            {"type":"result","subtype":"success","is_error":false,"result":"waiting"}
            {"type":"system","subtype":"task_notification","task_id":"a1","status":"completed"}
            """);

        Assert.Empty(session.KilledBackgroundTasks);
    }

    /// <summary>A task whose command the stream never showed is still named, by what claude called it.</summary>
    [Fact]
    public void A_task_with_no_known_command_is_named_by_its_description()
    {
        using var log = new ScopedLog();

        var session = Observe(
            """
            {"type":"system","subtype":"task_started","task_id":"a1","tool_use_id":"toolu_A","description":"Review the diff","is_backgrounded":true,"task_type":"local_agent"}
            {"type":"result","subtype":"success","is_error":false,"result":"done"}
            """);

        Assert.Equal(["Review the diff"], session.KilledBackgroundTasks);
    }

    private static ClaudeCliSession Observe(string stream)
    {
        var session = new ClaudeCliSession(new RoleSpec(VendorRole.Builder, "build"), new Selection("sonnet", null), null);
        foreach (var line in stream.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            session.Observe(document.RootElement);
        }

        return session;
    }

    /// <summary>Puts <paramref name="line"/> in front of the stream's result line.</summary>
    private static string Before(string stream, string line) =>
        stream.Replace("""{"type":"result",""", line + "\n" + """{"type":"result",""", StringComparison.Ordinal);

    private sealed class ScopedLog : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

        private readonly IDisposable _scope;
        private readonly string _path;

        public ScopedLog()
        {
            Directory.CreateDirectory(_directory);
            _path = Path.Combine(_directory, "forge.log");
            _scope = RunLog.Use(new RunLog(_path));
        }

        public List<JsonElement> All(string name) =>
            File.Exists(_path)
                ? [.. File.ReadAllLines(_path)
                          .Select(line => JsonDocument.Parse(line).RootElement)
                          .Where(entry => entry.GetProperty("event").GetString() == name)]
                : [];

        public void Dispose()
        {
            _scope.Dispose();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
    }
}
