using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Infrastructure;

namespace PlanForge.Vendors;

/// <summary>
/// The Codex App Server speaks a JSON-RPC-shaped line protocol over stdio — no "jsonrpc" member,
/// requests answered by id, everything else arriving as notifications. It is the one vendor that
/// holds a connection open across calls rather than running a process per call, so it cannot use
/// <see cref="StreamingProcess"/> and needs its own reader loop.
/// </summary>
internal sealed class AppServerConnection : IAsyncDisposable
{
    private const string Command = "codex";
    private const int MaxStderrLines = 20;
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(3);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Process _process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Queue<string> _stderrLines = new();
    private readonly object _stderrGate = new();
    private readonly Task _readerTask;
    private readonly Task _stderrTask;

    private long _nextId;
    private int _disposeStarted;

    private AppServerConnection(Process process)
    {
        _process = process;
        _readerTask = ReadMessagesAsync();
        _stderrTask = ReadStandardErrorAsync();
    }

    /// <summary>Every server notification, as method name and params. Set before the first request.</summary>
    public Action<string, JsonElement>? Notified { get; set; }

    public static AppServerConnection Start(string? workingDirectory)
    {
        var executable = ExecutableResolver.Resolve(Command)
            ?? throw new VendorException($"{Command} was not found on PATH");

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Without this the writer emits a BOM and the server rejects the first message.
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory)) startInfo.WorkingDirectory = workingDirectory;

        // npm ships codex as a .cmd that launches node, so there is no .exe for the resolver to
        // unwrap and the shim has to go through cmd.exe. That intermediate process is why disposal
        // kills the whole tree. The arguments are literals, so cmd re-parsing them is harmless.
        if (Path.GetExtension(executable) is ".cmd" or ".bat")
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.Arguments = $"/d /s /c \"\"{executable}\" app-server --stdio\"";
        }
        else
        {
            startInfo.FileName = executable;
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--stdio");
        }

        try
        {
            var process = Process.Start(startInfo) ?? throw new VendorException($"{executable} did not start");
            return new AppServerConnection(process);
        }
        catch (Win32Exception error)
        {
            throw new VendorException($"could not start {executable}: {error.Message}");
        }
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        var version = typeof(AppServerConnection).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        await RequestAsync("initialize", new JsonObject
        {
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "plan_forge_flow",
                ["title"] = "Plan Forge Flow",
                ["version"] = version
            }
        }, ct);

        await SendAsync(new JsonObject { ["method"] = "initialized", ["params"] = new JsonObject() }, ct);
    }

    public async Task<JsonElement> RequestAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            await SendAsync(new JsonObject { ["method"] = method, ["id"] = id, ["params"] = parameters }, ct);
            var response = await completion.Task.WaitAsync(ct);

            if (response.TryGetProperty("error", out var error)) throw Failed(method, error);
            if (!response.TryGetProperty("result", out var result))
                throw new VendorException($"{method} returned neither result nor error");

            return result.Clone();
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;

        try
        {
            // Closing stdin is how the server is asked to leave; killing is the fallback, not the
            // first move, so that it can finish writing its session files.
            await _writeLock.WaitAsync();
            try { _process.StandardInput.Close(); }
            finally { _writeLock.Release(); }

            if (!_process.HasExited)
            {
                try { await _process.WaitForExitAsync().WaitAsync(ExitGrace); }
                catch (TimeoutException)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            // The server was already gone; the reader loop has told everyone waiting.
        }
        finally
        {
            await _lifetime.CancelAsync();
            try { await Task.WhenAll(_readerTask, _stderrTask); } catch (Exception) { }

            _process.Dispose();
            _lifetime.Dispose();
            _writeLock.Dispose();
        }
    }

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            throw new VendorException("the Codex App Server connection was closed");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadMessagesAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync(_lifetime.Token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;

                if (message.TryGetProperty("method", out var method))
                {
                    // A request from the server, not a notification. Declining keeps the connection
                    // alive; the old client treated this as fatal and lost the whole act.
                    if (message.TryGetProperty("id", out var requestId))
                        await DeclineAsync(requestId, method.GetString() ?? "unknown");
                    else if (message.TryGetProperty("params", out var parameters))
                        Notified?.Invoke(method.GetString() ?? string.Empty, parameters.Clone());

                    continue;
                }

                if (message.TryGetProperty("id", out var id) && id.TryGetInt64(out var value)
                    && _pending.TryRemove(value, out var completion))
                {
                    completion.TrySetResult(message.Clone());
                }
            }

            if (Volatile.Read(ref _disposeStarted) == 0) Fail(new VendorException(ExitMessage()));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Fail(error as VendorException
                 ?? new VendorException($"the Codex App Server sent an unreadable message: {error.Message}"));
        }
    }

    private async Task DeclineAsync(JsonElement requestId, string method)
    {
        var response = new JsonObject
        {
            ["id"] = JsonNode.Parse(requestId.GetRawText()),
            ["error"] = new JsonObject
            {
                ["code"] = -32601,
                ["message"] = $"Plan Forge Flow does not serve the request '{method}'"
            }
        };

        try { await SendAsync(response, _lifetime.Token); }
        catch (Exception error) when (error is VendorException or OperationCanceledException) { }
    }

    private async Task ReadStandardErrorAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync(_lifetime.Token) is { } line)
            {
                lock (_stderrGate)
                {
                    _stderrLines.Enqueue(line);
                    while (_stderrLines.Count > MaxStderrLines) _stderrLines.Dequeue();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void Fail(Exception error)
    {
        foreach (var pending in _pending)
            if (_pending.TryRemove(pending.Key, out var completion))
                completion.TrySetException(error);
    }

    private string ExitMessage()
    {
        var message = _process.HasExited
            ? $"the Codex App Server exited with code {_process.ExitCode}"
            : "the Codex App Server closed its output";

        lock (_stderrGate)
            return _stderrLines.Count == 0 ? message : $"{message}: {string.Join(" | ", _stderrLines)}";
    }

    private static VendorException Failed(string method, JsonElement error)
    {
        var code = error.TryGetProperty("code", out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;
        var message = error.TryGetProperty("message", out var text) ? text.GetString() : null;
        return new VendorException($"{method} failed ({code}): {message ?? "unknown error"}");
    }
}
