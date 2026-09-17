using System.Text.Json;
using PlanForge.Vendors;
using PlanForge.Vendors.Claude;
using PlanForge.Vendors.Codex;
using Xunit;

namespace PlanForge.Tests;

public sealed class CodexProbeTests
{
    [Fact]
    public void Shell_readiness_names_the_store_alias_when_no_real_powershell_is_on_the_path()
    {
        var root = FixtureRoot();
        try
        {
            var aliasDirectory = Path.Combine(root, "WindowsApps");
            Directory.CreateDirectory(aliasDirectory);
            File.WriteAllBytes(Path.Combine(aliasDirectory, "pwsh.exe"), []);

            var readiness = CodexCliVendor.ShellReadiness(aliasDirectory);

            Assert.NotNull(readiness);
            Assert.False(readiness.Available);
            Assert.Contains("Store", readiness.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Shell_readiness_is_satisfied_by_a_real_powershell()
    {
        var root = FixtureRoot();
        try
        {
            var directory = Path.Combine(root, "System32");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "powershell.exe"), [1, 2, 3]);

            Assert.Null(CodexCliVendor.ShellReadiness(directory));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Vendor_factory_builds_the_right_vendor_or_refuses_an_unknown_id()
    {
        Assert.IsType<ClaudeCliVendor>(VendorFactory.Create(null, "."));
        Assert.IsType<CodexCliVendor>(VendorFactory.Create("codex", "."));
        Assert.Throws<VendorException>(() => VendorFactory.Create("grok", "."));
    }

    [Fact]
    public void A_doctor_failing_only_on_sandbox_helpers_leaves_codex_available_with_a_warning()
    {
        // Recorded from codex-cli 0.154.0 on 2026-09-17, which exited 1 with this report while
        // `codex exec --sandbox read-only` answered; details are dropped, ids and verdicts kept.
        using var document = JsonDocument.Parse(
            """
            {
              "schemaVersion": 1,
              "overallStatus": "fail",
              "codexVersion": "0.154.0",
              "checks": {
                "app_server.status": { "id": "app_server.status", "category": "app-server", "status": "ok", "summary": "background server is not running" },
                "auth.credentials": { "id": "auth.credentials", "category": "auth", "status": "ok", "summary": "auth is configured" },
                "config.load": { "id": "config.load", "category": "config", "status": "ok", "summary": "config loaded" },
                "desktop.app.version": { "id": "desktop.app.version", "category": "desktop", "status": "ok", "summary": "the desktop application is installed" },
                "desktop.app_server.handshake": { "id": "desktop.app_server.handshake", "category": "desktop", "status": "ok", "summary": "the desktop application is not running" },
                "desktop.security.enforcement": { "id": "desktop.security.enforcement", "category": "desktop", "status": "ok", "summary": "no locally visible recent Codex security enforcement was found" },
                "git.environment": { "id": "git.environment", "category": "git", "status": "ok", "summary": "git executable found; execution not verified" },
                "git.worktree.dev_drive": { "id": "git.worktree.dev_drive", "category": "git", "status": "ok", "summary": "no Git worktree is active" },
                "installation": { "id": "installation", "category": "install", "status": "ok", "summary": "installation looks consistent" },
                "mcp.config": { "id": "mcp.config", "category": "mcp", "status": "warning", "summary": "MCP configuration has optional issues" },
                "network.env": { "id": "network.env", "category": "network", "status": "ok", "summary": "network-related environment looks readable" },
                "network.provider_reachability": { "id": "network.provider_reachability", "category": "reachability", "status": "ok", "summary": "active provider endpoints are reachable over HTTP" },
                "network.websocket_reachability": { "id": "network.websocket_reachability", "category": "websocket", "status": "ok", "summary": "Responses WebSocket handshake succeeded" },
                "runtime.provenance": { "id": "runtime.provenance", "category": "runtime", "status": "ok", "summary": "running npm on windows-x86_64" },
                "runtime.search": { "id": "runtime.search", "category": "search", "status": "ok", "summary": "search command found (bundled); execution not verified" },
                "sandbox.helpers": { "id": "sandbox.helpers", "category": "sandbox", "status": "fail", "summary": "elevated Windows sandbox provisioning recorded a structured failure" },
                "security.endpoint": { "id": "security.endpoint", "category": "security", "status": "warning", "summary": "endpoint protection detected; Codex exclusions are unverified" },
                "state.paths": { "id": "state.paths", "category": "state", "status": "ok", "summary": "state paths and databases are inspectable" },
                "state.rollout_db_parity": { "id": "state.rollout_db_parity", "category": "threads", "status": "ok", "summary": "rollout files and state DB thread inventory agree" },
                "system.disk": { "id": "system.disk", "category": "disk", "status": "ok", "summary": "sufficient free disk space (37.9 GiB)" },
                "system.environment": { "id": "system.environment", "category": "system", "status": "ok", "summary": "OS language en-US" },
                "terminal.env": { "id": "terminal.env", "category": "terminal", "status": "ok", "summary": "terminal metadata was detected" },
                "terminal.title": { "id": "terminal.title", "category": "title", "status": "ok", "summary": "terminal title default" },
                "updates.status": { "id": "updates.status", "category": "updates", "status": "ok", "summary": "update configuration is locally consistent" }
              }
            }
            """);

        var (refusal, warning) = CodexCliVendor.JudgeDoctor(document.RootElement);

        Assert.Null(refusal);
        Assert.NotNull(warning);
        Assert.Contains("sandbox.helpers failed", warning, StringComparison.Ordinal);
        Assert.Contains("structured failure", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp.config", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("auth.credentials", "fail")]
    [InlineData("auth.credentials", "warning")]
    [InlineData("installation", "fail")]
    [InlineData("config.load", "fail")]
    public void A_doctor_failing_a_check_a_worker_needs_withholds_codex(string failing, string status)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "checks": {
                "auth.credentials": { "status": "ok", "summary": "auth is configured" },
                "installation": { "status": "ok", "summary": "installation looks consistent" },
                "config.load": { "status": "ok", "summary": "config loaded" },
                "{{failing}}": { "status": "{{status}}", "summary": "broken {{failing}}" }
              }
            }
            """);

        var (refusal, warning) = CodexCliVendor.JudgeDoctor(document.RootElement);

        Assert.NotNull(refusal);
        Assert.False(refusal.Available);
        Assert.Equal($"broken {failing}", refusal.Detail);
        Assert.Null(warning);
    }

    private static string FixtureRoot() =>
        Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
}
