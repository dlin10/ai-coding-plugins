using System.Text.Json;
using System.Threading.Channels;
using PlanForge.Diagnostics;
using PlanForge.Infrastructure;

namespace PlanForge.Vendors.Cursor;

/// <summary>
/// Has no structured output tool, so it reaches structure through <see cref="SchemaInPrompt"/>:
/// schema in the prompt, validation here, one retry before the call is declared failed.
/// </summary>
internal sealed class CursorAgentSession : IVendorSession
{
    private static readonly TimeSpan _runTimeout = TimeSpan.FromMinutes(20);

    private readonly RoleSpec _role;
    private readonly Selection _selection;
    private readonly string? _workingDirectory;
    private readonly Channel<VendorEvent> _events = Channel.CreateUnbounded<VendorEvent>();

    private string? _chatId;

    public CursorAgentSession(RoleSpec role, Selection selection, string? workingDirectory, string? resumeToken = null)
    {
        _role = role;
        _selection = selection;
        _workingDirectory = workingDirectory;
        _chatId = resumeToken;
    }

    public IAsyncEnumerable<VendorEvent> Events => _events.Reader.ReadAllAsync();

    public bool CanResume => _role.Role is VendorRole.Builder;

    public string? ResumeToken => CanResume ? _chatId : null;

    public async Task<T> RunAsync<T>(string prompt, VendorSchema<T> schema, CancellationToken ct)
    {
        string? lastFailure = null;
        for (var attempt = 1; attempt <= SchemaInPrompt.MaxAttempts; attempt++)
        {
            var spec = new ProcessSpec(CursorAgentVendor.Executable, BuildArguments(), _workingDirectory,
                SchemaInPrompt.Compose(WithRoleInstructions(prompt), schema.Json, lastFailure));

            await _events.Writer.EmitAsync("cursor", new VendorEvent(VendorEventKind.Started, $"attempt {attempt}"), ct);

            string text;
            try
            {
                text = await ReadResultAsync(spec, ct);
            }
            catch (VendorException error)
            {
                await _events.Writer.EmitAsync("cursor", new VendorEvent(VendorEventKind.Failed, error.Message), ct);
                throw new VendorException($"cursor-agent failed for {DescribeSelection()}: {error.Message}");
            }
            if (SchemaInPrompt.TryExtract(text, schema, out var value, out lastFailure))
            {
                await _events.Writer.EmitAsync("cursor", new VendorEvent(VendorEventKind.Finished, _role.Role.ToString()), ct);
                return value;
            }

            await _events.Writer.EmitAsync("cursor", new VendorEvent(VendorEventKind.Failed, lastFailure ?? "invalid reply"), ct);
        }

        throw new VendorException($"cursor-agent did not return a valid object in {SchemaInPrompt.MaxAttempts} attempts: {lastFailure}");
    }

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    internal async Task<string> ReadResultAsync(ProcessSpec spec, CancellationToken ct)
    {
        var result = string.Empty;
        await foreach (var line in StreamingProcess.RunAsync(spec, _runTimeout, ct))
        {
            JsonDocument message;
            try { message = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (message)
            {
                var root = message.RootElement;

                // Same hazard as ClaudeCliSession.Observe (issue #41): a line that parses as JSON
                // but not as an object would throw on the property probe and end the run under a
                // misleading output-cap kill.
                if (root.ValueKind is not JsonValueKind.Object)
                {
                    RunLog.Current?.Write("warn", "cursor", "vendor.skipped-line",
                        ("payload", RunLog.Truncate(root.GetRawText())));
                    continue;
                }

                if (root.TryGetProperty("session_id", out var session) && session.GetString() is { } id) _chatId = id;
                if (root.TryGetProperty("type", out var type) && type.GetString() is "result"
                    && root.TryGetProperty("result", out var payload))
                    result = payload.GetString() ?? string.Empty;
            }
        }

        return result;
    }

    /// <summary>
    /// cursor-agent has no system-prompt flag (measured against 2026.08.11-e8db854: the help lists
    /// none), so the role instructions ride at the head of the prompt — the one channel it offers.
    /// </summary>
    internal string WithRoleInstructions(string prompt) => $"{_role.SystemPrompt}\n\n{prompt}";

    internal List<string> BuildArguments()
    {
        // Headless runs load global and plugin MCP servers but drop the workspace .cursor/mcp.json
        // ones unless they are approved at launch; "cursor-agent mcp enable" does not reach print
        // mode (measured against 2026.08.11-e8db854). Without this flag both roles lose
        // solution-local servers such as roslyn-mcp. It approves every configured server, so a
        // plan-mode critic can also reach the user's global MCP servers.
        var arguments = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--force",
            "--trust",
            "--approve-mcps",
            "--model", ModelWithEffort()
        };

        // A critic judges; it does not edit. Plan mode is this vendor's own read-only profile, and
        // it is the only thing standing between --force and a reviewer — or a subagent it spawned —
        // deciding to fix what it just found. Codex gets this from its sandbox instead.
        if (_role.Role is VendorRole.Critic)
        {
            arguments.Add("--mode");
            arguments.Add("plan");
        }

        if (CanResume && _chatId is not null)
        {
            arguments.Add("--resume");
            arguments.Add(_chatId);
        }

        return arguments;
    }

    /// <summary>
    /// Cursor carries effort inside the model id, so the join happens here, not in the core.
    /// "default" is the catalogue's name for a family's bare variant, so it joins to nothing.
    /// </summary>
    internal string ModelWithEffort() =>
        string.IsNullOrWhiteSpace(_selection.Effort)
        || _selection.Effort.Equals("default", StringComparison.OrdinalIgnoreCase)
        || _selection.Model.EndsWith(_selection.Effort, StringComparison.OrdinalIgnoreCase)
            ? _selection.Model
            : $"{_selection.Model}-{_selection.Effort}";

    /// <summary>
    /// Names what was asked — model, effort, and the joined id when it differs — so a run the
    /// vendor rejects reads as a bad request to correct, not as infrastructure to retry.
    /// </summary>
    internal string DescribeSelection()
    {
        var effort = string.IsNullOrWhiteSpace(_selection.Effort) ? "no effort" : $"effort \"{_selection.Effort}\"";
        var joined = ModelWithEffort();
        var sent = joined == _selection.Model ? string.Empty : $", sent as \"{joined}\"";
        return $"model \"{_selection.Model}\" with {effort}{sent}";
    }
}
