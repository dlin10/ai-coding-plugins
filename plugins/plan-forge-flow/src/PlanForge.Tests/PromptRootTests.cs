using System.Diagnostics;
using PlanForge.Prompts;
using PlanForge.Vendors;
using PlanForge.Vendors.Cursor;
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

        Assert.True(File.Exists(Path.Combine(located, "critic-contract.md")),
            $"the walk-up answered {located}, which holds no role prompts");
    }

    [Fact]
    public void A_configured_root_is_what_the_library_then_loads_from()
    {
        var root = Path.Combine(_temp, "prompts");
        Directory.CreateDirectory(Path.Combine(root, "cursor"));
        File.WriteAllText(Path.Combine(root, "critic-contract.md"), "judge it");
        File.WriteAllText(Path.Combine(root, "cursor", "critic.md"), "answer in JSON");

        var prompt = new PromptLibrary(PromptLibrary.Locate(root)).Load("cursor", VendorRole.Critic);

        // Both halves, in order: the role contract, then what differs about this vendor.
        Assert.Contains("judge it", prompt, StringComparison.Ordinal);
        Assert.Contains("answer in JSON", prompt, StringComparison.Ordinal);
        Assert.True(prompt.IndexOf("judge it", StringComparison.Ordinal)
                    < prompt.IndexOf("answer in JSON", StringComparison.Ordinal),
            "the role contract must come before the vendor's own tail");
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

    [Fact]
    public async Task The_launcher_exits_before_starting_the_server_in_a_cursor_worker()
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(Launcher());
        start.Environment[CursorAgentSession.SelfExclusionEnvironment] = "1";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("launcher did not start");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(0, process.ExitCode);
        Assert.Empty(await process.StandardOutput.ReadToEndAsync(timeout.Token));
        Assert.Empty(await process.StandardError.ReadToEndAsync(timeout.Token));
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
            var candidate = Path.Combine(directory.FullName, "bin", "planforge-launcher.cmd");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("could not locate planforge-launcher.cmd above the test binary");
    }
}
