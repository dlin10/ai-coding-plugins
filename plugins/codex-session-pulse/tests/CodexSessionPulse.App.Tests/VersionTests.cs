using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace CodexSessionPulse.App.Tests;

/// <summary>
/// The manifest's version is the one source: the exe, the README title and the CHANGELOG must name it. A plugin bump that
/// forgets one of them fails here, before a release is cut with a binary that reports another version.
/// </summary>
public sealed class VersionTests
{
    /// <summary>The exe's informational version is the manifest version, with no commit hash appended.</summary>
    [Fact]
    public void Exe_ReportsTheManifestVersion()
    {
        string? version = typeof(HookRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.Equal(ManifestVersion(), version);
    }

    /// <summary>The README title carries the manifest version.</summary>
    [Fact]
    public void ReadmeTitle_NamesTheManifestVersion()
    {
        string title = File.ReadLines(Path.Combine(PluginRoot(), "README.md")).First();

        Assert.Equal($"# Codex Session Pulse {ManifestVersion()}", title);
    }

    /// <summary>Every version folder the README's commands name (<c>\0.1.0\</c>) is the manifest version.</summary>
    [Fact]
    public void ReadmePaths_NameTheManifestVersion()
    {
        string[] versions = Regex.Matches(File.ReadAllText(Path.Combine(PluginRoot(), "README.md")), @"\\(\d+\.\d+\.\d+)\\")
                                 .Select(match => match.Groups[1].Value)
                                 .ToArray();

        Assert.NotEmpty(versions);
        Assert.All(versions, version => Assert.Equal(ManifestVersion(), version));
    }

    /// <summary>The newest CHANGELOG entry is the manifest version.</summary>
    [Fact]
    public void Changelog_TopEntryIsTheManifestVersion()
    {
        string heading = File.ReadLines(Path.Combine(PluginRoot(), "CHANGELOG.md")).First(line => line.StartsWith("## ", StringComparison.Ordinal));

        Assert.Matches($"^## {Regex.Escape(ManifestVersion())}( |$)", heading);
    }

    private static string ManifestVersion()
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(PluginRoot(), ".codex-plugin", "plugin.json")));

        return manifest.RootElement.GetProperty("version").GetString()!;
    }

    private static string PluginRoot() =>
        typeof(VersionTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "PluginRoot").Value!;
}
