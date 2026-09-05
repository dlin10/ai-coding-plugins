using System.Text.Json;
using System.Threading.Channels;
using PlanForge.Diagnostics;
using PlanForge.Infrastructure;

namespace PlanForge.Vendors.Codex;

/// <summary>
/// One codex conversation, driven through `codex exec` rather than the App Server — see
/// docs/adr/0012-reach-codex-through-exec.md. Modelled on <see cref="Claude.ClaudeCliSession"/>:
/// one process per turn, JSONL on stdout, structure demanded rather than negotiated.
/// </summary>
internal sealed class CodexCliSession : IVendorSession
{
    private static readonly TimeSpan RUN_TIMEOUT = TimeSpan.FromMinutes(20);

    private readonly RoleSpec _role;
    private readonly Selection _selection;
    private readonly string? _workingDirectory;
    private readonly Channel<VendorEvent> _events = Channel.CreateUnbounded<VendorEvent>();

    // Set on the first run so a Builder's later tasks resume the same thread.
    private string? _sessionId;

    // Names an API refusal when the run ends with no result to show for it.
    private string? _lastFailure;

    public CodexCliSession(RoleSpec role, Selection selection, string? workingDirectory, string? resumeToken = null)
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
        var executable = CodexLaunch.Executable;
        var directory = Path.Combine(Path.GetTempPath(), "planforge-codex", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var schemaPath = Path.Combine(directory, "schema.json");
        var resultPath = Path.Combine(directory, "result.json");

        try
        {
            await File.WriteAllTextAsync(schemaPath, schema.Json, ct);

            var inspected = CodexLaunch.Inspect(Environment.GetEnvironmentVariable("PATH"));
            var environment = inspected.Repaired
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = inspected.Path! }
                : null;

            var arguments = BuildArguments(_role, _selection, _sessionId, schemaPath, resultPath);
            var spec = new ProcessSpec(executable, arguments, _workingDirectory, prompt, environment);

            await _events.Writer.EmitAsync("codex", new VendorEvent(VendorEventKind.Started, _selection.Model), ct);

            await foreach (var line in StreamingProcess.RunAsync(spec, RUN_TIMEOUT, ct))
            {
                if (!TryParse(line, out var document)) continue;
                using (document) Observe(document.RootElement);
            }

            var result = File.Exists(resultPath)
                ? JsonSerializer.Deserialize(await File.ReadAllTextAsync(resultPath, ct), schema.TypeInfo)
                : default;

            if (result is null)
            {
                var message = _lastFailure is { Length: > 0 }
                    ? $"codex wrote no result: {_lastFailure}"
                    : "codex wrote no result";
                throw new VendorException(message);
            }

            await _events.Writer.EmitAsync("codex", new VendorEvent(VendorEventKind.Finished, _role.Role.ToString()), ct);
            return result;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The prompt travels on standard input, never as an argument: a code-review prompt carries a
    /// whole diff and would exceed the Windows command-line limit. Static and internal so a test
    /// can pin the exact order without starting a process.
    /// </summary>
    internal static List<string> BuildArguments(RoleSpec role,
                                                 Selection selection,
                                                 string? sessionId,
                                                 string schemaPath,
                                                 string resultPath)
    {
        var arguments = new List<string> { "exec" };

        if (role.Role is VendorRole.Builder && !string.IsNullOrEmpty(sessionId))
        {
            arguments.Add("resume");
            arguments.Add(sessionId);
        }

        arguments.Add("-");
        arguments.Add("--skip-git-repo-check");
        arguments.Add("--json");
        arguments.Add("--output-schema");
        arguments.Add(schemaPath);
        arguments.Add("-o");
        arguments.Add(resultPath);
        arguments.Add("-m");
        arguments.Add(selection.Model);

        if (!string.IsNullOrWhiteSpace(selection.Effort))
        {
            arguments.Add("-c");
            arguments.Add("model_reasoning_effort=" + TomlValue.String(selection.Effort));
        }

        // codex exec resume has no -s, so one spelling of the sandbox covers a builder's first turn
        // and every later one, at the cost of the flag's pre-launch validation of a bad value.
        var sandbox = role.Role is VendorRole.Builder ? "workspace-write" : "read-only";
        arguments.Add("-c");
        arguments.Add("sandbox_mode=" + TomlValue.String(sandbox));

        // The extra roots a builder may write to, as the TOML array `sandbox_workspace_write.writable_roots`
        // takes them (config reference for codex 0.153, checked 2026-09-05). Never for a critic: its
        // sandbox is read-only and the key would widen nothing.
        if (role.Role is VendorRole.Builder && role.WritableRoots is { Count: > 0 } roots)
        {
            arguments.Add("-c");
            arguments.Add("sandbox_workspace_write.writable_roots=[" + string.Join(", ", roots.Select(TomlValue.String)) + "]");
        }

        // Sent on every turn, including a resumed one: a resumed turn keeps the instructions its
        // thread started with, so this is harmless rather than effective, and forge never changes a
        // role mid-session.
        arguments.Add("-c");
        arguments.Add("developer_instructions=" + TomlValue.String(role.SystemPrompt));

        return arguments;
    }

    /// <summary>
    /// Turns one line of `codex exec --json` into events. Internal so the tests can drive it
    /// directly, as they already drive the Claude and Cursor sessions.
    /// </summary>
    internal void Observe(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object)
        {
            RunLog.Current?.Write("warn", "codex", "vendor.skipped-line",
                ("payload", RunLog.Truncate(root.GetRawText())));
            return;
        }

        if (!TryRead(root, "type", out var type)) return;

        switch (type.GetString())
        {
            case "thread.started":
                if (TryRead(root, "thread_id", out var threadId) && threadId.GetString() is { } id)
                    _sessionId = id;
                break;

            case "item.started":
                if (TryItem(root, "command_execution", out var startedItem))
                {
                    List<(string Name, string? Value)>? fields = null;
                    if (TryRead(startedItem, "command", out var command) && command.GetString() is { } commandText)
                        (fields ??= []).Add(("command", commandText));

                    _events.Writer.Emit("codex", new VendorEvent(VendorEventKind.ToolUse, "command_execution", fields));
                }
                break;

            case "item.completed":
                if (TryItem(root, "command_execution", out var completedItem))
                {
                    _events.Writer.Emit("codex",
                        new VendorEvent(VendorEventKind.ToolResult, "command_execution", CommandExecutionDetail(completedItem)));
                }
                else if (TryItem(root, "agent_message", out var messageItem)
                    && TryRead(messageItem, "text", out var text) && text.GetString() is { } message)
                {
                    _events.Writer.Emit("codex", new VendorEvent(VendorEventKind.Text, message));
                }
                break;

            case "turn.failed":
                if (TryRead(root, "error", out var turnError) && TryRead(turnError, "message", out var turnMessage)
                    && turnMessage.GetString() is { } turnReason)
                {
                    _lastFailure = turnReason;
                    _events.Writer.Emit("codex", new VendorEvent(VendorEventKind.Failed, turnReason));
                }
                break;

            case "error":
                if (TryRead(root, "message", out var errorMessage) && errorMessage.GetString() is { } reason)
                {
                    _lastFailure = reason;
                    _events.Writer.Emit("codex", new VendorEvent(VendorEventKind.Failed, reason));
                }
                break;
        }
    }

    /// <summary>The command's fields, carried only when their property is present.</summary>
    private static List<(string Name, string? Value)>? CommandExecutionDetail(JsonElement item)
    {
        List<(string Name, string? Value)>? detail = null;

        void Carry(string name, string? value)
        {
            if (value is null) return;
            (detail ??= []).Add((name, value));
        }

        if (TryRead(item, "command", out var command)) Carry("command", command.GetString());
        if (TryRead(item, "exit_code", out var exitCode) && exitCode.ValueKind is JsonValueKind.Number)
            Carry("exitCode", exitCode.GetRawText());
        if (TryRead(item, "aggregated_output", out var output) && output.GetString() is { } text)
            Carry("output", RunLog.Tail(text));
        if (TryRead(item, "status", out var status)) Carry("status", status.GetString());

        return detail;
    }

    private static bool TryItem(JsonElement root, string type, out JsonElement item)
    {
        if (TryRead(root, "item", out var candidate)
            && TryRead(candidate, "type", out var itemType) && itemType.GetString() == type)
        {
            item = candidate;
            return true;
        }

        item = default;
        return false;
    }

    /// <summary>
    /// A property read that survives a value of the wrong kind, mirroring
    /// <see cref="Claude.ClaudeCliSession"/>'s helper of the same name: a shape nobody predicted
    /// must cost a skipped block, not the run.
    /// </summary>
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
}
