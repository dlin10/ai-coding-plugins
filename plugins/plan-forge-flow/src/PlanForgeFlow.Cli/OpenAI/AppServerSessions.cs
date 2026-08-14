using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.OpenAI;

internal sealed record AppServerSessionRequest(string Id,
                                               string Workspace,
                                               string Role,
                                               string Model,
                                               string Effort,
                                               string? ThreadId,
                                               string Prompt);

internal sealed record AppServerSessionState(int SchemaVersion,
                                             string Id,
                                             int? ProcessId,
                                             string Status,
                                             string CreatedAt,
                                             string HeartbeatAt,
                                             string? ThreadId = null,
                                             string? TurnId = null,
                                             string? ErrorCode = null,
                                             string? Error = null);

internal sealed record AppServerSessionResult(string Id,
                                              string Status,
                                              AppServerAgentResult? Agent = null,
                                              string? ErrorCode = null,
                                              string? Error = null);

internal static class AppServerSessions
{
    private const int SchemaVersion = 1;
    internal static Func<ProcessStartInfo, Process> WorkerLauncher { get; set; } = startInfo
        => Process.Start(startInfo) ?? throw new InvalidOperationException("process did not start");
    internal static Func<AppServerClient> ClientFactory { get; set; } = () => AppServerClient.Start();
    internal static Action? BeforeTerminalPublish { get; set; }

    public static AppServerSessionState Start(string workspace,
                                              string role,
                                              string model,
                                              string effort,
                                              string? threadId,
                                              string prompt,
                                              HostKind host = HostKind.Codex)
    {
        workspace = RepositoryPaths.CanonicalWorkspaceRoot(workspace);
        if (role is not ("reviewer" or "builder-hold" or "builder-resume")) throw new CliFailure("usage", "session role must be reviewer, builder-hold, or builder-resume");
        if (role == "builder-resume" && string.IsNullOrWhiteSpace(threadId)) throw new CliFailure("usage", "builder-resume requires --thread-id");
        if (role != "builder-resume" && threadId is not null) throw new CliFailure("usage", "--thread-id is valid only for builder-resume");
        if (string.IsNullOrWhiteSpace(prompt)) throw new CliFailure("usage", "session start requires a non-empty prompt on stdin");

        var id = Guid.NewGuid().ToString("N");
        var request = new AppServerSessionRequest(id, workspace, role, model, effort, threadId, prompt);
        var created = DateTimeOffset.UtcNow.ToString("O");
        var state = new AppServerSessionState(SchemaVersion, id, null, "starting", created, created, threadId);
        Write(RequestPath(workspace, id, host), request, ForgeJsonContext.Default.AppServerSessionRequest);
        Write(StatePath(workspace, id, host), state, ForgeJsonContext.Default.AppServerSessionState);
        Process process;
        try { process = StartWorker(workspace, id, host); }
        catch (CliFailure error)
        {
            state = state with { Status = "failed", HeartbeatAt = DateTimeOffset.UtcNow.ToString("O"), ErrorCode = error.Code, Error = error.Message };
            PublishTerminal(workspace, id, state,
                            new AppServerSessionResult(id, state.Status, ErrorCode: state.ErrorCode, Error: state.Error), host);
            throw;
        }
        using (AcquireSessionLock(workspace, id, host))
        {
            var current = Read(StatePath(workspace, id, host), ForgeJsonContext.Default.AppServerSessionState);
            if (current.Status == "starting")
            {
                state = current with { ProcessId = process.Id };
                Write(StatePath(workspace, id, host), state, ForgeJsonContext.Default.AppServerSessionState);
            }
            else state = current;
        }
        process.Dispose();
        return state;
    }

    public static AppServerSessionState Status(string workspace, string id, HostKind host = HostKind.Codex)
    {
        workspace = RepositoryPaths.CanonicalWorkspaceRoot(workspace);
        id = ValidateId(id);
        using (AcquireSessionLock(workspace, id, host))
        {
            var state = Read(StatePath(workspace, id, host), ForgeJsonContext.Default.AppServerSessionState);
            if (state.SchemaVersion != SchemaVersion) throw new CliFailure("unsupported-state-schema", "App Server session schema is unsupported", 3);
            if (state.Status is "starting" or "running" or "cancelling" && state.ProcessId is { } pid && !IsRunning(pid))
            {
                var cancellationRequested = state.Status == "cancelling" || File.Exists(CancelPath(workspace, id, host));
                state = state with
                {
                    Status = cancellationRequested ? "cancelled" : "failed",
                    HeartbeatAt = DateTimeOffset.UtcNow.ToString("O"),
                    ErrorCode = cancellationRequested ? "cancelled" : "process",
                    Error = cancellationRequested ? "session cancellation was requested before the detached worker exited" :
                                                    "detached worker process exited without an atomic result",
                };
                PublishTerminal(workspace, id, state,
                                new AppServerSessionResult(id, state.Status, ErrorCode: state.ErrorCode, Error: state.Error), host, lockHeld: true);
            }
            return state;
        }
    }

    public static AppServerSessionResult Result(string workspace, string id, HostKind host = HostKind.Codex)
    {
        var state = Status(workspace, id, host);
        if (state.Status is not ("completed" or "cancelled" or "failed")) throw new CliFailure("state", $"App Server session is {state.Status}: {state.Error}", 3);
        return Read(ResultPath(workspace, id, host), ForgeJsonContext.Default.AppServerSessionResult);
    }

    public static AppServerSessionState Cancel(string workspace, string id, HostKind host = HostKind.Codex)
    {
        workspace = RepositoryPaths.CanonicalWorkspaceRoot(workspace);
        id = ValidateId(id);
        using (AcquireSessionLock(workspace, id, host))
        {
            var state = Read(StatePath(workspace, id, host), ForgeJsonContext.Default.AppServerSessionState);
            if (state.Status is "completed" or "failed" or "cancelled") return state;
            DurableFiles.WriteAtomic(CancelPath(workspace, id, host), DateTimeOffset.UtcNow.ToString("O"));
            state = Read(StatePath(workspace, id, host), ForgeJsonContext.Default.AppServerSessionState);
            if (state.Status is "completed" or "failed" or "cancelled") return state;
            state = state with { Status = "cancelling", HeartbeatAt = DateTimeOffset.UtcNow.ToString("O") };
            Write(StatePath(workspace, id, host), state, ForgeJsonContext.Default.AppServerSessionState);
            return state;
        }
    }

    public static void RunWorker(string workspace, string id, HostKind host = HostKind.Codex)
    {
        id = ValidateId(id);
        WaitForParentStart(workspace, id, host);
        var request = Read(RequestPath(workspace, id, host), ForgeJsonContext.Default.AppServerSessionRequest);
        if (!string.Equals(request.Workspace, RepositoryPaths.CanonicalWorkspaceRoot(workspace), StringComparison.Ordinal))
        {
            throw new CliFailure("identity", "App Server worker workspace does not match its request", 3);
        }
        var initial = Read(StatePath(workspace, id, host), ForgeJsonContext.Default.AppServerSessionState);
        var state = initial with { ProcessId = Environment.ProcessId, Status = "running", HeartbeatAt = DateTimeOffset.UtcNow.ToString("O"), ThreadId = request.ThreadId };
        WriteWorkerState(workspace, id, state, host, terminal: false);

        void Heartbeat()
        {
            state = state with { HeartbeatAt = DateTimeOffset.UtcNow.ToString("O") };
            WriteWorkerState(workspace, id, state, host, terminal: false);
        }

        bool Cancelled() => File.Exists(CancelPath(workspace, id, host));

        try
        {
            using var client = ClientFactory();
            var result = request.Role switch
            {
                "reviewer" => AppServerAgents.RunFreshReviewer(client, request.Workspace, request.Model, request.Effort, request.Prompt, Cancelled, Heartbeat),
                "builder-hold" => AppServerAgents.CreateBuilderHold(client, request.Workspace, request.Model, request.Effort, request.Prompt, Cancelled, Heartbeat),
                "builder-resume" => AppServerAgents.ResumeBuilder(client, request.ThreadId!, request.Workspace, request.Model, request.Effort, request.Prompt, Cancelled, Heartbeat),
                _ => throw new CliFailure("state", "App Server session role is invalid", 3),
            };
            state = state with { Status = "completed", HeartbeatAt = DateTimeOffset.UtcNow.ToString("O"), ThreadId = result.ThreadId, TurnId = result.TurnId };
            BeforeTerminalPublish?.Invoke();
            PublishTerminal(workspace, id, state, new AppServerSessionResult(id, "completed", result), host);
        }
        catch (OperationCanceledException)
        {
            state = state with { Status = "cancelled", HeartbeatAt = DateTimeOffset.UtcNow.ToString("O"), ErrorCode = "cancelled", Error = "session was cancelled" };
            PublishTerminal(workspace, id, state, new AppServerSessionResult(id, state.Status, ErrorCode: state.ErrorCode, Error: state.Error), host);
        }
        catch (CliFailure error)
        {
            state = state with { Status = "failed", HeartbeatAt = DateTimeOffset.UtcNow.ToString("O"), ErrorCode = error.Code, Error = error.Message };
            PublishTerminal(workspace, id, state, new AppServerSessionResult(id, state.Status, ErrorCode: state.ErrorCode, Error: state.Error), host);
        }
        catch (Exception error)
        {
            state = state with { Status = "failed", HeartbeatAt = DateTimeOffset.UtcNow.ToString("O"), ErrorCode = "unknown", Error = error.Message };
            PublishTerminal(workspace, id, state, new AppServerSessionResult(id, state.Status, ErrorCode: state.ErrorCode, Error: state.Error), host);
        }
    }

    private static Process StartWorker(string workspace, string id, HostKind host)
    {
        var executable = Environment.ProcessPath ?? throw new CliFailure("process", "could not determine the planforge executable", 3);
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(AppServerSessions).Assembly.GetName().Name + ".dll"));
        }
        foreach (var argument in new[] { "session", "worker", "--workspace", workspace, "--host", host.ToString().ToLowerInvariant(), "--session-id", id })
            startInfo.ArgumentList.Add(argument);
        try { return WorkerLauncher(startInfo); }
        catch (Exception error) { throw new CliFailure("process", $"could not start App Server session worker: {error.Message}", 3); }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId); 
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    private static void WaitForParentStart(string workspace, string id, HostKind host)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var state = Read(StatePath(workspace, id, host), ForgeJsonContext.Default.AppServerSessionState);
            if (state.ProcessId is not null) return;
            Thread.Sleep(10);
        }
        throw new CliFailure("process", "App Server session parent did not finish worker registration", 3);
    }

    private static string ValidateId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new CliFailure("usage", "session-id must be a lowercase GUID without separators");
        return id;
    }

    private static string BasePath(string workspace, string id, HostKind host) =>
        Path.Combine(RepositoryPaths.AppServerSessionDirectory(RepositoryPaths.CanonicalWorkspaceRoot(workspace), host), id);
    private static string RequestPath(string workspace, string id, HostKind host) => BasePath(workspace, id, host) + ".request.json";
    private static string StatePath(string workspace, string id, HostKind host) => BasePath(workspace, id, host) + ".state.json";
    private static string ResultPath(string workspace, string id, HostKind host) => BasePath(workspace, id, host) + ".result.json";
    private static string CancelPath(string workspace, string id, HostKind host) => BasePath(workspace, id, host) + ".cancel";
    private static string LockPath(string workspace, string id, HostKind host) => BasePath(workspace, id, host) + ".lock";

    private static FileStream AcquireSessionLock(string workspace, string id, HostKind host)
    {
        var path = LockPath(workspace, id, host);
        OwnershipGuards.EnsureDirectory(Path.GetDirectoryName(path)!);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(10); }
            catch (IOException) { throw new CliFailure("state", "App Server session state lock is busy", 3); }
        }
    }

    private static void WriteWorkerState(string workspace, string id, AppServerSessionState state, HostKind host, bool terminal)
    {
        using (AcquireSessionLock(workspace, id, host))
        {
            var current = Read(StatePath(workspace, id, host), ForgeJsonContext.Default.AppServerSessionState);
            if (terminal && current.Status is "completed" or "failed" or "cancelled") return;
            if (!terminal && current.Status == "cancelling") state = state with { Status = "cancelling" };
            Write(StatePath(workspace, id, host), state, ForgeJsonContext.Default.AppServerSessionState);
        }
    }

    private static void PublishTerminal(string workspace,
                                        string id,
                                        AppServerSessionState state,
                                        AppServerSessionResult result,
                                        HostKind host = HostKind.Codex,
                                        bool lockHeld = false)
    {
        if (!lockHeld)
        {
            using var sessionLock = AcquireSessionLock(workspace, id, host);
            PublishTerminal(workspace, id, state, result, host, lockHeld: true);
            return;
        }
        var current = Read(StatePath(workspace, id, host), ForgeJsonContext.Default.AppServerSessionState);
        if (current.Status is "completed" or "failed" or "cancelled") return;
        Write(ResultPath(workspace, id, host), result, ForgeJsonContext.Default.AppServerSessionResult);
        Write(StatePath(workspace, id, host), state, ForgeJsonContext.Default.AppServerSessionState);
    }

    private static void Write<T>(string path, T value, JsonTypeInfo<T> typeInfo)
        => DurableFiles.WriteAtomic(path, JsonSerializer.Serialize(value, typeInfo));

    private static T Read<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        if (!File.Exists(path)) throw new CliFailure("state", "App Server session does not exist", 3);
        try { return JsonSerializer.Deserialize(File.ReadAllText(path), typeInfo) ?? throw new JsonException("null document"); }
        catch (JsonException error) { throw new CliFailure("state", $"App Server session is malformed: {error.Message}", 3); }
    }
}
