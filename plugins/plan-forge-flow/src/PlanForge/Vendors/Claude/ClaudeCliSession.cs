using System.Text.Json;
using System.Threading.Channels;
using PlanForge.Diagnostics;
using PlanForge.Infrastructure;

namespace PlanForge.Vendors.Claude;

internal sealed class ClaudeCliSession : IVendorSession
{
    private const string StructuredOutputTool = "StructuredOutput";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(20);

    private readonly RoleSpec _role;
    private readonly Selection _selection;
    private readonly string? _workingDirectory;
    private readonly Channel<VendorEvent> _events = Channel.CreateUnbounded<VendorEvent>();

    // Set on the first run so a Builder's later tasks resume the same conversation.
    private string? _sessionId;

    // tool_result blocks name their call by id only; the tool_use block carried the name.
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);

    public ClaudeCliSession(RoleSpec role, Selection selection, string? workingDirectory, string? resumeToken = null)
    {
        _role = role;
        _selection = selection;
        _workingDirectory = workingDirectory;
        _sessionId = resumeToken;
    }

    public IAsyncEnumerable<VendorEvent> Events => _events.Reader.ReadAllAsync();

    public bool CanResume => _role.Role is VendorRole.Builder;

    public string? ResumeToken => CanResume ? _sessionId : null;

    public async Task<T> RunAsync<T>(string prompt, VendorSchema<T> schema, CancellationToken ct)
    {
        var executable = ClaudeCliVendor.Executable;
        var spec = new ProcessSpec(executable, BuildArguments(schema.Json), _workingDirectory, prompt);
        await _events.Writer.EmitAsync("claude", new VendorEvent(VendorEventKind.Started, _selection.Model), ct);

        JsonElement? structured = null;
        await foreach (var line in StreamingProcess.RunAsync(spec, RunTimeout, ct))
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
    internal JsonElement? Observe(JsonElement root)
    {
        if (root.TryGetProperty("session_id", out var session) && session.GetString() is { } id) _sessionId = id;
        if (!root.TryGetProperty("type", out var type)) return null;
        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
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
            var kind = block.TryGetProperty("type", out var blockType) ? blockType.GetString() : null;
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
                        if (block.TryGetProperty("id", out var callId) && callId.GetString() is { } call)
                            _toolNames[call] = toolName ?? "?";

                        _events.Writer.Emit("claude",
                            new VendorEvent(VendorEventKind.ToolUse, toolName ?? "?", ToolInput(block)));
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
            if (!block.TryGetProperty("type", out var blockType) || blockType.GetString() is not "tool_result") continue;

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

    /// <summary>The command for Bash-shaped tools, the raw input for the rest — cut, not dropped.</summary>
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
    private static string? ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind is JsonValueKind.String) return content.GetString();
        if (content.ValueKind is not JsonValueKind.Array) return null;

        var pieces = new List<string>();
        foreach (var piece in content.EnumerateArray())
        {
            if (piece.TryGetProperty("type", out var kind) && kind.GetString() is "text"
                && piece.TryGetProperty("text", out var text) && text.GetString() is { } value)
            {
                pieces.Add(value);
            }
        }

        return pieces.Count == 0 ? null : string.Join("\n", pieces);
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

    private List<string> BuildArguments(string schemaJson)
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

        return arguments;
    }
}
