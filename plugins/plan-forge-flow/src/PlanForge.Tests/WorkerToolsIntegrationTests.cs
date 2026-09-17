using System.Net.Sockets;
using System.Text.Json;
using PlanForge.Diagnostics;
using PlanForge.Vendors;
using Xunit;
using Xunit.Abstractions;

namespace PlanForge.Tests;

/// <summary>
/// The acceptance of issue #90: a headless critic and a headless builder on every vendor call a
/// Roslyn tool with nothing in the user's own settings granting it. Runs the real CLIs, so it costs
/// money and is traited for filtering with --filter Category!=Integration.
/// </summary>
/// <remarks>
/// Needs a Roslyn MCP server listening on <c>PLANFORGE_IT_ROSLYN_PORT</c> (5051 when unset — the
/// port of this repository's own solution, served while Visual Studio has it open) and, for claude,
/// a local-scope server for this repository whose name starts with <c>roslyn-</c>, which is what
/// <c>roslyn-setup-repo</c> writes. Codex and cursor get their server from a scratch workspace this
/// test writes under the test binary — inside the repository, so codex trusts it — and the codex
/// one deliberately carries no approval key: the grant has to come from the launch.
/// </remarks>
public sealed class WorkerToolsIntegrationTests : IDisposable
{
    private const string Prompt =
        "Call the MCP tool roslyn_search_symbols, on the server whose name starts with roslyn, with the " +
        "query VendorFactory. Do not edit any file and do not run any shell command. Then report the " +
        "number of results, or the exact error the tool returned.";

    private readonly ITestOutputHelper _output;
    private readonly string _workspace = Path.Combine(AppContext.BaseDirectory, "roslyn-it", Guid.NewGuid().ToString("n"));
    private readonly string _log;
    private readonly IDisposable _logScope;

    public WorkerToolsIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        var port = Environment.GetEnvironmentVariable("PLANFORGE_IT_ROSLYN_PORT") is { Length: > 0 } value ? value : "5051";

        Directory.CreateDirectory(Path.Combine(_workspace, ".codex"));
        Directory.CreateDirectory(Path.Combine(_workspace, ".cursor"));
        File.WriteAllText(Path.Combine(_workspace, ".codex", "config.toml"),
                          $"[mcp_servers.roslyn-it]\nurl = \"http://localhost:{port}/mcp\"\nomit_tools_from = [\"deferred\"]\n");
        File.WriteAllText(Path.Combine(_workspace, ".cursor", "mcp.json"),
                          $$"""{ "mcpServers": { "roslyn-it": { "url": "http://localhost:{{port}}/mcp" } } }""");

        _log = Path.Combine(_workspace, "forge.log");
        _logScope = RunLog.Use(new RunLog(_log));

        using var probe = new TcpClient();
        Assert.True(probe.ConnectAsync("localhost", int.Parse(port)).Wait(TimeSpan.FromSeconds(5)),
                    $"no Roslyn MCP server listens on port {port}; open the solution in Visual Studio or set PLANFORGE_IT_ROSLYN_PORT");
    }

    public void Dispose()
    {
        _logScope.Dispose();
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("claude", "haiku", null, "critic")]
    [InlineData("claude", "haiku", null, "builder")]
    [InlineData("codex", "gpt-5.6-luna", "low", "critic")]
    [InlineData("codex", "gpt-5.6-luna", "low", "builder")]
    [InlineData("cursor", "auto", null, "critic")]
    [InlineData("cursor", "auto", null, "builder")]
    public async Task A_headless_worker_calls_roslyn_with_only_the_default_grant(string vendorId, string model, string? effort, string roleName)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        var role = roleName is "builder" ? VendorRole.Builder : VendorRole.Critic;
        var vendor = VendorFactory.Create(vendorId, _workspace);

        await using var session = await vendor.StartAsync(
            new RoleSpec(role, "You are a worker in an automated test. Follow the task exactly.",
                         WorkerTools: WorkerTools.Effective(null)),
            new Selection(model, effort), resumeToken: null, ct);

        var events = new List<VendorEvent>();
        var reading = Task.Run(async () =>
        {
            await foreach (var raised in session.Events.WithCancellation(ct)) events.Add(raised);
        }, ct);

        if (role is VendorRole.Builder)
            await session.RunAsync(Prompt, Schemas.BuildResult, ct);
        else
            await session.RunAsync(Prompt, Schemas.Critique, ct);

        await session.DisposeAsync();
        await reading;

        foreach (var raised in events.Where(raised => raised.Kind is VendorEventKind.ToolUse or VendorEventKind.ToolResult))
            _output.WriteLine($"{raised.Kind} {raised.Text} {string.Join(" ", raised.Fields?.Select(field => $"{field.Name}={field.Value}") ?? [])}");

        Assert.Contains(events, raised => raised.Kind is VendorEventKind.ToolResult
                                          && raised.Fields is { } fields
                                          && fields.Contains(("isError", "false"))
                                          && fields.Any(field => field.Name == "output" && field.Value?.Contains("VendorFactory") is true));

        var grant = Assert.Single(File.ReadAllLines(_log).Select(line => JsonDocument.Parse(line).RootElement),
                                  entry => entry.GetProperty("event").GetString() == "worker.tools");
        var granted = grant.GetProperty("fields");
        _output.WriteLine(granted.GetRawText());
        Assert.Equal("roslyn-*", granted.GetProperty("patterns").GetString());
        if (vendorId is not "cursor")
            Assert.StartsWith("roslyn-", granted.GetProperty("servers").GetString(), StringComparison.Ordinal);
    }
}
