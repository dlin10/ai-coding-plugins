using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace CodexSessionPulse.Win32.Tests;

/// <summary><c>hooks/hooks.json</c> against spec §4.4, and the cmd launcher's fast path with the real exe.</summary>
[Collection(ExeCollection.Name)]
public sealed class LauncherTests
{
    /// <summary>The three hooks run the launcher with the timeouts, async flags and arguments of §4.4, and SessionStart has no matcher.</summary>
    [Fact]
    public void HooksJson_MatchesTheSpec()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(PluginRoot(), "hooks", "hooks.json")));
        JsonElement hooks = document.RootElement.GetProperty("hooks");

        Assert.Equal(["SessionStart", "UserPromptSubmit", "SessionEnd"], hooks.EnumerateObject().Select(property => property.Name));
        Check(hooks, "SessionStart", "--hook", async: true, timeout: 60);
        Check(hooks, "UserPromptSubmit", "--hook", async: true, timeout: 10);
        Check(hooks, "SessionEnd", "--hook --no-start", async: false, timeout: 3);
    }

    /// <summary>The launcher finds the exe by the manifest version and passes stdin through; the hook reaches the overlay it starts.</summary>
    [Fact]
    public void Launcher_RunsTheVersionedExe()
    {
        using LifecycleHome home = new();
        string bin = Path.Combine(home.Root, "bin", ManifestVersion());
        Directory.CreateDirectory(bin);

        foreach (string file in Directory.GetFiles(Path.GetDirectoryName(LifecycleHome.Exe)!))
        {
            File.Copy(file, Path.Combine(bin, Path.GetFileName(file)));
        }

        (int exitCode, _) = RunLauncher(home, "--hook", "{\"hook_event_name\":\"SessionStart\",\"source\":\"startup\",\"transcript_path\":null}");

        Assert.Equal(0, exitCode);
        Assert.True(LifecycleHome.WaitForLog(home.OverlayLog, "hook SessionStart (startup)"));
    }

    /// <summary>
    /// Without the exe, SessionEnd (<c>--no-start</c>) says where it looked, downloads nothing and exits 0. The download
    /// path itself is exercised by <c>build/test-launcher.ps1</c> against a local release, never the network.
    /// </summary>
    [Fact]
    public void Launcher_MissingExe_SessionEndExplainsAndDownloadsNothing()
    {
        using LifecycleHome home = new();

        (int hookExit, string hookError) = RunLauncher(home, "--hook --no-start", "{}");

        Assert.Equal(0, hookExit);
        Assert.Contains("is missing", hookError, StringComparison.Ordinal);
        Assert.DoesNotContain("downloading", hookError, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("bin", ManifestVersion(), "CodexSessionPulse.exe"), hookError, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(home.Root, "bin")));
    }

    private static void Check(JsonElement hooks, string name, string arguments, bool async, int timeout)
    {
        JsonElement group = Assert.Single(hooks.GetProperty(name).EnumerateArray());
        JsonElement hook = Assert.Single(group.GetProperty("hooks").EnumerateArray());

        Assert.False(group.TryGetProperty("matcher", out _), $"{name} must not have a matcher.");
        Assert.Equal("exit 0", hook.GetProperty("command").GetString());
        Assert.Equal($"cmd.exe /d /c \"${{PLUGIN_ROOT}}\\bin\\codex-session-pulse-launcher.cmd\" {arguments}", hook.GetProperty("commandWindows").GetString());
        Assert.Equal(async, hook.TryGetProperty("async", out JsonElement value) && value.GetBoolean());
        Assert.Equal(timeout, hook.GetProperty("timeout").GetInt32());
    }

    private static (int ExitCode, string Stderr) RunLauncher(LifecycleHome home, string arguments, string stdin)
    {
        string launcher = Path.Combine(PluginRoot(), "bin", "codex-session-pulse-launcher.cmd");
        ProcessStartInfo startInfo = new(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c \"\"{launcher}\" {arguments}\"")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        startInfo.Environment["CODEX_SESSION_PULSE_HOME"] = home.Root;
        startInfo.Environment["CODEX_HOME"] = home.CodexHome;

        using Process process = Process.Start(startInfo)!;
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        string stderr = process.StandardError.ReadToEnd();

        Assert.True(process.WaitForExit(30_000), "The launcher did not exit.");

        return (process.ExitCode, stderr);
    }

    private static string ManifestVersion()
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(PluginRoot(), ".codex-plugin", "plugin.json")));

        return manifest.RootElement.GetProperty("version").GetString()!;
    }

    private static string PluginRoot() =>
        typeof(LauncherTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "PluginRoot").Value!;
}
