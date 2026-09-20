using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace ConcurrencyHunter.Cli.Tests;

public sealed class PublishedExecutableEndToEndTests(ITestOutputHelper output)
{
    private const string REQUIRE_VARIABLE = "CONCURRENCYHUNTER_REQUIRE_E2E";
    private static readonly TimeSpan HANDSHAKE_TIMEOUT = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ANALYSIS_TIMEOUT = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PUBLISH_TIMEOUT = TimeSpan.FromMinutes(10);

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

            var requestId = 4;
            var groups = new List<JsonElement>();
            for (var page = 1; ; page++)
            {
                server.Send(ToolCall(requestId++, "get_groups", new { run_id = runId, page }));
                var payload = ToolPayload(await server.ReadResponseAsync($"get_groups page {page}", HANDSHAKE_TIMEOUT));
                groups.AddRange(payload.GetProperty("items").EnumerateArray());
                if (page >= payload.GetProperty("pages").GetInt32())
                    break;
            }

            Assert.NotEmpty(groups);
            Assert.Contains(groups, group => group.GetProperty("region").GetString()!.StartsWith("di:", StringComparison.Ordinal));
            Assert.Contains(groups, group => group.GetProperty("region").GetString()!.StartsWith("alloc:", StringComparison.Ordinal));
            Assert.Contains(groups, group => group.GetProperty("confidenceLabel").GetString() == "Medium");
            Assert.All(groups, group =>
            {
                Assert.True(group.GetProperty("occurrenceCount").GetInt32() >= group.GetProperty("findingCount").GetInt32());
                Assert.All(group.GetProperty("findings").EnumerateArray(),
                           finding => Assert.True(finding.GetProperty("occurrenceCount").GetInt32() >= 1));
            });
            var narrated = groups.Where(group => group.GetProperty("confidenceLabel").GetString() is "High" or "Medium").ToArray();
            Assert.NotEmpty(narrated);
            foreach (var group in narrated)
            {
                var groupId = group.GetProperty("groupId").GetString()!;
                server.Send(ToolCall(requestId++, "submit_narrative", new
                {
                    run_id = runId,
                    target = groupId,
                    text = GroupNarrative(group)
                }));
                var submission = ToolPayload(await server.ReadResponseAsync($"submit_narrative {groupId}", HANDSHAKE_TIMEOUT));
                Assert.True(submission.GetProperty("status").GetString() == "accepted",
                            $"Narrative for {groupId} was not accepted: {submission.GetRawText()}");
            }

            var firstEvidence = narrated[0].GetProperty("findings")[0].GetProperty("evidenceIds")[0].GetString();
            server.Send(ToolCall(requestId++, "submit_narrative", new
            {
                run_id = runId,
                target = "summary",
                text = $"The demo holds {groups.Count} groups of shared state that concurrent roots can corrupt [E:{firstEvidence}]."
            }));
            var summarySubmission = ToolPayload(
                await server.ReadResponseAsync("submit_narrative summary", HANDSHAKE_TIMEOUT));
            Assert.True(summarySubmission.GetProperty("status").GetString() == "accepted",
                        $"Summary was not accepted: {summarySubmission.GetRawText()}");

            server.Send(ToolCall(requestId, "render_report", new { run_id = runId }));
            var rendered = ToolPayload(await server.ReadResponseAsync("render_report", HANDSHAKE_TIMEOUT));
            Assert.Equal("CompleteWithFindings", rendered.GetProperty("status").GetString());
            var bundlePath = rendered.GetProperty("bundlePath").GetString()!;
            Assert.Equal(
                ["findings.json", "report.md", "run-metadata.json"],
                Directory.GetFiles(bundlePath).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            using var findings = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(bundlePath, "findings.json")));
            var findingElements = findings.RootElement.GetProperty("findings").EnumerateArray().ToArray();
            Assert.Equal(rendered.GetProperty("counts").GetProperty("findings").GetInt32(), findingElements.Length);
            Assert.Contains(findingElements, finding => finding.GetProperty("ruleId").GetString() == "DCA1002");
            Assert.Equal("2.1", findings.RootElement.GetProperty("schemaVersion").GetString());
            Assert.All(findingElements, finding =>
            {
                Assert.Matches("^[0-9a-f]{16}$", finding.GetProperty("fingerprint").GetString()!);
                Assert.True(finding.GetProperty("occurrenceCount").GetInt32() >= 1);
            });
            var sharedHelper = Assert.Single(findingElements, finding =>
                finding.GetProperty("resource").GetProperty("region").GetString()!.Contains("ActivityLog", StringComparison.Ordinal));
            Assert.Equal(6, sharedHelper.GetProperty("occurrenceCount").GetInt32());
            var sharedHelperGroup = Assert.Single(findings.RootElement.GetProperty("groups").EnumerateArray(),
                                                  group => group.GetProperty("groupId").GetString() == sharedHelper.GetProperty("groupId").GetString());
            Assert.Equal(1, sharedHelperGroup.GetProperty("findingIds").GetArrayLength());
            Assert.Equal(6, sharedHelperGroup.GetProperty("occurrenceCount").GetInt32());
            var sharedHelperDigest = Assert.Single(groups, group => group.GetProperty("groupId").GetString() == sharedHelper.GetProperty("groupId").GetString());
            Assert.Equal((1, 6), (sharedHelperDigest.GetProperty("findingCount").GetInt32(), sharedHelperDigest.GetProperty("occurrenceCount").GetInt32()));
            Assert.Equal(6, Assert.Single(sharedHelperDigest.GetProperty("findings").EnumerateArray()).GetProperty("occurrenceCount").GetInt32());

            var report = (await File.ReadAllTextAsync(Path.Combine(bundlePath, "report.md"))).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Contains("verify manually", report, StringComparison.Ordinal);
            var block = report[report.IndexOf($"##### {sharedHelper.GetProperty("findingId").GetString()}\n", StringComparison.Ordinal)..];
            block = block[..block.IndexOf("\n\n", StringComparison.Ordinal)];
            Assert.Contains("- Occurrences: 6 (the first 3 listed)\n", block, StringComparison.Ordinal);
            Assert.Equal(3, block.Split('\n').Count(line => line.StartsWith("  - ", StringComparison.Ordinal)));
            await server.CompleteAsync(HANDSHAKE_TIMEOUT);
        }
        finally
        {
            if (Directory.Exists(localApplicationData))
                Directory.Delete(localApplicationData, true);
        }
    }

    /// <summary>
    /// The one check that a silently broken package cannot pass. The solver degrades to Unknown by design (ADR 0004), so an
    /// executable published without its native library analyses the demo exactly as a working one does, only with less
    /// precision — and no other test would notice. This one publishes the executable, runs it over the demo, and requires the
    /// coverage it prints to say the solver was there and that it decided at least one query.
    /// </summary>
    [Fact]
    public async Task Published_executable_carries_a_solver_that_answers_over_the_demo()
    {
        await DemoWorkspace.EnsureRestoredAsync();
        var project = RepositoryFiles.FindRepositoryFile(
            "plugins", "concurrency-hunter", "src", "ConcurrencyHunter.Cli", "ConcurrencyHunter.Cli.csproj");
        var demoSolution = RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "demo", "Demo.slnx");
        var directory = Path.Combine(Path.GetTempPath(), $"concurrency-hunter-solver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            await RunAsync("dotnet", ["publish", project, "-c", "Release", "-o", directory, "--disable-build-servers", "-nodeReuse:false"],
                           PUBLISH_TIMEOUT);
            var executable = Path.Combine(directory, "concurrency-hunter.exe");
            Assert.True(File.Exists(executable), $"The publish produced no executable in {directory}.");

            var metrics = Path.Combine(directory, "metrics.json");
            await RunAsync(executable, ["metrics", "--target", demoSolution, "--out", metrics], ANALYSIS_TIMEOUT);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(metrics));
            var counters = document.RootElement.GetProperty("coverage").EnumerateArray()
                                   .Select(scope => scope.GetProperty("counters"))
                                   .ToArray();
            Assert.All(counters, scope => Assert.Equal(1, Counter(scope, SolverCounters.AVAILABLE)));
            Assert.All(counters, scope => Assert.Equal(0, Counter(scope, SolverCounters.UNAVAILABLE)));
            Assert.True(counters.Sum(scope => Counter(scope, SolverCounters.SAT) + Counter(scope, SolverCounters.UNSAT)) > 0,
                        "The published executable answered no query either way: the solver it ships decided nothing.");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private static int Counter(JsonElement counters, string name) =>
        counters.TryGetProperty(name, out var value) ? value.GetInt32() : 0;

    /// <summary>Runs a command to completion, failing with everything it printed when it does not succeed.</summary>
    private async Task RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        await process.WaitForExitAsync(cancellation.Token);
        var text = $"{await standardOutput}{await standardError}";
        output.WriteLine($"{fileName} {string.Join(' ', arguments)} exited with {process.ExitCode}");
        Assert.True(process.ExitCode == 0, $"{fileName} failed with {process.ExitCode}: {text}");
    }

    /// <summary>A narrative built from a group digest alone: it cites an evidence id of every finding the digest lists
    /// and names no symbol or location, so the validator's grounding rules have nothing to reject.</summary>
    private static string GroupNarrative(JsonElement group)
    {
        var citations = group.GetProperty("findings").EnumerateArray()
                             .Select(finding => $"[E:{finding.GetProperty("evidenceIds")[0].GetString()}]")
                             .ToArray();
        return string.Join('\n',
            "### What can be lost",
            $"Concurrent roots can overwrite or observe a half-finished update of this shared state {string.Join(' ', citations)}.",
            "### Remediation",
            $"- Make every access to this state go through one synchronization primitive; verify manually {citations[0]}.",
            $"  - Check: every read and write in the listed accesses holds that one primitive {citations[^1]}.");
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
