using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForgeFlow.Cli;

namespace PlanForgeFlow.OpenAI;

internal sealed class AppServerClient : IDisposable
{
    internal static string ClientVersion => typeof(AppServerClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    private const int MaxLineChars = 8 * 1024 * 1024;
    private readonly TextReader reader;
    private readonly TextWriter writer;
    private readonly Process? process;
    private readonly List<AppServerNotification> notifications = [];
    private long nextId;
    private bool initialized;

    internal TimeSpan CancellationGracePeriod { get; set; } = TimeSpan.FromSeconds(10);
    internal TimeSpan CleanupGracePeriod { get; set; } = TimeSpan.FromSeconds(2);

    internal AppServerClient(TextReader reader, TextWriter writer)
    {
        this.reader = reader;
        this.writer = writer;
    }

    private AppServerClient(Process process)
    {
        this.process = process;
        reader = process.StandardOutput;
        writer = process.StandardInput;
    }

    public IReadOnlyList<AppServerNotification> Notifications => notifications;

    public static AppServerClient Start(string executable = "codex")
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("app-server");
        try
        {
            if (!process.Start()) throw new InvalidOperationException("process did not start");
            return new AppServerClient(process);
        }
        catch (Exception error)
        {
            process.Dispose();
            throw new CliFailure("process", $"could not start Codex App Server: {error.Message}", 3);
        }
    }

    public void Initialize()
    {
        if (initialized) throw new CliFailure("protocol", "App Server connection is already initialized", 3);
        Request("initialize", new JsonObject
        {
            ["clientInfo"] = new JsonObject { ["name"] = "plan_forge_flow", ["title"] = "Plan Forge Flow", ["version"] = ClientVersion },
        }, allowBeforeInitialize: true);
        Notify("initialized", new JsonObject());
        initialized = true;
    }

    public void ValidateOpenAiAccount()
        => AppServerContracts.ValidateAccount(Request("account/read", new JsonObject { ["refreshToken"] = false }));

    public IReadOnlyList<AppServerModel> ListModels()
    {
        var models = new List<AppServerModel>();
        string? cursor = null;
        do
        {
            var parameters = new JsonObject { ["limit"] = 100, ["includeHidden"] = false };
            if (cursor is not null) parameters["cursor"] = cursor;
            var result = Request("model/list", parameters);
            models.AddRange(AppServerContracts.ParseModels(result));
            cursor = AppServerContracts.NextCursor(result);
        } while (cursor is not null);
        if (models.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count() != models.Count)
        {
            throw new CliFailure("protocol", "model/list returned duplicate model IDs", 3);
        }
        return models;
    }

    public AppServerThread StartThread(string workspace, AppServerModel selection)
    {
        var result = Request("thread/start", new JsonObject
        {
            ["model"] = selection.Id,
            ["cwd"] = workspace,
            ["approvalPolicy"] = "never",
            ["sandbox"] = "readOnly",
            ["serviceName"] = "plan_forge_flow",
        });
        return AppServerContracts.ParseThread(result, null, selection.Id, requireStatus: false, requireModel: true);
    }

    public AppServerThread ResumeThread(string threadId, string workspace, AppServerModel selection, bool writable)
    {
        var result = Request("thread/resume", new JsonObject
        {
            ["threadId"] = threadId,
            ["model"] = selection.Id,
            ["cwd"] = workspace,
            ["approvalPolicy"] = "never",
            ["sandbox"] = writable ? "workspaceWrite" : "readOnly",
        });
        return AppServerContracts.ParseThread(result, threadId, selection.Id, requireStatus: false, requireModel: true);
    }

    public AppServerThread ReadThread(string threadId)
        => AppServerContracts.ParseThread(Request("thread/read", new JsonObject { ["threadId"] = threadId, ["includeTurns"] = false }), threadId, null, requireStatus: true);

    public void DeleteThread(string threadId, Action? heartbeat = null)
    {
        try
        {
            RequestCore("thread/delete", new JsonObject { ["threadId"] = threadId }, false,
                        DateTimeOffset.UtcNow + CleanupGracePeriod, heartbeat);
        }
        catch (PollingTimeoutException)
        {
            throw new CliFailure("process", "App Server did not acknowledge thread/delete before the cleanup deadline", 3);
        }
    }

    public void DeleteThreadBestEffort(string threadId, Action? heartbeat = null)
    {
        try { DeleteThread(threadId, heartbeat); }
        catch (Exception) { }
    }

    public AppServerTurn StartTurn(string threadId, string prompt, string workspace, AppServerModel selection, string effort, bool writable)
    {
        var sandboxPolicy = writable
            ? new JsonObject { ["type"] = "workspaceWrite", ["writableRoots"] = new JsonArray(workspace), ["networkAccess"] = true }
            : new JsonObject { ["type"] = "readOnly", ["access"] = new JsonObject { ["type"] = "fullAccess" } };
        var result = Request("turn/start", new JsonObject
        {
            ["threadId"] = threadId,
            ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt }),
            ["cwd"] = workspace,
            ["approvalPolicy"] = "never",
            ["sandboxPolicy"] = sandboxPolicy,
            ["model"] = selection.Id,
            ["effort"] = effort,
        });
        var turn = AppServerContracts.ParseTurn(result);
        if (!string.Equals(turn.Status, "inProgress", StringComparison.Ordinal)) throw new CliFailure("protocol", "turn/start did not return inProgress", 3);
        return turn;
    }

    public AppServerTurn WaitForTurn(string threadId, string turnId, Func<bool>? cancellationRequested = null, Action? heartbeat = null)
    {
        var interruptRequested = false;
        DateTimeOffset? interruptDeadline = null;
        while (true)
        {
            if (!interruptRequested && cancellationRequested?.Invoke() == true)
            {
                interruptRequested = true;
                interruptDeadline = DateTimeOffset.UtcNow + CancellationGracePeriod;
                try { RequestCore("turn/interrupt", new JsonObject { ["threadId"] = threadId, ["turnId"] = turnId }, false, interruptDeadline, heartbeat); }
                catch (PollingTimeoutException) { throw new OperationCanceledException("App Server did not acknowledge turn/interrupt before the cancellation deadline"); }
            }
            var queued = notifications.Where(notification => notification.Method == "turn/completed")
                                      .Select(notification => AppServerContracts.ParseTurn(notification.Params))
                                      .FirstOrDefault(turn => turn.Id == turnId);
            if (queued is not null)
            {
                if (interruptRequested && queued.Status == "interrupted") throw new OperationCanceledException();
                return queued;
            }
            JsonElement message;
            try
            {
                message = ReadMessagePolling(heartbeat ?? (() => { }),
                                             interruptRequested ? null : cancellationRequested,
                                             interruptDeadline);
            }
            catch (PollingInterruptedException)
            {
                continue;
            }
            catch (PollingTimeoutException)
            {
                throw new OperationCanceledException("App Server did not complete the interrupted turn before the cancellation deadline");
            }
            if (message.TryGetProperty("id", out _) && message.TryGetProperty("method", out var serverMethod))
            {
                throw new CliFailure("protocol", $"unsupported App Server request: {serverMethod.GetString()}", 3);
            }
            if (message.TryGetProperty("method", out var methodValue) && methodValue.ValueKind == JsonValueKind.String)
            {
                var method = methodValue.GetString()!;
                var parameters = message.TryGetProperty("params", out var value) ? value.Clone() : EmptyObject();
                notifications.Add(new AppServerNotification(method, parameters));
                if (method == "turn/completed")
                {
                    var turn = AppServerContracts.ParseTurn(parameters);
                    if (turn.Id == turnId)
                    {
                        if (interruptRequested && turn.Status == "interrupted") throw new OperationCanceledException();
                        return turn;
                    }
                }
                continue;
            }
            throw new CliFailure("protocol", "unexpected App Server response while waiting for turn completion", 3);
        }
    }

    public JsonElement Request(string method, JsonObject parameters, bool allowBeforeInitialize = false)
        => RequestCore(method, parameters, allowBeforeInitialize, null, null);

    private JsonElement RequestCore(string method,
                                    JsonObject parameters,
                                    bool allowBeforeInitialize,
                                    DateTimeOffset? deadline,
                                    Action? poll)
    {
        if (!initialized && !allowBeforeInitialize) throw new CliFailure("protocol", "App Server connection is not initialized", 3);
        var id = Interlocked.Increment(ref nextId);
        Write(RequestObject(method, id, parameters));
        while (true)
        {
            var message = deadline is null ? ReadMessage() : ReadMessagePolling(poll ?? (() => { }), null, deadline);
            if (message.TryGetProperty("id", out _) && message.TryGetProperty("method", out var serverMethod))
            {
                throw new CliFailure("protocol", $"unsupported App Server request: {serverMethod.GetString()}", 3);
            }
            if (!message.TryGetProperty("id", out var responseId))
            {
                if (!message.TryGetProperty("method", out var notificationMethod) || notificationMethod.ValueKind != JsonValueKind.String)
                {
                    throw new CliFailure("protocol", "App Server notification is missing method", 3);
                }
                notifications.Add(new AppServerNotification(notificationMethod.GetString()!,
                    message.TryGetProperty("params", out var value) ? value.Clone() : EmptyObject()));
                continue;
            }
            if (!responseId.TryGetInt64(out var actualId) || actualId != id)
            {
                throw new CliFailure("protocol", "App Server response ID does not match the request", 3);
            }
            if (message.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var codeValue) && codeValue.TryGetInt32(out var parsedCode) ? parsedCode : 0;
                var text = error.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.String
                               ? messageValue.GetString()
                               : "unknown App Server error";
                throw new CliFailure("protocol", $"App Server request {method} failed ({code}): {text}", 3);
            }
            if (!message.TryGetProperty("result", out var result)) throw new CliFailure("protocol", "App Server response is missing result", 3);
            return result.Clone();
        }
    }

    public void Dispose()
    {
        try { writer.Dispose(); } catch { }
        try { reader.Dispose(); } catch { }
        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
    }

    private void Notify(string method, JsonObject parameters)
        => Write(new JsonObject { ["method"] = method, ["params"] = parameters });

    private void Write(JsonObject message)
    {
        writer.WriteLine(message.ToJsonString());
        writer.Flush();
    }

    private static JsonObject RequestObject(string method, long id, JsonObject parameters)
        => new() { ["method"] = method, ["id"] = id, ["params"] = parameters };

    private JsonElement ReadMessage()
    {
        string? line;
        try { line = reader.ReadLine(); }
        catch (Exception error) { throw new CliFailure("process", $"Codex App Server output failed: {error.Message}", 3); }
        if (line is null)
        {
            var stderr = process is { HasExited: true } ? process.StandardError.ReadToEnd().Trim() : string.Empty;
            throw new CliFailure("process", string.IsNullOrEmpty(stderr) ? "Codex App Server closed its output" : $"Codex App Server exited: {stderr}", 3);
        }
        if (line.Length > MaxLineChars) throw new CliFailure("protocol", "Codex App Server message exceeded the size limit", 3);
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("message root is not an object");
            return document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new CliFailure("protocol", $"Codex App Server emitted invalid JSON: {error.Message}", 3);
        }
    }

    private JsonElement ReadMessagePolling(Action poll,
                                           Func<bool>? cancellationRequested = null,
                                           DateTimeOffset? deadline = null)
    {
        using var cancellation = new CancellationTokenSource();
        var read = reader.ReadLineAsync(cancellation.Token).AsTask();
        while (!read.IsCompleted)
        {
            poll();
            if (cancellationRequested?.Invoke() == true)
            {
                cancellation.Cancel();
                ObserveCancelledRead(read);
                throw new PollingInterruptedException();
            }
            if (deadline is { } value && DateTimeOffset.UtcNow >= value)
            {
                cancellation.Cancel();
                ObserveCancelledRead(read);
                throw new PollingTimeoutException();
            }
            if (process is { HasExited: true }) break;
            try { read.Wait(TimeSpan.FromMilliseconds(100)); }
            catch (AggregateException error) { throw new CliFailure("process", $"Codex App Server output failed: {error.GetBaseException().Message}", 3); }
        }
        string? line;
        try { line = read.GetAwaiter().GetResult(); }
        catch (Exception error) { throw new CliFailure("process", $"Codex App Server output failed: {error.Message}", 3); }
        if (line is null) throw new CliFailure("process", "Codex App Server closed its output", 3);
        if (line.Length > MaxLineChars) throw new CliFailure("protocol", "Codex App Server message exceeded the size limit", 3);
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("message root is not an object");
            return document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new CliFailure("protocol", $"Codex App Server emitted invalid JSON: {error.Message}", 3);
        }
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static void ObserveCancelledRead(Task<string?> read)
    {
        try { read.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
    }

    private sealed class PollingInterruptedException : Exception;
    private sealed class PollingTimeoutException : Exception;
}
