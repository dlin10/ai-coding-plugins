using System.Text.Json;
using System.Threading.Channels;
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
        await _events.Writer.WriteAsync(new VendorEvent(VendorEventKind.Started, _selection.Model), ct);

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
            await _events.Writer.WriteAsync(new VendorEvent(VendorEventKind.Failed, "no structured output"), ct);
            throw new VendorException($"{executable} returned no {StructuredOutputTool} result");
        }

        await _events.Writer.WriteAsync(new VendorEvent(VendorEventKind.Finished, _role.Role.ToString()), ct);
        return structured.Value.Deserialize(schema.TypeInfo)
            ?? throw new VendorException($"{executable} returned a {StructuredOutputTool} result that did not match the schema");
    }

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>Returns the structured payload when this message carries it.</summary>
    private JsonElement? Observe(JsonElement root)
    {
        if (root.TryGetProperty("session_id", out var session) && session.GetString() is { } id) _sessionId = id;
        if (!root.TryGetProperty("type", out var type) || type.GetString() is not "assistant") return null;
        if (!root.TryGetProperty("message", out var message)) return null;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind is not JsonValueKind.Array) return null;

        JsonElement? structured = null;

        foreach (var block in content.EnumerateArray())
        {
            var kind = block.TryGetProperty("type", out var blockType) ? blockType.GetString() : null;
            switch (kind)
            {
                case "text" when block.TryGetProperty("text", out var text):
                    _events.Writer.TryWrite(new VendorEvent(VendorEventKind.Text, text.GetString() ?? string.Empty));
                    break;

                // --json-schema is served by a tool: the object arrives as this call's input.
                case "tool_use" when block.TryGetProperty("name", out var name):
                    var toolName = name.GetString();
                    if (toolName is StructuredOutputTool && block.TryGetProperty("input", out var input))
                        structured = input.Clone();
                    else
                        _events.Writer.TryWrite(new VendorEvent(VendorEventKind.ToolUse, toolName ?? "?"));
                    break;
            }
        }

        return structured;
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
