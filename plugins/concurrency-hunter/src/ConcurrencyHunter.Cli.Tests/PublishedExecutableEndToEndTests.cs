using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace ConcurrencyHunter.Cli.Tests;

public sealed class PublishedExecutableEndToEndTests(ITestOutputHelper output)
{
    private const string REQUIRE_VARIABLE = "CONCURRENCYHUNTER_REQUIRE_E2E";
    private static readonly TimeSpan HANDSHAKE_TIMEOUT = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ANALYSIS_TIMEOUT = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Published_server_lists_exactly_the_five_tools_with_only_json_rpc_on_stdout()
    {
        if (SkipUnlessPublished(out var executable))
            return;

        await using var server = new PublishedServer(executable, ["mcp"], output);
        await InitializeAsync(server);
        server.Send(new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        var response = await server.ReadResponseAsync("tools/list", HANDSHAKE_TIMEOUT);
        await server.CompleteAsync(HANDSHAKE_TIMEOUT);

        using var document = JsonDocument.Parse(response);
        var names = document.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ["get_groups", "render_report", "run_poll", "run_start", "submit_narrative"],
            names);
    }

    [Fact]
    public async Task Published_launcher_serves_mcp_when_a_host_manifest_launches_it_with_no_arguments()
    {
        if (SkipUnlessPublished(out var executable))
            return;

        var launcher = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(executable)!, "..", "concurrency-hunter-launcher.cmd"));
        Assert.True(File.Exists(launcher), $"The launcher the manifests name is missing: {launcher}");

        await using var server = new PublishedServer("cmd.exe", ["/d", "/c", launcher], output);
        await InitializeAsync(server);
        server.Send(new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        var response = await server.ReadResponseAsync("tools/list", HANDSHAKE_TIMEOUT);
        await server.CompleteAsync(HANDSHAKE_TIMEOUT);

        using var document = JsonDocument.Parse(response);
        Assert.Equal(5, document.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task Published_server_takes_the_demo_through_all_five_tools_to_a_complete_report()
    {
        if (SkipUnlessPublished(out var executable))
            return;

        await DemoWorkspace.EnsureRestoredAsync();
        var demoSolution = RepositoryFiles.FindRepositoryFile(
            "plugins", "concurrency-hunter", "demo", "Demo.slnx");
        var demoDirectory = Path.GetDirectoryName(demoSolution)!;
        var localApplicationData = Path.Combine(
            Path.GetTempPath(),
            $"concurrency-hunter-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(localApplicationData);

        try
        {
            await using var server = new PublishedServer(
                executable,
                ["mcp"],
                output,
                new Dictionary<string, string?> { ["LOCALAPPDATA"] = localApplicationData });
            await InitializeAsync(server);

            server.Send(ToolCall(2, "run_start", new { target = demoDirectory }));
            var start = ToolPayload(await server.ReadResponseAsync("run_start", HANDSHAKE_TIMEOUT));
            var runId = start.GetProperty("run_id").GetString()!;

            JsonElement poll;
            var expiresAt = DateTimeOffset.UtcNow + ANALYSIS_TIMEOUT;
            do
            {
                server.Send(ToolCall(3, "run_poll", new { run_id = runId }));
                poll = ToolPayload(await server.ReadResponseAsync("run_poll", HANDSHAKE_TIMEOUT));
                if (poll.GetProperty("state").GetString() == "awaiting_narrative")
                    break;
                Assert.NotEqual("failed", poll.GetProperty("state").GetString());
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            while (DateTimeOffset.UtcNow < expiresAt);
            Assert.Equal("awaiting_narrative", poll.GetProperty("state").GetString());

            server.Send(ToolCall(4, "get_groups", new { run_id = runId, page = 1 }));
            var groups = ToolPayload(await server.ReadResponseAsync("get_groups", HANDSHAKE_TIMEOUT));
            var group = Assert.Single(groups.GetProperty("items").EnumerateArray());
            Assert.Equal("DCA1001", group.GetProperty("ruleId").GetString());
            Assert.Equal("High", group.GetProperty("confidenceLabel").GetString());
            Assert.Equal(
                "static:Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController",
                group.GetProperty("region").GetString());
            var groupId = group.GetProperty("groupId").GetString()!;

            var groupNarrative = string.Join('\n',
                "### What can be lost",
                "A visitor name written by one request can be overwritten or read mid-update by another [E:F1.A] [E:F2.B].",
                "### Remediation",
                "- Guard every access to `_lastVisitor` with one `lock` on a static gate object; verify manually.",
                "  - Check: every read and write of the field sits inside that lock [E:F1.P].");
            server.Send(ToolCall(5, "submit_narrative", new
            {
                run_id = runId,
                target = groupId,
                text = groupNarrative
            }));
            var groupSubmission = ToolPayload(
                await server.ReadResponseAsync("submit_narrative group", HANDSHAKE_TIMEOUT));
            Assert.Equal("accepted", groupSubmission.GetProperty("status").GetString());

            server.Send(ToolCall(6, "submit_narrative", new
            {
                run_id = runId,
                target = "summary",
                text = "One group of unprotected static state was found [E:F1.R]."
            }));
            var summarySubmission = ToolPayload(
                await server.ReadResponseAsync("submit_narrative summary", HANDSHAKE_TIMEOUT));
            Assert.Equal("accepted", summarySubmission.GetProperty("status").GetString());

            server.Send(ToolCall(7, "render_report", new { run_id = runId }));
            var rendered = ToolPayload(await server.ReadResponseAsync("render_report", HANDSHAKE_TIMEOUT));
            Assert.Equal("CompleteWithFindings", rendered.GetProperty("status").GetString());
            Assert.Equal(2, rendered.GetProperty("counts").GetProperty("findings").GetInt32());
            var bundlePath = rendered.GetProperty("bundlePath").GetString()!;
            Assert.Equal(
                ["findings.json", "report.md", "run-metadata.json"],
                Directory.GetFiles(bundlePath).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.Contains("Guard every access to", await File.ReadAllTextAsync(
                Path.Combine(bundlePath, "report.md")));
            using var findings = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(bundlePath, "findings.json")));
            Assert.Equal(2, findings.RootElement.GetProperty("findings").GetArrayLength());

            await server.CompleteAsync(HANDSHAKE_TIMEOUT);
        }
        finally
        {
            if (Directory.Exists(localApplicationData))
                Directory.Delete(localApplicationData, true);
        }
    }

    private static async Task InitializeAsync(PublishedServer server)
    {
        server.Send(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = InitializeParameters()
        });
        var response = await server.ReadResponseAsync("initialize", HANDSHAKE_TIMEOUT);
        using var document = JsonDocument.Parse(response);
        Assert.Equal("2025-06-18", document.RootElement.GetProperty("result")
            .GetProperty("protocolVersion").GetString());
        server.Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
    }

    private bool SkipUnlessPublished(out string executable)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "bin", "win-x64", "concurrency-hunter.exe");
            if (File.Exists(candidate))
            {
                executable = candidate;
                return false;
            }
        }

        executable = string.Empty;
        var message = "The published bin/win-x64/concurrency-hunter.exe was not found. Run build/package.ps1 first.";
        if (Environment.GetEnvironmentVariable(REQUIRE_VARIABLE) == "1")
            Assert.Fail(message);
        output.WriteLine($"Skipped: {message}");
        return true;
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
        clientInfo = new { name = "concurrency-hunter-e2e", version = "1.0" }
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

    private sealed class PublishedServer : IAsyncDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly Process _process;
        private readonly Task<string> _standardError;
        private bool _completed;

        internal PublishedServer(string fileName, IReadOnlyList<string> arguments, ITestOutputHelper output,
                                 IReadOnlyDictionary<string, string?>? environment = null)
        {
            _output = output;
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false)
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            if (environment is not null)
            {
                foreach (var variable in environment)
                    startInfo.Environment[variable.Key] = variable.Value;
            }

            _process = Process.Start(startInfo) ??
                throw new InvalidOperationException("Could not start concurrency-hunter.");
            _process.StandardInput.NewLine = "\n";
            _standardError = _process.StandardError.ReadToEndAsync();
        }

        internal void Send(object message)
        {
            _process.StandardInput.WriteLine(JsonSerializer.Serialize(message));
            _process.StandardInput.Flush();
        }

        internal async Task<string> ReadResponseAsync(string operation, TimeSpan timeout)
        {
            var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(timeout);
            Assert.False(string.IsNullOrWhiteSpace(line),
                $"The server closed stdout before answering {operation}.");
            using var document = JsonDocument.Parse(line);
            Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
            return line;
        }

        internal async Task CompleteAsync(TimeSpan timeout)
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
