using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using PlanForge.Diagnostics;
using PlanForge.Infrastructure;

namespace PlanForge.Vendors.Claude;

internal sealed class ClaudeCliSession : IVendorSession
{
    private const string StructuredOutputTool = "StructuredOutput";
    private const string SelfPluginSettings =
        """{"enabledPlugins":{"plan-forge-flow@dlin10-ai-coding-plugins":false}}""";

    // The Bash tool's own ceiling on a foreground command, ten minutes unless raised. Thirty covers
    // any gate the host would run (twenty) and leaves the rest of the host's hour for the gate that
    // follows the turn. The server's idle reaper does not interfere: a foreground call emits a
    // heartbeat every 30 s. See docs/adr/0018.
    private const string ForegroundCommandLimit = "1800000";

    private readonly RoleSpec _role;
    private readonly Selection _selection;
    private readonly string? _workingDirectory;
    private readonly IReadOnlyList<string> _grantedServers;
    private readonly Channel<VendorEvent> _events = Channel.CreateUnbounded<VendorEvent>();

    // Set on the first run so a Builder's later tasks resume the same conversation.
    private string? _sessionId;

    // tool_result blocks name their call by id only; the tool_use block carried the name.
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);

    // A task names the call that started it by id only; the tool_use block carried the command.
    private readonly Dictionary<string, string> _toolCommands = new(StringComparer.Ordinal);

    // Tasks claude started and has not reported finished, with what each runs.
    private readonly Dictionary<string, string> _openTasks = new(StringComparer.Ordinal);

    // Set by the result line. After it nothing the model started can still be collected.
    private bool _turnEnded;

    /// <param name="role">The worker role and its contract.</param>
    /// <param name="selection">The selected model and effort.</param>
    /// <param name="workingDirectory">The workspace the worker runs in.</param>
    /// <param name="resumeToken">The builder session to resume, when one exists.</param>
    /// <param name="grantedServers">The MCP servers this worker may call unasked, as claude lists them.</param>
    public ClaudeCliSession(RoleSpec role,
                            Selection selection,
                            string? workingDirectory,
                            string? resumeToken = null,
                            IReadOnlyList<string>? grantedServers = null)
    {
        _role = role;
        _selection = selection;
        _workingDirectory = workingDirectory;
        _sessionId = resumeToken;
        _grantedServers = grantedServers ?? [];
    }

    public IAsyncEnumerable<VendorEvent> Events => _events.Reader.ReadAllAsync();

    public bool CanResume => _role.Role is VendorRole.Builder;

    public string? ResumeToken => CanResume ? _sessionId : null;

    /// <summary>
    /// `-p` kills a background task about five seconds after the result line, so a task still open
    /// once the turn has ended was lost — unless claude then reports it completed, which is what
    /// `-p` waiting for a background subagent looks like. A task stopped before the result was the
    /// model's own decision and is closed then.
    /// </summary>
    public IReadOnlyList<string> KilledBackgroundTasks => _turnEnded ? [.. _openTasks.Values] : [];

    public async Task<T> RunAsync<T>(string prompt, VendorSchema<T> schema, CancellationToken ct)
    {
        var executable = ClaudeCliVendor.Executable;
        var spec = new ProcessSpec(executable, BuildArguments(schema.Json), _workingDirectory, prompt, BuildEnvironment());
        await _events.Writer.EmitAsync("claude", new VendorEvent(VendorEventKind.Started, _selection.Model), ct);

        JsonElement? structured = null;
        await foreach (var line in StreamingProcess.RunWorkerAsync(spec, ct))
        {
            if (!TryParse(line, out var message)) continue;
            using (message)
            {
                structured = Observe(message.RootElement) ?? structured;
            }
        }

        if (structured is null)
        {
            await _events.Writer.EmitAsync("claude", new VendorEvent(VendorEventKind.Failed, "no structured output"), ct);
            throw new VendorException($"{executable} returned no {StructuredOutputTool} result");
        }

        await _events.Writer.EmitAsync("claude", new VendorEvent(VendorEventKind.Finished, _role.Role.ToString()), ct);
        return structured.Value.Deserialize(schema.TypeInfo)
            ?? throw new VendorException($"{executable} returned a {StructuredOutputTool} result that did not match the schema");
    }

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>Returns the structured payload when this message carries it.</summary>
    /// <param name="root">The parsed JSONL message.</param>
    internal JsonElement? Observe(JsonElement root)
    {
        // A line can parse as JSON without being a message — a bare string, for one. Asking such a
        // root for a property throws, and that crash used to take the whole run down under a
        // misleading output-cap kill (issue #41). Logged rather than dropped: what the CLI emits
        // outside the protocol is exactly what the next post-mortem needs.
        if (root.ValueKind is not JsonValueKind.Object)
        {
            RunLog.Current?.Write("warn", "claude", "vendor.skipped-line",
                ("payload", RunLog.Truncate(root.GetRawText())));
            return null;
        }

        if (root.TryGetProperty("session_id", out var session) && session.GetString() is { } id) _sessionId = id;
        if (!root.TryGetProperty("type", out var type)) return null;

        // Neither carries a `message`, so both used to end at the check below, unlogged — which is
        // how a killed background task left no trace in a run whose process exited 0 (issue #91).
        if (type.GetString() is "result")
        {
            _turnEnded = true;
            return null;
        }

        if (type.GetString() is "system" && ObserveTask(root)) return null;

        if (!root.TryGetProperty("message", out var message)) return null;

        // `message` is an object on every event this reads, but not on every event the CLI emits,
        // and asking one of the others for a property threw straight out through RunAsync — ending
        // a builder that had already applied most of its edits, with only the report lost (run
        // 20260902-224201-7bf03b). Which events carry the other shape is still unmeasured: two
        // captures of a builder calling Bash did not produce one. So the payload is logged rather
        // than dropped, and the next occurrence names itself instead of costing a second
        // post-mortem.
        if (message.ValueKind is not JsonValueKind.Object)
        {
            RunLog.Current?.Write("warn", "claude", "vendor.skipped-message",
                ("type", type.GetString()),
                ("payload", RunLog.Truncate(root.GetRawText())));
            return null;
        }

        if (!message.TryGetProperty("content", out var content)
            || content.ValueKind is not JsonValueKind.Array)
        {
            return null;
        }

        switch (type.GetString())
        {
            case "assistant":
                return ObserveAssistant(content);

            // Tool results ride back to the model as user messages; their outcome — a failed test
            // run, a denied command — is what the run log needs for a post-mortem.
            case "user":
                ObserveToolResults(content);
                return null;

            default:
                return null;
        }
    }

    private JsonElement? ObserveAssistant(JsonElement content)
    {
        JsonElement? structured = null;

        foreach (var block in content.EnumerateArray())
        {
            var kind = TryRead(block, "type", out var blockType) ? blockType.GetString() : null;
            switch (kind)
            {
                case "text" when block.TryGetProperty("text", out var text):
                    _events.Writer.Emit("claude", new VendorEvent(VendorEventKind.Text, text.GetString() ?? string.Empty));
                    break;

                // --json-schema is served by a tool: the object arrives as this call's input.
                case "tool_use" when block.TryGetProperty("name", out var name):
                    var toolName = name.GetString();
                    if (toolName is StructuredOutputTool && block.TryGetProperty("input", out var input))
                    {
                        structured = input.Clone();
                    }
                    else
                    {
                        var detail = ToolInput(block);
                        if (block.TryGetProperty("id", out var callId) && callId.GetString() is { } call)
                        {
                            _toolNames[call] = toolName ?? "?";
                            if (detail?.FirstOrDefault(field => field.Name == "command").Value is { } command)
                                _toolCommands[call] = command;
                        }

                        _events.Writer.Emit("claude", new VendorEvent(VendorEventKind.ToolUse, toolName ?? "?", detail));
                    }
                    break;
            }
        }

        return structured;
    }

    private void ObserveToolResults(JsonElement content)
    {
        foreach (var block in content.EnumerateArray())
        {
            if (!TryRead(block, "type", out var blockType) || blockType.GetString() is not "tool_result") continue;

            var name = block.TryGetProperty("tool_use_id", out var callId)
                       && callId.GetString() is { } call
                       && _toolNames.TryGetValue(call, out var known)
                ? known
                : "?";

            var isError = block.TryGetProperty("is_error", out var flag) && flag.ValueKind is JsonValueKind.True;
            var detail = new List<(string Name, string? Value)> { ("isError", isError ? "true" : "false") };
            if (ResultText(block) is { Length: > 0 } output) detail.Add(("output", RunLog.Tail(output)));

            _events.Writer.Emit("claude", new VendorEvent(VendorEventKind.ToolResult, name, detail));
        }
    }

    /// <summary>
    /// Follows the tasks claude runs for the model — a Bash call, backgrounded or not, and a
    /// subagent — as measured against Claude Code 2.1.273 on 2026-09-17: `task_started` with
    /// `task_id` and `tool_use_id`, `task_notification` with a final `status`, and `task_updated`
    /// with a `patch` whose `status` is `killed` when `-p` ends a task the turn left running.
    /// </summary>
    /// <param name="root">A system message.</param>
    /// <returns>Whether the message was one of these.</returns>
    private bool ObserveTask(JsonElement root)
    {
        if (!TryRead(root, "subtype", out var subtype)) return false;
        var task = TryRead(root, "task_id", out var taskId) ? taskId.GetString() : null;
        string? what = null;
        if (task is not null) _openTasks.TryGetValue(task, out what);

        string? status;
        switch (subtype.GetString())
        {
            case "task_started":
                status = "started";
                what = Describe(root);
                if (task is not null) _openTasks[task] = what;
                break;

            case "task_notification":
                status = TryRead(root, "status", out var final) ? final.GetString() : null;
                if (task is not null && (!_turnEnded || status is "completed" or "failed")) _openTasks.Remove(task);
                break;

            case "task_updated":
                status = TryRead(root, "patch", out var patch) && TryRead(patch, "status", out var changed)
                    ? changed.GetString()
                    : null;
                if (status is null) return true;
                break;

            default:
                return false;
        }

        var fields = new List<(string Name, string? Value)> { ("task", task), ("status", status) };
        if (TryRead(root, "is_backgrounded", out var backgrounded) && backgrounded.ValueKind is JsonValueKind.True or JsonValueKind.False)
            fields.Add(("background", backgrounded.GetBoolean() ? "true" : "false"));
        if (what is not null) fields.Add(("command", what));

        _events.Writer.Emit("claude", new VendorEvent(VendorEventKind.Task, $"{status}: {task}", fields));
        return true;
    }

    /// <summary>The command that started the task where the stream showed one, else what claude called the task.</summary>
    /// <param name="started">A <c>task_started</c> message.</param>
    private string Describe(JsonElement started)
    {
        if (TryRead(started, "tool_use_id", out var call) && call.GetString() is { } id && _toolCommands.TryGetValue(id, out var command))
            return command;

        return TryRead(started, "description", out var description) && description.GetString() is { Length: > 0 } text
            ? text
            : "an unnamed task";
    }

    /// <summary>The command for Bash-shaped tools, the raw input for the rest — cut, not dropped.</summary>
    /// <param name="block">The tool-use content block.</param>
    private static List<(string Name, string? Value)>? ToolInput(JsonElement block)
    {
        if (!block.TryGetProperty("input", out var input)) return null;

        return input.ValueKind is JsonValueKind.Object
               && input.TryGetProperty("command", out var command)
               && command.GetString() is { } line
            ? [("command", line)]
            : [("input", RunLog.Truncate(input.GetRawText()))];
    }

    /// <summary>A tool result's content is either a plain string or an array of text blocks.</summary>
    /// <param name="block">The tool-result content block.</param>
    private static string? ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind is JsonValueKind.String) return content.GetString();
        if (content.ValueKind is not JsonValueKind.Array) return null;

        var pieces = new List<string>();
        foreach (var piece in content.EnumerateArray())
        {
            if (TryRead(piece, "type", out var kind) && kind.GetString() is "text"
                && piece.TryGetProperty("text", out var text) && text.GetString() is { } value)
            {
                pieces.Add(value);
            }
        }

        return pieces.Count == 0 ? null : string.Join("\n", pieces);
    }

    /// <summary>
    /// A property read that survives a value of the wrong kind. Every read above of something the
    /// CLI sent rather than this session built goes through here, because
    /// <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/> throws on anything that is
    /// not an object and a shape nobody predicted should cost a skipped block, not the run.
    /// </summary>
    /// <param name="element">The JSON value that may be an object.</param>
    /// <param name="name">The property name to read.</param>
    /// <param name="value">Receives the property value when present.</param>
    private static bool TryRead(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind is JsonValueKind.Object) return element.TryGetProperty(name, out value);

        value = default;
        return false;
    }

    private static bool TryParse(string line, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(line);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    internal List<string> BuildArguments(string schemaJson)
    {
        var arguments = new List<string>
        {
            "--print",
            "--output-format", "stream-json",
            "--verbose",
            "--json-schema", schemaJson,
            "--append-system-prompt", _role.SystemPrompt,
            "--model", _selection.Model
        };

        // Effort is a flag here; other vendors fold it into the model string. Joining is the
        // vendor's job, not the core's.
        if (!string.IsNullOrWhiteSpace(_selection.Effort))
        {
            arguments.Add("--effort");
            arguments.Add(_selection.Effort);
        }

        // A worker may inherit every other host capability, but never this plugin: disabling the
        // whole plugin keeps its skill, hooks and MCP server out of both worker roles.
        arguments.Add("--settings");
        arguments.Add(SelfPluginSettings);

        // A headless worker asks nobody, so anything its rules do not cover is refused (issue #90).
        // Only a builder gets the shell: a blanket rule is what lifts claude's safety checks as well,
        // which a pattern rule does not, and a critic judges rather than runs.
        List<string> allowed = CanResume ? ["Bash", "PowerShell"] : [];
        allowed.AddRange(_grantedServers.Select(ToolRule));
        if (allowed.Count > 0)
        {
            arguments.Add("--allowedTools");
            arguments.Add(string.Join(",", allowed));
        }

        if (CanResume)
        {
            // The Builder edits files, so it needs its edits to land without a prompt.
            arguments.Add("--permission-mode");
            arguments.Add("acceptEdits");

            if (_sessionId is not null)
            {
                arguments.Add("--resume");
                arguments.Add(_sessionId);
            }
        }
        else
        {
            // A critic is deliberately fresh every round and must not leave a resumable transcript.
            arguments.Add("--no-session-persistence");
        }

        return arguments;
    }

    /// <summary>What every worker process runs with beyond the server's own environment.</summary>
    internal static IReadOnlyDictionary<string, string> BuildEnvironment() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BASH_MAX_TIMEOUT_MS"] = ForegroundCommandLimit
        };

    /// <summary>
    /// The rule granting every tool of a server. It names the server as the server's tools do, which
    /// is where a listed name and a rule part: `plugin:context7:context7` lists with colons and its
    /// tools are `mcp__plugin_context7_context7__…`. A rule matches the whole name or nothing — see
    /// CONTEXT.md.
    /// </summary>
    /// <param name="server">The server name as `claude mcp list` prints it.</param>
    internal static string ToolRule(string server) => "mcp__" + Regex.Replace(server, "[^A-Za-z0-9_-]", "_");
}
