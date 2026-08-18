using System.Text.Json;
using System.Threading.Channels;
using PlanForge.Infrastructure;

namespace PlanForge.Vendors.Cursor;

/// <summary>
/// Has no structured output tool, so it reaches structure through <see cref="SchemaInPrompt"/>:
/// schema in the prompt, validation here, one retry before the call is declared failed.
/// </summary>
internal sealed class CursorAgentSession : IVendorSession
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(20);

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

    private async Task<string> ReadResultAsync(ProcessSpec spec, CancellationToken ct)
    {
        var result = string.Empty;
        await foreach (var line in StreamingProcess.RunAsync(spec, RunTimeout, ct))
        {
            JsonDocument message;
            try { message = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (message)
            {
                var root = message.RootElement;
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
        var arguments = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--force",
            "--trust",
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

    /// <summary>Cursor carries effort inside the model id, so the join happens here, not in the core.</summary>
    internal string ModelWithEffort() =>
        string.IsNullOrWhiteSpace(_selection.Effort)
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
