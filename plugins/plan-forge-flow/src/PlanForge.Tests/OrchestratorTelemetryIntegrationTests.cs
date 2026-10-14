using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Repo;
using PlanForge.Vendors.Claude;
using PlanForge.Vendors.Codex;
using Xunit;
using Xunit.Abstractions;

namespace PlanForge.Tests;

/// <summary>
/// The Orchestrator's usage end to end (R5–R9): a real <c>claude -p</c> or <c>codex exec</c> drives the
/// Plan Forge server of the test build through <c>forge.begin</c>, <c>planning</c> and <c>done</c>, and
/// the Run's <c>telemetry.json</c> is read back. Runs the real CLIs, so it costs money and is traited for
/// filtering with --filter Category!=Integration.
/// </summary>
/// <remarks>
/// The server gets test-owned profile files through <c>PLANFORGE_CLAUDE_SETTINGS</c> and
/// <c>PLANFORGE_CODEX_CONFIG</c>, so its export choice never reads the user's profile; the transcript it
/// reads is the one the launched host writes for the session this test started. The host may send
/// <c>planning</c> and <c>done</c> from one response, and the server may then apply <c>done</c> first,
/// so at most one <c>planning</c> record is asserted. Every collector a test causes is stopped by the
/// process id it reports.
/// </remarks>
public sealed class OrchestratorTelemetryIntegrationTests : IDisposable
{
    private const string SERVER = "planforge";
    private const string CODEX_MODEL = "gpt-5.6-luna";

    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "planforge-orchestrator-it", Guid.NewGuid().ToString("n"));
    private readonly string _workspace;
    private readonly string _claudeSettings;
    private readonly string _codexConfig;
    private readonly int _port = TelemetryCollectorTests.FreePort();

    public OrchestratorTelemetryIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = Path.Combine(_root, "workspace");
        _claudeSettings = Path.Combine(_root, "profile", "settings.json");
        _codexConfig = Path.Combine(_root, "profile", "config.toml");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(_claudeSettings)!);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task A_Claude_session_without_the_export_records_its_interview_from_the_transcript()
    {
        await WriteProfilesAsync(export: false);

        var outcome = await RunClaudeAsync(export: false);

        AssertRecords(outcome, "transcript", costInInterview: false);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task A_Claude_session_with_the_export_records_its_interview_from_the_collector_with_cost()
    {
        await WriteProfilesAsync(export: true);

        var outcome = await WithCollectorStoppedAsync(() => RunClaudeAsync(export: true));

        AssertRecords(outcome, "telemetry", costInInterview: true);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task A_Codex_thread_without_the_export_records_its_interview_from_the_rollout()
    {
        await WriteProfilesAsync(export: false);

        var outcome = await RunCodexAsync(export: false);

        AssertRecords(outcome, "transcript", costInInterview: false);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task A_Codex_thread_with_the_export_records_its_interview_from_the_collector_without_cost()
    {
        await WriteProfilesAsync(export: true);

        var outcome = await WithCollectorStoppedAsync(() => RunCodexAsync(export: true));

        AssertRecords(outcome, "telemetry", costInInterview: false);
    }

    // ---- Hosts ----

    private async Task<Outcome> RunClaudeAsync(bool export)
    {
        await InitWorkspaceAsync();

        var settings = JsonNode.Parse(ClaudeCliSession.STANDARD_SETTINGS)!.AsObject();
        if (export) settings["env"] = ClaudeExportEnvironment();
        var mcp = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [SERVER] = new JsonObject
                {
                    ["command"] = TelemetryCollectorTests.DotnetHost,
                    ["args"] = new JsonArray(PlanForgeDll),
                    ["env"] = new JsonObject
                    {
                        ["PLANFORGE_CLAUDE_SETTINGS"] = _claudeSettings,
                        ["PLANFORGE_CODEX_CONFIG"] = _codexConfig,
                    },
                },
            },
        };

        var stdout = await RunHostAsync(ClaudeCliVendor.Executable,
                                        ["-p", "--output-format", "json", "--model", "haiku", "--effort", "low",
                                         "--strict-mcp-config", "--mcp-config", mcp.ToJsonString(),
                                         "--allowedTools", ClaudeCliSession.ToolRule(SERVER),
                                         "--settings", settings.ToJsonString()]);

        var result = stdout.Split('\n').Select(line => line.Trim()).Last(line => line.StartsWith('{'));
        var sessionId = JsonDocument.Parse(result).RootElement.GetProperty("session_id").GetString()!;
        return new Outcome(sessionId, Records());
    }

    private async Task<Outcome> RunCodexAsync(bool export)
    {
        await InitWorkspaceAsync();

        var environment = new List<string>
        {
            "PLANFORGE_CLAUDE_SETTINGS = " + TomlValue.String(_claudeSettings),
            "PLANFORGE_CODEX_CONFIG = " + TomlValue.String(_codexConfig),
        };

        // Codex hands a stdio server only part of its environment; the server finds the rollout in the
        // same home the host writes it to.
        if (Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home)
            environment.Add("CODEX_HOME = " + TomlValue.String(home));

        var codex = CodexLaunch.Command;
        List<string> arguments =
        [
            .. codex.PrefixArguments, "exec", "-", "--skip-git-repo-check", "--json", "-m", CODEX_MODEL,
            "-c", "model_reasoning_effort=" + TomlValue.String("low"),
            "-c", "sandbox_mode=" + TomlValue.String("read-only"),
            "-c", CodexCliSession.SELF_PLUGIN_DISABLE,
            "-c", $"mcp_servers.{SERVER}.command=" + TomlValue.String(TelemetryCollectorTests.DotnetHost),
            "-c", $"mcp_servers.{SERVER}.args=[{TomlValue.String(PlanForgeDll)}]",
            "-c", $"mcp_servers.{SERVER}.env={{ {string.Join(", ", environment)} }}",
            "-c", $"mcp_servers.{SERVER}.default_tools_approval_mode=" + TomlValue.String("approve"),
        ];
        if (export) arguments.AddRange(["-c", "otel.exporter=" + CodexExporter()]);

        var stdout = await RunHostAsync(codex.FileName, arguments);

        var threadId = stdout.Split('\n')
                             .Select(line => line.Trim())
                             .Where(line => line.StartsWith('{'))
                             .Select(line => JsonDocument.Parse(line).RootElement)
                             .First(line => line.TryGetProperty("type", out var type) && type.GetString() == "thread.started")
                             .GetProperty("thread_id").GetString()!;
        return new Outcome(threadId, Records());
    }

    /// <summary>Runs a host to its end in the workspace, the prompt on its standard input.</summary>
    /// <param name="fileName">The host's executable.</param>
    /// <param name="arguments">Its arguments.</param>
    private async Task<string> RunHostAsync(string fileName, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = _workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };

        // Export settings come only from the test: none inherited from the environment it runs in.
        foreach (var name in info.Environment.Keys.Where(IsInheritedExportSetting).ToList()) info.Environment.Remove(name);
        using var host = Process.Start(info)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            var stdout = host.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = host.StandardError.ReadToEndAsync(timeout.Token);
            await host.StandardInput.WriteAsync(Prompt());
            host.StandardInput.Close();
            await host.WaitForExitAsync(timeout.Token);

            var output = await stdout;
            var errors = await stderr;
            _output.WriteLine(output);
            _output.WriteLine(errors);
            Assert.True(host.ExitCode == 0, $"the host exited with {host.ExitCode}: {errors}");
            return output;
        }
        finally
        {
            try { if (!host.HasExited) host.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
    }

    private static bool IsInheritedExportSetting(string name) =>
        name.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)
        || name.Equals("CLAUDE_CODE_ENABLE_TELEMETRY", StringComparison.OrdinalIgnoreCase);

    private string Prompt() =>
        $"""
        You are driving an automated test. Use only the MCP server named {SERVER}; do not edit any file and
        do not run any shell command. Make these three tool calls, one at a time, each only after the
        previous call has returned its result:
        1. Call forge.begin with workspaceRoot = {_workspace}.
        2. Call forge.log.append with workspaceRoot = {_workspace}, runId = the runId forge.begin returned,
           message = "planning", and phase = "planning".
        3. Call forge.log.append with the same workspaceRoot and runId, message = "done", and phase = "done".
        Then reply with the single word ok.
        """;

    // ---- Profiles ----

    /// <summary>The server's profile files: the same export the host is launched with, or none.</summary>
    /// <param name="export">Whether the files configure the telemetry export on the test's port.</param>
    private async Task WriteProfilesAsync(bool export)
    {
        var settings = new JsonObject();
        if (export) settings["env"] = ClaudeExportEnvironment();
        await File.WriteAllTextAsync(_claudeSettings, settings.ToJsonString());
        await File.WriteAllTextAsync(_codexConfig, export ? $"[otel]\nexporter = {CodexExporter()}\n" : "# no export\n");
    }

    private JsonObject ClaudeExportEnvironment() => new()
    {
        ["CLAUDE_CODE_ENABLE_TELEMETRY"] = "1",
        ["OTEL_LOGS_EXPORTER"] = "otlp",
        ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/json",
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"http://127.0.0.1:{_port}",

        // The logs-specific keys win over the generic ones, so a user profile that sets them must not
        // send the host's export elsewhere while the server reads this port.
        ["OTEL_EXPORTER_OTLP_LOGS_PROTOCOL"] = "http/json",
        ["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = $"http://127.0.0.1:{_port}/v1/logs",
        ["OTEL_LOGS_EXPORT_INTERVAL"] = "1000",
    };

    private string CodexExporter() =>
        $"{{ otlp-http = {{ endpoint = {TomlValue.String($"http://127.0.0.1:{_port}/v1/logs")}, protocol = \"json\" }} }}";

    // ---- Run and records ----

    private static string PlanForgeDll => Path.Combine(AppContext.BaseDirectory, "planforge.dll");

    private async Task InitWorkspaceAsync()
    {
        var git = new GitClient(_workspace);
        var ct = CancellationToken.None;
        await git.OutputAsync(["init", "-q"], ct);
        await git.OutputAsync(["config", "user.email", "tests@example.invalid"], ct);
        await git.OutputAsync(["config", "user.name", "PlanForge Tests"], ct);
        await File.WriteAllTextAsync(Path.Combine(_workspace, "tracked.txt"), "original\n", ct);
        await git.OutputAsync(["add", "tracked.txt"], ct);
        await git.OutputAsync(["commit", "-qm", "initial"], ct);
    }

    /// <summary>The one Run's records, sorted by <c>at</c>; its <c>forge.log</c> goes to the output.</summary>
    private IReadOnlyList<JsonElement> Records()
    {
        var run = Assert.Single(Directory.GetDirectories(Path.Combine(_workspace, ".forge")),
                                directory => File.Exists(Path.Combine(directory, "forge.log")));
        var log = Path.Combine(run, "forge.log");
        _output.WriteLine(File.ReadAllText(log));

        var telemetry = Path.Combine(run, "telemetry.json");
        Assert.True(File.Exists(telemetry), "the Run has no telemetry.json");
        var text = File.ReadAllText(telemetry);
        _output.WriteLine(text);
        return [.. JsonDocument.Parse(text).RootElement.EnumerateArray().OrderBy(record => record.GetProperty("at").GetString(), StringComparer.Ordinal)];
    }

    /// <summary>Runs a host whose server may start a collector on the test's port, then stops that collector.</summary>
    /// <param name="run">The host's run.</param>
    private async Task<Outcome> WithCollectorStoppedAsync(Func<Task<Outcome>> run)
    {
        try
        {
            return await run();
        }
        finally
        {
            try
            {
                TelemetryCollectorTests.KillById((await TelemetryCollectorTests.IdentityAsync(_port)).ProcessId);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
                // No collector answers on the port: none was caused.
            }
        }
    }

    /// <summary>The records' common assertions: <c>interview</c> fully counted, and at most one sound <c>planning</c>.</summary>
    /// <param name="outcome">The host's session and the Run's records.</param>
    /// <param name="source">The expected <c>usageSource</c>.</param>
    /// <param name="costInInterview">Whether <c>interview</c> carries a positive cost; otherwise no record has one.</param>
    private static void AssertRecords(Outcome outcome, string source, bool costInInterview)
    {
        var records = outcome.Records.Where(record => record.GetProperty("event").GetString() == "orchestrator.usage").ToList();
        Assert.All(records, record => Assert.Equal(outcome.SessionId, record.GetProperty("hostSessionId").GetString()));

        var interview = Assert.Single(records, record => record.GetProperty("phase").GetString() == "interview");
        Assert.Equal(source, interview.GetProperty("usageSource").GetString());
        Assert.False(interview.TryGetProperty("usageMissing", out _));
        Assert.False(interview.TryGetProperty("usageIncomplete", out _));
        Assert.True(interview.GetProperty("inputTokens").GetInt64() > 0);

        var planning = records.Where(record => record.GetProperty("phase").GetString() == "planning").ToList();
        Assert.True(planning.Count <= 1, $"{planning.Count} planning records");
        foreach (var record in planning)
        {
            Assert.Equal(source, record.GetProperty("usageSource").GetString());
            Assert.False(record.TryGetProperty("usageMissing", out _));
            Assert.True(record.GetProperty("inputTokens").GetInt64() >= 0);
            Assert.True(record.GetProperty("outputTokens").GetInt64() >= 0);
        }

        if (costInInterview)
            Assert.True(interview.GetProperty("costUsd").GetDouble() > 0);
        else
            Assert.All(records, record => Assert.False(record.TryGetProperty("costUsd", out _)));
    }

    /// <summary>What one host run left: its own session and the Run's records.</summary>
    /// <param name="SessionId">The session or thread id the host reported in its own JSON output.</param>
    /// <param name="Records">The Run's <c>telemetry.json</c> records, sorted by <c>at</c>.</param>
    private sealed record Outcome(string SessionId, IReadOnlyList<JsonElement> Records);
}
