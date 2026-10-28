using System.Text.Json;
using PlanForge.Advisors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// Codex hands a STDIO server only a small default environment plus the names its configuration
/// lists in <c>env_vars</c>, so a key the user exported never reached the server and every Advisor
/// read as absent (issue #205). Claude Code inherits the whole environment and needs no list.
/// </summary>
public sealed class CodexMcpConfigurationTests
{
    /// <summary>
    /// Read through the Codex manifest's own <c>mcpServers</c> path, so the check follows the file
    /// Codex actually loads rather than one that merely sits in the plugin root.
    /// </summary>
    [Fact]
    public void The_Codex_server_configuration_forwards_every_Advisor_variable()
    {
        var pluginRoot = PluginRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(pluginRoot, ".codex-plugin", "plugin.json")));
        var configurationPath = Path.Combine(pluginRoot, manifest.RootElement.GetProperty("mcpServers").GetString()!);
        using var configuration = JsonDocument.Parse(File.ReadAllText(configurationPath));
        var server = configuration.RootElement.GetProperty("mcpServers").GetProperty("plan-forge-flow");

        var forwarded = server.GetProperty("env_vars").EnumerateArray().Select(name => name.GetString()).ToList();
        var advisorVariables = AdvisorCatalog.Default.Variables;

        Assert.NotEmpty(advisorVariables);
        Assert.All(advisorVariables, name => Assert.Contains(name, forwarded));
        Assert.False(server.TryGetProperty("env", out _), "the configuration names variables; values belong to the user's environment");
    }

    /// <summary>Found by the Codex manifest above the test binary.</summary>
    private static string PluginRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".codex-plugin", "plugin.json"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("could not locate .codex-plugin/plugin.json above the test binary");
    }
}
