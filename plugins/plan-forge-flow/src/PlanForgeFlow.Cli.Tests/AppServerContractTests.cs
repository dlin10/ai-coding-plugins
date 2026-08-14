using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.OpenAI;
using PlanForgeFlow.Pending;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.State;
using Xunit;

namespace PlanForgeFlow.Tests;

[Collection("Process environment")]
public sealed class AppServerContractTests
{
    private static readonly object SessionEnvironmentLock = new();
    [Fact]
    public void CatalogUsesEveryPageAndOnlyAdvertisedEfforts()
    {
        using var client = Client(
            Response(1, "{}"),
            Response(2, """{"data":[{"id":"gpt-a","supportedReasoningEfforts":[{"reasoningEffort":"low"}]}],"nextCursor":"page-2"}"""),
            Response(3, """{"data":[{"id":"gpt-b","supportedReasoningEfforts":[{"reasoningEffort":"high"}]}],"nextCursor":null}"""));

        client.Initialize();
        var models = client.ListModels();

        Assert.Equal(["gpt-a", "gpt-b"], models.Select(model => model.Id));
        Assert.Equal("gpt-b", AppServerContracts.SelectModel(models, "gpt-b", "high").Id);
        Assert.Throws<CliFailure>(() => AppServerContracts.SelectModel(models, "gpt-b", "low"));
    }

    [Fact]
    public void InitializeAdvertisesTheAssemblyVersion()
    {
        var output = new StringWriter();
        using var client = Client(output, Response(1, "{}"));

        client.Initialize();

        var initialize = Requests(output).Single(request => request.GetProperty("method").GetString() == "initialize");
        Assert.Equal(AppServerClient.ClientVersion,
                     initialize.GetProperty("params").GetProperty("clientInfo").GetProperty("version").GetString());
    }

    [Fact]
    public void ClaudeSessionsUseClaudePluginDataWithoutTouchingCodexHome()
    {
        var workspace = TestPaths.Unique("planforge-app-server-root");
        Directory.CreateDirectory(workspace);
        Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "init"]).ExitCode);
        var claudeData = TestPaths.Unique("planforge-claude-data");
        var codexHome = TestPaths.Unique("planforge-codex-home");
        var originalForgeData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
        var originalClaudeData = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA");
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", null);
            Environment.SetEnvironmentVariable("CLAUDE_PLUGIN_DATA", claudeData);
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);

            var sessionDirectory = RepositoryPaths.AppServerSessionDirectory(workspace, HostKind.Claude);
            var pendingPath = PendingRuns.PathFor(RepositoryPaths.Identify(workspace), "same-root", HostKind.Claude);

            Assert.StartsWith(Path.GetFullPath(claudeData), sessionDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(Path.GetFullPath(claudeData), pendingPath, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(codexHome));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", originalForgeData);
            Environment.SetEnvironmentVariable("CLAUDE_PLUGIN_DATA", originalClaudeData);
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
            if (Directory.Exists(claudeData)) Directory.Delete(claudeData, recursive: true);
            if (Directory.Exists(codexHome)) Directory.Delete(codexHome, recursive: true);
        }
    }

    [Theory]
    [InlineData("apiKey")]
    [InlineData("chatgpt")]
    [InlineData("chatgptAuthTokens")]
    public void AuthAcceptsOnlyOpenAiAndChatGptModes(string type)
    {
        using var client = Client(Response(1, "{}"), Response(2, $$"""{"account":{"type":"{{type}}"},"requiresOpenaiAuth":true}"""));
        client.Initialize();
        client.ValidateOpenAiAccount();
    }

    [Fact]
    public void AuthRejectsBedrock()
    {
        using var client = Client(Response(1, "{}"), Response(2, """{"account":{"type":"amazonBedrock"},"requiresOpenaiAuth":false}"""));
        client.Initialize();
        Assert.Throws<CliFailure>(client.ValidateOpenAiAccount);
    }

    [Fact]
    public void FreshReviewerIsReadOnlyAndDeletedAfterIdentityAudit()
    {
        var output = new StringWriter();
        using var client = Client(output,
            Response(1, "{}"),
            Response(2, """{"account":{"type":"chatgpt"},"requiresOpenaiAuth":true}"""),
            Response(3, Catalog("gpt-review", "high")),
            Response(4, Thread("thr-review", "gpt-review")),
            Response(5, """{"turn":{"id":"turn-review","status":"inProgress","items":[],"error":null}}"""),
            """{"method":"turn/completed","params":{"turn":{"id":"turn-review","status":"completed","items":[],"error":null}}}""",
            Response(6, """{"thread":{"id":"thr-review","sessionId":"thr-review","modelProvider":"openai","status":{"type":"idle"}}}"""),
            Response(7, "{}"));

        var result = AppServerAgents.RunFreshReviewer(client, "C:/repo", "gpt-review", "high", "audit");

        Assert.Equal("completed", result.Status);
        var requests = Requests(output);
        Assert.Contains(requests, request => request.GetProperty("method").GetString() == "thread/start" && request.GetProperty("params").GetProperty("sandbox").GetString() == "readOnly");
        Assert.Equal("thread/delete", requests[^1].GetProperty("method").GetString());
    }

    [Fact]
    public void BuilderResumeUsesRepoScopedWritableNetworkSandbox()
    {
        var output = new StringWriter();
        using var client = Client(output,
            Response(1, "{}"),
            Response(2, """{"account":{"type":"apiKey"},"requiresOpenaiAuth":true}"""),
            Response(3, Catalog("gpt-build", "max")),
            Response(4, """{"thread":{"id":"thr-build","sessionId":"thr-build","modelProvider":"openai","status":{"type":"notLoaded"}}}"""),
            Response(5, Thread("thr-build", "gpt-build")),
            Response(6, """{"turn":{"id":"turn-build","status":"inProgress","items":[],"error":null}}"""),
            """{"method":"turn/completed","params":{"turn":{"id":"turn-build","status":"completed","items":[],"error":null}}}""");

        AppServerAgents.ResumeBuilder(client, "thr-build", "C:/repo", "gpt-build", "max", "implement");

        var turn = Requests(output).Single(request => request.GetProperty("method").GetString() == "turn/start");
        var parameters = turn.GetProperty("params");
        Assert.Equal("never", parameters.GetProperty("approvalPolicy").GetString());
        Assert.Equal("workspaceWrite", parameters.GetProperty("sandboxPolicy").GetProperty("type").GetString());
        Assert.True(parameters.GetProperty("sandboxPolicy").GetProperty("networkAccess").GetBoolean());
        Assert.Equal("C:/repo", parameters.GetProperty("sandboxPolicy").GetProperty("writableRoots")[0].GetString());
    }

    [Fact]
    public void ProviderModelIdentityAndStatusDriftFailClosed()
    {
        using var wrongProvider = Document("""{"thread":{"id":"thr","modelProvider":"bedrock","model":"gpt-a"}}""");
        using var wrongModel = Document("""{"thread":{"id":"thr","modelProvider":"openai","model":"gpt-b"}}""");
        using var wrongIdentity = Document("""{"thread":{"id":"other","modelProvider":"openai","status":{"type":"idle"}}}""");
        using var missingStatus = Document("""{"thread":{"id":"thr","modelProvider":"openai"}}""");
        using var missingModel = Document("""{"thread":{"id":"thr","modelProvider":"openai"}}""");

        Assert.Throws<CliFailure>(() => AppServerContracts.ParseThread(wrongProvider.RootElement, "thr", "gpt-a", false));
        Assert.Throws<CliFailure>(() => AppServerContracts.ParseThread(wrongModel.RootElement, "thr", "gpt-a", false));
        Assert.Throws<CliFailure>(() => AppServerContracts.ParseThread(wrongIdentity.RootElement, "thr", null, true));
        Assert.Throws<CliFailure>(() => AppServerContracts.ParseThread(missingStatus.RootElement, "thr", null, true));
        Assert.Throws<CliFailure>(() => AppServerContracts.ParseThread(missingModel.RootElement, "thr", "gpt-a", false, true));
    }

    [Fact]
    public void CancellationSendsTurnInterrupt()
    {
        var output = new StringWriter();
        using var client = Client(output,
            Response(1, "{}"),
            Response(2, """{"account":{"type":"apiKey"},"requiresOpenaiAuth":true}"""),
            Response(3, Catalog("gpt-build", "high")),
            Response(4, """{"thread":{"id":"thr-build","sessionId":"thr-build","modelProvider":"openai","status":{"type":"idle"}}}"""),
            Response(5, Thread("thr-build", "gpt-build")),
            Response(6, """{"turn":{"id":"turn-build","status":"inProgress","items":[],"error":null}}"""),
            Response(7, "{}"),
            """{"method":"turn/completed","params":{"turn":{"id":"turn-build","status":"interrupted","items":[],"error":null}}}""");

        Assert.Throws<OperationCanceledException>(() => AppServerAgents.ResumeBuilder(client, "thr-build", "C:/repo", "gpt-build", "high", "work", () => true));
        Assert.Equal("turn/interrupt", Requests(output)[^1].GetProperty("method").GetString());
    }

    [Fact]
    public void ProtocolErrorsAndClosedProcessAreDistinctFailures()
    {
        using var mismatch = Client(Response(9, "{}"));
        Assert.Equal("protocol", Assert.Throws<CliFailure>(mismatch.Initialize).Code);
        using var closed = Client();
        Assert.Equal("process", Assert.Throws<CliFailure>(closed.Initialize).Code);

        using var serverRequest = Client(Response(1, "{}"), """{"id":2,"method":"account/chatgptAuthTokens/refresh","params":{"reason":"unauthorized"}}""");
        serverRequest.Initialize();
        Assert.Contains("unsupported App Server request", Assert.Throws<CliFailure>(serverRequest.ValidateOpenAiAccount).Message);
    }

    [Fact]
    public void DetachedStartPublishesInitialStateBeforeLaunchAndCancelPersists()
    {
        lock (SessionEnvironmentLock)
        {
            var workspace = TestPaths.Unique("planforge-app-server-tests");
            var data = Path.Combine(workspace, "data");
            Directory.CreateDirectory(workspace);
            var originalData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
            var originalLauncher = AppServerSessions.WorkerLauncher;
            try
            {
                Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
                AppServerSessions.WorkerLauncher = startInfo =>
                {
                    var id = startInfo.ArgumentList[^1];
                    var statePath = Path.Combine(RepositoryPaths.AppServerSessionDirectory(workspace), id + ".state.json");
                    using var initial = JsonDocument.Parse(File.ReadAllText(statePath));
                    Assert.Equal("starting", initial.RootElement.GetProperty("status").GetString());
                    Assert.Equal(JsonValueKind.Null, initial.RootElement.GetProperty("processId").ValueKind);
                    return Process.GetCurrentProcess();
                };

                var started = AppServerSessions.Start(workspace, "builder-hold", "gpt-a", "high", null, "hold");
                Assert.Equal("starting", started.Status);
                Assert.Equal(Environment.ProcessId, started.ProcessId);

                var cancelled = AppServerSessions.Cancel(workspace, started.Id);
                Assert.Equal("cancelling", cancelled.Status);
                Assert.Equal("cancelling", AppServerSessions.Status(workspace, started.Id).Status);
            }
            finally
            {
                AppServerSessions.WorkerLauncher = originalLauncher;
                Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", originalData);
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [Fact]
    public void DetachedCancellationConsumesInterruptAndPersistsTerminalResult()
    {
        lock (SessionEnvironmentLock)
        {
            var workspace = TestPaths.Unique("planforge-app-server-tests");
            var data = Path.Combine(workspace, "data");
            Directory.CreateDirectory(workspace);
            var originalData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
            var originalLauncher = AppServerSessions.WorkerLauncher;
            var originalFactory = AppServerSessions.ClientFactory;
            try
            {
                Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
                AppServerSessions.WorkerLauncher = _ => Process.GetCurrentProcess();
                AppServerSessions.ClientFactory = () => Client(
                    Response(1, "{}"),
                    Response(2, """{"account":{"type":"apiKey"},"requiresOpenaiAuth":true}"""),
                    Response(3, Catalog("gpt-a", "high")),
                    Response(4, Thread("thr-hold", "gpt-a")),
                    Response(5, """{"turn":{"id":"turn-hold","status":"inProgress","items":[],"error":null}}"""),
                    Response(6, "{}"),
                    """{"method":"turn/completed","params":{"turn":{"id":"turn-hold","status":"interrupted","items":[],"error":null}}}""");

                var started = AppServerSessions.Start(workspace, "builder-hold", "gpt-a", "high", null, "hold");
                AppServerSessions.Cancel(workspace, started.Id);
                AppServerSessions.RunWorker(workspace, started.Id);

                var status = AppServerSessions.Status(workspace, started.Id);
                var result = AppServerSessions.Result(workspace, started.Id);
                Assert.Equal("cancelled", status.Status);
                Assert.Equal("cancelled", status.ErrorCode);
                Assert.Equal("cancelled", result.Status);
                Assert.Equal("cancelled", result.ErrorCode);
            }
            finally
            {
                AppServerSessions.ClientFactory = originalFactory;
                AppServerSessions.WorkerLauncher = originalLauncher;
                Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", originalData);
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [Fact]
    public void SilentServerCancellationInterruptsPromptlyAndBoundsTerminalWait()
    {
        var reader = new CancellableScriptedReader(Response(1, "{}"));
        var output = new InterruptAwareWriter(reader);
        using var client = new AppServerClient(reader, output) { CancellationGracePeriod = TimeSpan.FromMilliseconds(100) };
        client.Initialize();
        var polls = 0;
        var stopwatch = Stopwatch.StartNew();

        Assert.Throws<OperationCanceledException>(() => client.WaitForTurn("thread", "turn", () => Volatile.Read(ref polls) > 0,
                                                                           () => Interlocked.Increment(ref polls)));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"cancellation took {stopwatch.Elapsed}");
        Assert.Contains(Requests(output), request => request.GetProperty("method").GetString() == "turn/interrupt");
        Assert.False(reader.ConcurrentReadDetected);
    }

    [Fact]
    public void SilentReviewerInterruptAndDeletePreserveCancellationAndFinishPromptly()
    {
        var reader = new CancellableScriptedReader(
            Response(1, "{}"),
            Response(2, """{"account":{"type":"apiKey"},"requiresOpenaiAuth":true}"""),
            Response(3, Catalog("gpt-review", "high")),
            Response(4, Thread("thr-review", "gpt-review")),
            Response(5, """{"turn":{"id":"turn-review","status":"inProgress","items":[],"error":null}}"""));
        var output = new StringWriter();
        using var client = new AppServerClient(reader, output)
        {
            CancellationGracePeriod = TimeSpan.FromMilliseconds(100),
            CleanupGracePeriod = TimeSpan.FromMilliseconds(100),
        };
        var polls = 0;
        var stopwatch = Stopwatch.StartNew();

        Assert.Throws<OperationCanceledException>(() =>
            AppServerAgents.RunFreshReviewer(client, "C:/repo", "gpt-review", "high", "audit",
                                              () => Volatile.Read(ref polls) > 0,
                                              () => Interlocked.Increment(ref polls)));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"reviewer cancellation and cleanup took {stopwatch.Elapsed}");
        Assert.Equal(["turn/interrupt", "thread/delete"],
                     Requests(output).Select(request => request.GetProperty("method").GetString()).TakeLast(2));
        Assert.False(reader.ConcurrentReadDetected);
    }

    [Fact]
    public void DeadCancellingWorkerBecomesAtomicCancelledResult()
    {
        lock (SessionEnvironmentLock)
        {
            var workspace = TestPaths.Unique("planforge-app-server-tests");
            var data = Path.Combine(workspace, "data");
            Directory.CreateDirectory(workspace);
            var originalData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
            var originalLauncher = AppServerSessions.WorkerLauncher;
            try
            {
                Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
                AppServerSessions.WorkerLauncher = _ => Process.GetCurrentProcess();
                var started = AppServerSessions.Start(workspace, "builder-hold", "gpt-a", "high", null, "hold");
                var cancelling = AppServerSessions.Cancel(workspace, started.Id) with { ProcessId = int.MaxValue };
                var statePath = Path.Combine(RepositoryPaths.AppServerSessionDirectory(workspace), started.Id + ".state.json");
                DurableFiles.WriteJson(statePath, cancelling, ForgeJsonContext.Default.AppServerSessionState);

                var status = AppServerSessions.Status(workspace, started.Id);
                var result = AppServerSessions.Result(workspace, started.Id);

                Assert.Equal("cancelled", status.Status);
                Assert.Equal("cancelled", status.ErrorCode);
                Assert.Equal("cancelled", result.Status);
                Assert.Equal("cancelled", result.ErrorCode);
            }
            finally
            {
                AppServerSessions.WorkerLauncher = originalLauncher;
                Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", originalData);
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [Fact]
    public void CompletedWorkerWinsConcurrentCancelWithoutStateResultDrift()
    {
        lock (SessionEnvironmentLock)
        {
            var workspace = TestPaths.Unique("planforge-app-server-tests");
            var data = Path.Combine(workspace, "data");
            Directory.CreateDirectory(workspace);
            var originalData = Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA");
            var originalLauncher = AppServerSessions.WorkerLauncher;
            var originalFactory = AppServerSessions.ClientFactory;
            var originalProbe = AppServerSessions.BeforeTerminalPublish;
            using var ready = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            try
            {
                Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", data);
                AppServerSessions.WorkerLauncher = _ => Process.GetCurrentProcess();
                AppServerSessions.ClientFactory = () => Client(
                    Response(1, "{}"),
                    Response(2, """{"account":{"type":"apiKey"},"requiresOpenaiAuth":true}"""),
                    Response(3, Catalog("gpt-a", "high")),
                    Response(4, Thread("thr-hold", "gpt-a")),
                    Response(5, """{"turn":{"id":"turn-hold","status":"inProgress","items":[],"error":null}}"""),
                    """{"method":"turn/completed","params":{"turn":{"id":"turn-hold","status":"completed","items":[],"error":null}}}""");
                AppServerSessions.BeforeTerminalPublish = () => { ready.Set(); release.Wait(); };
                var started = AppServerSessions.Start(workspace, "builder-hold", "gpt-a", "high", null, "hold");
                var worker = Task.Run(() => AppServerSessions.RunWorker(workspace, started.Id));
                Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));

                Assert.Equal("cancelling", AppServerSessions.Cancel(workspace, started.Id).Status);
                release.Set();
                Assert.True(SpinWait.SpinUntil(() => worker.IsCompleted, TimeSpan.FromSeconds(5)));
                Assert.False(worker.IsFaulted, worker.Exception?.ToString());

                var status = AppServerSessions.Status(workspace, started.Id);
                var result = AppServerSessions.Result(workspace, started.Id);
                Assert.Equal("completed", status.Status);
                Assert.Equal("completed", result.Status);
                Assert.NotNull(result.Agent);
            }
            finally
            {
                release.Set();
                AppServerSessions.BeforeTerminalPublish = originalProbe;
                AppServerSessions.ClientFactory = originalFactory;
                AppServerSessions.WorkerLauncher = originalLauncher;
                Environment.SetEnvironmentVariable("FORGE_PLUGIN_DATA", originalData);
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    private static AppServerClient Client(params string[] lines) => Client(new StringWriter(), lines);

    private static AppServerClient Client(StringWriter output, params string[] lines)
        => new(new StringReader(string.Join(Environment.NewLine, lines)), output);

    private static string Response(int id, string result) => $$"""{"id":{{id}},"result":{{result}}}""";
    private static string Catalog(string model, string effort)
        => $$"""{"data":[{"id":"{{model}}","supportedReasoningEfforts":[{"reasoningEffort":"{{effort}}"}]}],"nextCursor":null}""";
    private static string Thread(string id, string model)
        => $$"""{"thread":{"id":"{{id}}","sessionId":"{{id}}","modelProvider":"openai","model":"{{model}}"} }""";
    private static JsonDocument Document(string json) => JsonDocument.Parse(json);

    private static List<JsonElement> Requests(StringWriter output)
        => output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                 .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                 .Where(value => value.TryGetProperty("id", out _))
                 .ToList();

    private sealed class CancellableScriptedReader(params string[] lines) : TextReader
    {
        private readonly ConcurrentQueue<string> queue = new(lines);
        private readonly SemaphoreSlim available = new(lines.Length);
        private int activeReads;

        public bool ConcurrentReadDetected { get; private set; }

        public void Enqueue(string line)
        {
            queue.Enqueue(line);
            available.Release();
        }

        public override string? ReadLine()
        {
            available.Wait();
            return queue.TryDequeue(out var line) ? line : null;
        }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref activeReads) != 1) ConcurrentReadDetected = true;
            try
            {
                await available.WaitAsync(cancellationToken);
                return queue.TryDequeue(out var line) ? line : null;
            }
            finally { Interlocked.Decrement(ref activeReads); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) available.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class InterruptAwareWriter(CancellableScriptedReader reader) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value is null) return;
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (root.TryGetProperty("method", out var method) && method.GetString() == "turn/interrupt")
                reader.Enqueue(Response(root.GetProperty("id").GetInt32(), "{}"));
        }
    }
}
