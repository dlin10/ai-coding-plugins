using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace PlanForge.Vendors.Codex;

/// <summary>
/// One Codex thread. The App Server has no schema field, so structure is reached the same way as
/// with Cursor — schema in the prompt, validation here, one retry. A session runs one turn at a
/// time, which is what lets notifications be matched by thread alone.
/// </summary>
internal sealed class CodexAppServerSession : IVendorSession
{
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan InterruptGrace = TimeSpan.FromSeconds(2);

    private readonly AppServerConnection _connection;
    private readonly string _threadId;
    private readonly RoleSpec _role;
    private readonly Selection _selection;
    private readonly string _workspace;
    private readonly Channel<VendorEvent> _events = Channel.CreateUnbounded<VendorEvent>();

    private volatile Turn? _turn;

    public CodexAppServerSession(AppServerConnection connection,
                                 string threadId,
                                 RoleSpec role,
                                 Selection selection,
                                 string workspace)
    {
        _connection = connection;
        _threadId = threadId;
        _role = role;
        _selection = selection;
        _workspace = workspace;
        _connection.Notified = Observe;
    }

    public IAsyncEnumerable<VendorEvent> Events => _events.Reader.ReadAllAsync();

    public bool CanResume => _role.Role is VendorRole.Builder;

    public string? ResumeToken => CanResume ? _threadId : null;

    public async Task<T> RunAsync<T>(string prompt, VendorSchema<T> schema, CancellationToken ct)
    {
        string? lastFailure = null;
        for (var attempt = 1; attempt <= SchemaInPrompt.MaxAttempts; attempt++)
        {
            await _events.Writer.WriteAsync(new VendorEvent(VendorEventKind.Started, $"attempt {attempt}"), ct);

            var text = await RunTurnAsync(SchemaInPrompt.Compose(prompt, schema.Json, lastFailure), ct);
            if (SchemaInPrompt.TryExtract(text, schema, out var value, out lastFailure))
            {
                await _events.Writer.WriteAsync(new VendorEvent(VendorEventKind.Finished, _role.Role.ToString()), ct);
                return value;
            }

            await _events.Writer.WriteAsync(new VendorEvent(VendorEventKind.Failed, lastFailure ?? "invalid reply"), ct);
        }

        throw new VendorException($"codex did not return a valid object in {SchemaInPrompt.MaxAttempts} attempts: {lastFailure}");
    }

    public async ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        _connection.Notified = null;
        await _connection.DisposeAsync();
    }

    private async Task<string> RunTurnAsync(string prompt, CancellationToken ct)
    {
        var turn = new Turn(_threadId);
        _turn = turn;

        try
        {
            var result = await _connection.RequestAsync("turn/start", TurnParameters(prompt), ct);
            if (!result.TryGetProperty("turn", out var started)
                || !started.TryGetProperty("id", out var id)
                || id.GetString() is not { Length: > 0 } turnId)
            {
                throw new VendorException("turn/start returned no turn id");
            }

            try
            {
                return await turn.Completion.Task.WaitAsync(TurnTimeout, ct);
            }
            catch (OperationCanceledException)
            {
                await InterruptAsync(turnId);
                throw;
            }
        }
        finally
        {
            _turn = null;
        }
    }

    private JsonObject TurnParameters(string prompt)
    {
        var builder = _role.Role is VendorRole.Builder;
        var sandbox = builder
            ? new JsonObject
            {
                ["type"] = "workspaceWrite",
                ["writableRoots"] = new JsonArray(_workspace),
                ["networkAccess"] = true
            }
            : new JsonObject { ["type"] = "readOnly", ["networkAccess"] = true };

        var parameters = new JsonObject
        {
            ["threadId"] = _threadId,
            ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt }),
            ["cwd"] = _workspace,
            ["approvalPolicy"] = "never",
            ["sandboxPolicy"] = sandbox,
            ["model"] = _selection.Model
        };

        // Codex takes effort per turn rather than baking it into the model id, and validates it
        // upstream: a bad level fails the turn rather than the request.
        if (!string.IsNullOrWhiteSpace(_selection.Effort)) parameters["effort"] = _selection.Effort;

        return parameters;
    }

    private async Task InterruptAsync(string turnId)
    {
        using var grace = new CancellationTokenSource(InterruptGrace);
        var parameters = new JsonObject { ["threadId"] = _threadId, ["turnId"] = turnId };

        try { await _connection.RequestAsync("turn/interrupt", parameters, grace.Token); }
        catch (Exception) { /* Disposal closes the connection anyway. */ }
    }

    private void Observe(string method, JsonElement parameters)
    {
        var turn = _turn;
        if (turn is null || !turn.Owns(parameters)) return;

        switch (method)
        {
            case "item/agentMessage/delta":
                if (parameters.TryGetProperty("delta", out var delta) && delta.GetString() is { } text)
                    _events.Writer.TryWrite(new VendorEvent(VendorEventKind.Text, text));
                break;

            case "item/started":
                if (ItemType(parameters) is { } startedType and not "userMessage" and not "agentMessage")
                    _events.Writer.TryWrite(new VendorEvent(VendorEventKind.ToolUse, startedType));
                break;

            case "item/completed":
                if (ItemType(parameters) is "agentMessage"
                    && parameters.GetProperty("item").TryGetProperty("text", out var message)
                    && message.GetString() is { Length: > 0 } body)
                {
                    turn.Say(body);
                }
                break;

            // A retryable error is the server's business; a final one is the turn's outcome.
            case "error":
                if (parameters.TryGetProperty("willRetry", out var retry) && retry.ValueKind is JsonValueKind.False
                    && parameters.TryGetProperty("error", out var failure)
                    && failure.TryGetProperty("message", out var reason))
                {
                    turn.Fail(reason.GetString());
                }
                break;

            case "turn/completed":
                turn.Complete(parameters);
                break;
        }
    }

    private static string? ItemType(JsonElement parameters) =>
        parameters.TryGetProperty("item", out var item) && item.TryGetProperty("type", out var type)
            ? type.GetString()
            : null;

    /// <summary>Collects one turn's output and settles when the server says the turn is over.</summary>
    private sealed class Turn(string threadId)
    {
        private readonly List<string> _messages = [];
        private readonly Lock _gate = new();
        private string? _failure;

        public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Owns(JsonElement parameters) =>
            parameters.TryGetProperty("threadId", out var thread) && thread.GetString() == threadId;

        public void Say(string message)
        {
            lock (_gate) _messages.Add(message);
        }

        public void Fail(string? reason)
        {
            lock (_gate) _failure = reason ?? "unknown error";
        }

        public void Complete(JsonElement parameters)
        {
            var status = "completed";
            if (parameters.TryGetProperty("turn", out var turn))
            {
                if (turn.TryGetProperty("status", out var value) && value.GetString() is { } reported) status = reported;
                if (turn.TryGetProperty("error", out var error) && error.ValueKind is JsonValueKind.Object
                    && error.TryGetProperty("message", out var message))
                {
                    Fail(message.GetString());
                }
            }

            if (status is "interrupted")
            {
                Completion.TrySetCanceled();
                return;
            }

            lock (_gate)
            {
                if (status is not "completed")
                {
                    Completion.TrySetException(new VendorException($"the Codex turn {status}: {_failure ?? "no reason given"}"));
                    return;
                }

                // The last agent message is the answer; earlier ones are progress narration.
                Completion.TrySetResult(_messages.Count == 0 ? string.Empty : _messages[^1]);
            }
        }
    }
}
