using PlanForge.Prompts;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// Where the prompts are, when the executable cannot see them from where it stands. The layout that
/// broke: the launcher downloads the bare executable into a per-version cache under %LOCALAPPDATA%,
/// the prompts ship in the plugin package and never travel with the release asset, and the walk-up
/// from the binary has nothing above it to find — so every act died on its first prompt.
/// </summary>
public sealed class PromptRootTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
    }

    /// <summary>
    /// A path that does not exist, deliberately: the launcher sets the variable only when the folder
    /// is there, so the library takes it as given rather than probing it. Falling back here would
    /// answer a broken install with the same guess that failed in the first place.
    /// </summary>
    [Fact]
    public void A_configured_root_is_taken_as_given()
    {
        var configured = Path.Combine(_temp, "nowhere");

        Assert.Equal(configured, PromptLibrary.Locate(configured));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_root_falls_back_to_the_walk_up(string? configured)
    {
        var located = PromptLibrary.Locate(configured);

        Assert.True(File.Exists(Path.Combine(located, "codex", "critic.md")),
            $"the walk-up answered {located}, which holds no vendor prompts");
    }

    [Fact]
    public void A_configured_root_is_what_the_library_then_loads_from()
    {
        var root = Path.Combine(_temp, "prompts");
        Directory.CreateDirectory(Path.Combine(root, "codex"));
        File.WriteAllText(Path.Combine(root, "codex", "critic.md"), "judge it");

        var prompt = new PromptLibrary(PromptLibrary.Locate(root)).Load("codex", VendorRole.Critic);

        Assert.Contains("judge it", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The contract has two halves in two languages, and nothing but this pins them together: the
    /// server reads the variable, the launcher is the only thing that knows a plugin root to put in
    /// it. Reading the constant rather than spelling it out is the point — renaming it in C# alone
    /// turns this red.
    /// </summary>
    [Fact]
    public void The_launcher_sets_the_variable_the_library_reads()
    {
        Assert.Contains(PromptLibrary.RootVariable, File.ReadAllText(Launcher()), StringComparison.Ordinal);
    }

    /// <summary>
    /// Found by the launcher itself rather than by the prompts folder beside it: the build copies
    /// the prompts into every output directory, so a walk-up looking for them stops at the test
    /// binary and never reaches the repository.
    /// </summary>
    private static string Launcher()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "bin", "planforge-launcher.ps1");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("could not locate planforge-launcher.ps1 above the test binary");
    }
}
