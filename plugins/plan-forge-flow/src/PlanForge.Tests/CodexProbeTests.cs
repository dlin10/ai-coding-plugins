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

    private static string FixtureRoot() =>
        Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
}
