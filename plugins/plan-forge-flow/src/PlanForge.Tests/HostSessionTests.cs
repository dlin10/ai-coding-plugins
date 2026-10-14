using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Mcp;
using PlanForge.Orchestration;
using PlanForge.Repo;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The Host session (R5): Claude Code's session id read through the injected environment, else the
/// latest Codex <c>threadId</c> from a call's <c>_meta</c>, else none; and the record a close writes
/// from it. Every owner reads the test's own environment, never the real variable.
/// </summary>
public sealed class HostSessionTests : IDisposable
{
    private const string SESSION_VARIABLE = "CLAUDE_CODE_SESSION_ID";

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "planforge-host-session", Guid.NewGuid().ToString("n"));

    public HostSessionTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void Claude_code_with_the_variable_is_its_session()
    {
        var read = new List<string>();
        var hosts = new HostSessions(name =>
        {
            read.Add(name);
            return name == SESSION_VARIABLE ? "session-a" : null;
        });

        hosts.Observe("claude-code", "thread-ignored");

        Assert.Equal(new HostSession("claude-code", "session-a"), hosts.Current());
        Assert.Equal([SESSION_VARIABLE], read.Distinct());
    }

    [Fact]
    public void Claude_code_without_the_variable_has_no_session_even_with_a_thread()
    {
        var hosts = new HostSessions(_ => null);

        hosts.Observe("claude-code", "thread-1");

        Assert.Null(hosts.Current());
    }

    [Fact]
    public async Task A_codex_thread_is_taken_from_the_call_meta()
    {
        var phases = new OrchestratorPhases(TimeProvider.System, NoUsageSource.Instance, _ => "never-read");
        var threadId = OrchestratorCall.ThreadIdOf(JsonNode.Parse("""{"threadId":"thread-1","other":1}""")!.AsObject());

        await CallAsync(phases, "codex-mcp-client", threadId);

        Assert.Equal("thread-1", threadId);
        Assert.Equal(new HostSession("codex", "thread-1"), phases.Hosts.Current());
    }

    [Fact]
    public void A_meta_without_a_string_thread_id_carries_none()
    {
        Assert.Null(OrchestratorCall.ThreadIdOf(null));
        Assert.Null(OrchestratorCall.ThreadIdOf(JsonNode.Parse("""{"threadId":""}""")!.AsObject()));
        Assert.Null(OrchestratorCall.ThreadIdOf(JsonNode.Parse("""{"threadId":42}""")!.AsObject()));
        Assert.Null(OrchestratorCall.ThreadIdOf(JsonNode.Parse("""{"progressToken":"t"}""")!.AsObject()));
    }

    [Fact]
    public async Task A_later_call_without_meta_keeps_the_last_thread_and_a_new_thread_replaces_it()
    {
        var phases = new OrchestratorPhases(TimeProvider.System, NoUsageSource.Instance, _ => null);

        await CallAsync(phases, "codex-mcp-client", "thread-1");
        await CallAsync(phases, "codex-mcp-client", null);
        Assert.Equal(new HostSession("codex", "thread-1"), phases.Hosts.Current());

        await CallAsync(phases, "codex-mcp-client", "thread-2");
        Assert.Equal(new HostSession("codex", "thread-2"), phases.Hosts.Current());
    }

    [Fact]
    public async Task A_client_with_neither_has_no_session_and_its_record_says_no_host_session()
    {
        var source = new CountingSource();
        var phases = new OrchestratorPhases(TimeProvider.System, source, name => name == SESSION_VARIABLE ? "session-a" : null);

        var runId = await BeginAsync(phases, "cursor", null);
        await DeclareAsync(phases, runId, "planning", "cursor", null);

        Assert.Null(phases.Hosts.Current());
        var record = Assert.Single(Records(runId));
        Assert.Equal("cursor", record.GetProperty("host").GetString());
        Assert.Equal("no_host_session", record.GetProperty("usageMissing").GetString());
        Assert.False(record.TryGetProperty("hostSessionId", out _));
        Assert.False(record.TryGetProperty("usageSource", out _));
        Assert.False(record.TryGetProperty("usageIncomplete", out _));
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task A_session_changed_between_opening_and_closing_marks_the_record_incomplete()
    {
        var source = new CountingSource();
        var phases = new OrchestratorPhases(TimeProvider.System, source, _ => null);

        var runId = await BeginAsync(phases, "codex-mcp-client", "thread-1");
        await DeclareAsync(phases, runId, "planning", "codex-mcp-client", "thread-2");

        var record = Assert.Single(Records(runId));
        Assert.Equal("codex-mcp-client", record.GetProperty("host").GetString());
        Assert.Equal("thread-2", record.GetProperty("hostSessionId").GetString());
        Assert.True(record.GetProperty("usageIncomplete").GetBoolean());
        Assert.Equal(7, record.GetProperty("inputTokens").GetInt64());
        Assert.Equal("thread-2", source.LastSession?.Id);
    }

    [Fact]
    public async Task The_same_session_at_opening_and_closing_leaves_the_record_complete()
    {
        var source = new CountingSource();
        var phases = new OrchestratorPhases(TimeProvider.System, source, name => name == SESSION_VARIABLE ? "session-a" : null);

        var runId = await BeginAsync(phases, "claude-code", null);
        await DeclareAsync(phases, runId, "planning", "claude-code", null);

        var record = Assert.Single(Records(runId));
        Assert.Equal("claude-code", record.GetProperty("host").GetString());
        Assert.Equal("session-a", record.GetProperty("hostSessionId").GetString());
        Assert.False(record.TryGetProperty("usageIncomplete", out _));
        Assert.False(record.TryGetProperty("usageMissing", out _));
    }

    private static Task<string> CallAsync(OrchestratorPhases phases, string client, string? threadId) =>
        OrchestratorCall.InvokeAsync(phases, null, null, client, threadId, () => ValueTask.FromResult("done")).AsTask();

    private async Task<string> BeginAsync(OrchestratorPhases phases, string client, string? threadId)
    {
        var git = new GitClient(_repo);
        var ct = CancellationToken.None;
        await git.OutputAsync(["init", "-q"], ct);
        await git.OutputAsync(["config", "user.email", "tests@example.invalid"], ct);
        await git.OutputAsync(["config", "user.name", "PlanForge Tests"], ct);
        await File.WriteAllTextAsync(Path.Combine(_repo, "tracked.txt"), "original\n", ct);
        await git.OutputAsync(["add", "tracked.txt"], ct);
        await git.OutputAsync(["commit", "-qm", "initial"], ct);

        var json = await OrchestratorCall.InvokeAsync(phases, _repo, null, client, threadId, async () =>
            await ForgeTools.Begin(SessionRoots.None, client, CapabilityProfile.Text, _ => { }, _repo, ct));
        return JsonDocument.Parse(json).RootElement.GetProperty("runId").GetString()!;
    }

    private Task<string> DeclareAsync(OrchestratorPhases phases, string runId, string phase, string client, string? threadId) =>
        OrchestratorCall.InvokeAsync(phases, _repo, runId, client, threadId, async () =>
            await ForgeTools.AppendLog(SessionRoots.None, _repo, runId, $"entering {phase}", CancellationToken.None,
                                       phase: phase)).AsTask();

    private JsonElement[] Records(string runId) =>
        [.. JsonDocument.Parse(File.ReadAllText(Path.Combine(RunDirectory.Open(_repo, runId).Path, "telemetry.json")))
                        .RootElement.EnumerateArray()];

    /// <summary>A source that answers at once and remembers whom it was asked about.</summary>
    private sealed class CountingSource : IUsageSource
    {
        private int _reads;

        public int Reads => _reads;

        public HostSession? LastSession { get; private set; }

        public string? Name => "transcript";

        public TimeSpan SettleDelay => TimeSpan.Zero;

        public TimeSpan ReadDeadline => TimeSpan.FromSeconds(30);

        public Task<UsageReading> ReadAsync(UsageWindow window, CancellationToken ct)
        {
            Interlocked.Increment(ref _reads);
            LastSession = window.Session;
            return Task.FromResult(new UsageReading(new OrchestratorTokens(InputTokens: 7, OutputTokens: 1)));
        }

        public Task<DateTimeOffset?> PhaseOpenedAsync(HostSession? session, CancellationToken ct) =>
            Task.FromResult<DateTimeOffset?>(null);
    }
}
