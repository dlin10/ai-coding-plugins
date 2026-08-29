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
    private const string CallSuffix = "ToolCall";

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

            using (message) result = Observe(message.RootElement) ?? result;
        }

        return result;
    }

    /// <summary>Returns the run's final text when this message carries it.</summary>
    internal string? Observe(JsonElement root)
    {
        // Same hazard as ClaudeCliSession.Observe (issue #41): a line that parses as JSON but not
        // as an object would throw on the property probe and end the run under a misleading
        // output-cap kill.
        if (root.ValueKind is not JsonValueKind.Object)
        {
            RunLog.Current?.Write("warn", "cursor", "vendor.skipped-line",
                ("payload", RunLog.Truncate(root.GetRawText())));
            return null;
        }

        if (root.TryGetProperty("session_id", out var session) && session.GetString() is { } id) _chatId = id;
        if (!root.TryGetProperty("type", out var type)) return null;

        switch (type.GetString())
        {
            case "result" when root.TryGetProperty("result", out var payload):
                return payload.GetString() ?? string.Empty;

            case "tool_call":
                ObserveToolCall(root);
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Every tool this vendor runs arrives as one "tool_call" message — "started" carrying the
    /// arguments, "completed" carrying the outcome — and the call object holds exactly one
    /// "&lt;name&gt;ToolCall" member, whose name is the tool. Measured against 2026.08.25-3e8eec8,
    /// where it sits beside "toolCallId" and the timestamps, which is why the member is matched on
    /// its kind as well as its suffix. Until this was read the session kept only the final text,
    /// so a run whose every shell call failed reached the log looking like a clean one.
    /// </summary>
    private void ObserveToolCall(JsonElement root)
    {
        if (!root.TryGetProperty("subtype", out var subtype)
            || !root.TryGetProperty("tool_call", out var call)
            || call.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        var started = subtype.GetString() switch
        {
            "started" => true,
            "completed" => false,
            _ => (bool?)null
        };

        if (started is null) return;

        foreach (var member in call.EnumerateObject())
        {
            if (member.Value.ValueKind is not JsonValueKind.Object) continue;
            if (!member.Name.EndsWith(CallSuffix, StringComparison.Ordinal)) continue;

            var tool = member.Name[..^CallSuffix.Length];
            _events.Writer.Emit("cursor", started is true
                ? new VendorEvent(VendorEventKind.ToolUse, tool, Arguments(member.Value))
                : new VendorEvent(VendorEventKind.ToolResult, tool, Outcome(member.Value)));
        }
    }

    /// <summary>The command for the shell tool, the raw arguments for the rest — cut, not dropped.</summary>
    private static List<(string Name, string? Value)>? Arguments(JsonElement call)
    {
        if (!call.TryGetProperty("args", out var args) || args.ValueKind is not JsonValueKind.Object) return null;

        return args.TryGetProperty("command", out var command) && command.GetString() is { } line
            ? [("command", line)]
            : [("input", RunLog.Truncate(args.GetRawText()))];
    }

    /// <summary>
    /// The result is a one-of: "success", or a member named after the failure — "error" for a path
    /// the read tool could not find, "spawnError" for a shell backend that returns no exit status.
    /// Only "success" counts as success, so a failure shape nobody has seen yet still reaches the
    /// log as a failure rather than as silence.
    /// </summary>
    private static List<(string Name, string? Value)>? Outcome(JsonElement call)
    {
        if (!call.TryGetProperty("result", out var result) || result.ValueKind is not JsonValueKind.Object) return null;

        var succeeded = result.TryGetProperty("success", out var payload);
        var detail = new List<(string Name, string? Value)> { ("isError", succeeded ? "false" : "true") };

        if (succeeded && payload.TryGetProperty("exitCode", out var exit) && exit.ValueKind is JsonValueKind.Number)
            detail.Add(("exitCode", exit.GetRawText()));

        detail.Add(("output", RunLog.Tail(result.GetRawText())));
        return detail;
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
