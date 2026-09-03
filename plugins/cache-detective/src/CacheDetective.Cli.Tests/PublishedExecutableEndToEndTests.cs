using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace CacheDetective.Tests;

public sealed class PublishedExecutableEndToEndTests(ITestOutputHelper output)
{
    private const string RequireVariable = "CACHEDETECTIVE_REQUIRE_E2E";
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IndexTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Published_server_handles_initialize_list_and_call_with_only_json_rpc_on_stdout()
    {
        if (SkipUnlessPublished(out var executable))
            return;

        await using var server = new PublishedServer(executable, output);
        server.Send(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = InitializeParameters()
        });
        var initialize = await server.ReadResponseAsync("initialize", HandshakeTimeout);
        server.Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
        server.Send(new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        var toolList = await server.ReadResponseAsync("tools/list", HandshakeTimeout);
        server.Send(ToolCall(3, "workspace_status", new { }));
        var toolCall = await server.ReadResponseAsync("tools/call", HandshakeTimeout);
        await server.CompleteAsync(HandshakeTimeout);

        using var initializeDocument = JsonDocument.Parse(initialize);
        Assert.Equal("2025-06-18", initializeDocument.RootElement.GetProperty("result")
            .GetProperty("protocolVersion").GetString());

        using var toolsDocument = JsonDocument.Parse(toolList);
        var tools = toolsDocument.RootElement.GetProperty("result").GetProperty("tools");
        Assert.Contains(tools.EnumerateArray(), tool =>
            tool.GetProperty("name").GetString() == "workspace_status");

        var status = ToolPayload(toolCall);
        Assert.Equal(0, status.GetProperty("solutions").GetProperty("total").GetInt32());
        Assert.Equal(0, status.GetProperty("counts").GetProperty("vertices").GetInt32());
    }

    [Fact]
    public async Task Published_server_indexes_real_solution_without_cache_keys_or_source_changes()
    {
        if (SkipUnlessPublished(out var executable))
            return;

        var solution = FindRepositoryFile("plugins", "plan-forge-flow", "src", "PlanForgeFlow.sln");
        var planForgeRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(solution)!, ".."));
        var before = SnapshotTree(planForgeRoot);
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"cache-detective-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            await using var server = new PublishedServer(executable, output);
            server.Send(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = InitializeParameters()
            });
            server.Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
            _ = await server.ReadResponseAsync("initialize", HandshakeTimeout);

            server.Send(ToolCall(2, "workspace_init", new
            {
                root = temporaryRoot,
                solutions = new[] { solution }
            }));
            var initialized = ToolPayload(await server.ReadResponseAsync("workspace_init", HandshakeTimeout));
            Assert.True(initialized.GetProperty("written").GetBoolean());

            server.Send(ToolCall(3, "index_solution", new { path = solution }));
            var indexed = ToolPayload(await server.ReadResponseAsync("index_solution", IndexTimeout));
            Assert.True(indexed.GetProperty("succeeded").GetBoolean());
            var diagnostics = indexed.GetProperty("diagnostics");
            Assert.Equal(JsonValueKind.Array, diagnostics.GetProperty("items").ValueKind);
            Assert.True(diagnostics.GetProperty("total").GetInt32() >= 0);
            Assert.All(diagnostics.GetProperty("items").EnumerateArray(), diagnostic =>
            {
                Assert.False(string.IsNullOrWhiteSpace(diagnostic.GetProperty("kind").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(diagnostic.GetProperty("message").GetString()));
            });

            server.Send(ToolCall(4, "export_graph", new { }));
            var graph = ToolPayload(await server.ReadResponseAsync("export_graph", HandshakeTimeout));
            Assert.DoesNotContain(graph.GetProperty("nodes").EnumerateArray(), node =>
                node.GetProperty("type").GetString() == "CacheKey");

            await server.CompleteAsync(HandshakeTimeout);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }

        var after = SnapshotTree(planForgeRoot);
        Assert.Equal(before.Keys, after.Keys);
        Assert.All(before, file => Assert.Equal(file.Value, after[file.Key]));
    }

    private bool SkipUnlessPublished(out string executable)
    {
        var found = FindPublishedExecutable();
        if (found is not null)
        {
            executable = found;
            return false;
        }

        executable = string.Empty;
        var message = "The published bin/win-x64/cachedet.exe was not found. Run build/package.ps1 first.";
        if (Environment.GetEnvironmentVariable(RequireVariable) == "1")
            Assert.Fail(message);
        output.WriteLine($"Skipped: {message}");
        return true;
    }

    private static string? FindPublishedExecutable()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "bin", "win-x64", "cachedet.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static object ToolCall(int id, string name, object arguments) => new
    {
        jsonrpc = "2.0",
        id,
        method = "tools/call",
        @params = new { name, arguments }
    };

    private static object InitializeParameters() => new
    {
        protocolVersion = "2025-06-18",
        capabilities = new { },
        clientInfo = new { name = "cache-detective-e2e", version = "1.0" }
    };

    private static JsonElement ToolPayload(string response)
    {
        using var responseDocument = JsonDocument.Parse(response);
        var result = responseDocument.RootElement.GetProperty("result");
        if (result.TryGetProperty("isError", out var isError))
            Assert.False(isError.GetBoolean(), response);
        var text = result.GetProperty("content").EnumerateArray()
            .Single(content => content.GetProperty("type").GetString() == "text")
            .GetProperty("text").GetString();
        Assert.False(string.IsNullOrWhiteSpace(text));
        using var payload = JsonDocument.Parse(text);
        return payload.RootElement.Clone();
    }

    private static IReadOnlyDictionary<string, string> SnapshotTree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(root, path)))
            .Where(file => !file.Relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                                segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                                segment.Equals(".vs", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(file => file.Relative, StringComparer.Ordinal)
            .ToDictionary(file => file.Relative,
                file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.Path))),
                StringComparer.Ordinal);

    private static string FindRepositoryFile(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(relativePath)}.");
    }

    private sealed class PublishedServer : IAsyncDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly Process _process;
        private readonly Task<string> _standardError;
        private bool _completed;

        public PublishedServer(string executable, ITestOutputHelper output)
        {
            _output = output;
            var startInfo = new ProcessStartInfo(executable)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            };
            startInfo.ArgumentList.Add("mcp");
            _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start cachedet.");
            _process.StandardInput.NewLine = "\n";
            _standardError = _process.StandardError.ReadToEndAsync();
        }

        public void Send(object message)
        {
            _process.StandardInput.WriteLine(JsonSerializer.Serialize(message));
            _process.StandardInput.Flush();
        }

        public async Task<string> ReadResponseAsync(string operation, TimeSpan timeout)
        {
            var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(timeout);
            Assert.False(string.IsNullOrWhiteSpace(line),
                $"The server closed stdout before answering {operation}.");
            using var document = JsonDocument.Parse(line);
            Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
            return line;
        }

        public async Task CompleteAsync(TimeSpan timeout)
        {
            _process.StandardInput.Close();
            var trailing = _process.StandardOutput.ReadToEndAsync();
            await _process.WaitForExitAsync().WaitAsync(timeout);
            Assert.True(string.IsNullOrWhiteSpace(await trailing),
                $"Unexpected trailing stdout: {await trailing}");
            _output.WriteLine($"stderr: {await _standardError}");
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed && !_process.HasExited)
                _process.Kill(entireProcessTree: true);
            if (!_completed)
                await _process.WaitForExitAsync();
            if (!_completed)
                _ = await _standardError;
            _process.Dispose();
        }
    }
}
