using PlanForge.Vendors.Codex;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// Docs/adr/0013: codex resolves its shell from the PATH of the process forge starts, and a Store
/// app execution alias for pwsh answers a restricted token with access denied rather than file not
/// found. These pin the repair against fixture directories standing in for a real PATH.
/// </summary>
public sealed class CodexLaunchTests
{
    [Fact]
    public void Repairs_a_path_whose_only_pwsh_is_the_store_alias()
    {
        var root = FixtureRoot();
        try
        {
            var aliasDirectory = Path.Combine(root, "WindowsApps");
            var systemDirectory = Path.Combine(root, "System32");
            Directory.CreateDirectory(aliasDirectory);
            Directory.CreateDirectory(systemDirectory);

            WriteZeroLength(Path.Combine(aliasDirectory, "pwsh.exe"));
            var powershell = WriteNonEmpty(Path.Combine(systemDirectory, "powershell.exe"));

            var path = string.Join(Path.PathSeparator, aliasDirectory, systemDirectory);

            var result = CodexLaunch.Inspect(path);

            Assert.True(result.Repaired);
            Assert.DoesNotContain("WindowsApps", result.Path, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Path.GetFullPath(powershell), result.Shell);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Leaves_a_path_unchanged_when_pwsh_is_a_real_executable()
    {
        var root = FixtureRoot();
        try
        {
            var directory = Path.Combine(root, "pwsh7");
            Directory.CreateDirectory(directory);
            var pwsh = WriteNonEmpty(Path.Combine(directory, "pwsh.exe"));

            var result = CodexLaunch.Inspect(directory);

            Assert.False(result.Repaired);
            Assert.Equal(directory, result.Path);
            Assert.Equal(Path.GetFullPath(pwsh), result.Shell);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Falls_back_to_powershell_when_pwsh_is_a_zero_length_file_outside_windowsapps()
    {
        var root = FixtureRoot();
        try
        {
            var pwshDirectory = Path.Combine(root, "pwsh7");
            var systemDirectory = Path.Combine(root, "System32");
            Directory.CreateDirectory(pwshDirectory);
            Directory.CreateDirectory(systemDirectory);

            WriteZeroLength(Path.Combine(pwshDirectory, "pwsh.exe"));
            var powershell = WriteNonEmpty(Path.Combine(systemDirectory, "powershell.exe"));

            var path = string.Join(Path.PathSeparator, pwshDirectory, systemDirectory);

            var result = CodexLaunch.Inspect(path);

            Assert.False(result.Repaired);
            Assert.Equal(Path.GetFullPath(powershell), result.Shell);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Reports_no_shell_when_only_the_alias_is_on_the_path()
    {
        var root = FixtureRoot();
        try
        {
            var aliasDirectory = Path.Combine(root, "WindowsApps");
            Directory.CreateDirectory(aliasDirectory);
            WriteZeroLength(Path.Combine(aliasDirectory, "pwsh.exe"));

            var result = CodexLaunch.Inspect(aliasDirectory);

            Assert.True(result.Repaired);
            Assert.Null(result.Shell);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void A_null_path_is_left_alone()
    {
        var result = CodexLaunch.Inspect(null);

        Assert.False(result.Repaired);
        Assert.Null(result.Shell);
        Assert.Null(result.Path);
    }

    private static string FixtureRoot() =>
        Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    private static void WriteZeroLength(string path) => File.WriteAllBytes(path, []);

    private static string WriteNonEmpty(string path)
    {
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
}
