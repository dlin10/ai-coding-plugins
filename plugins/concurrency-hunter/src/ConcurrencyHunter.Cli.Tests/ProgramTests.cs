using Common.Mcp;
using Xunit;

namespace ConcurrencyHunter.Cli.Tests;

public sealed class ProgramTests
{
    [Fact]
    public async Task Version_flag_prints_the_build_version_and_returns_ok()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(["--version"], output, error);

        Assert.Equal(ExitCode.Ok, exitCode);
        Assert.Equal("0.1.0", BuildInfo.Version);
        Assert.Equal(BuildInfo.Version + Environment.NewLine, output.ToString());
    }

    [Fact]
    public async Task No_arguments_print_usage_to_error_and_return_usage_error()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync([], output, error);

        Assert.Equal(ExitCode.UsageError, exitCode);
        Assert.Contains("Usage:", error.ToString());
    }

    [Fact]
    public async Task Unknown_command_returns_usage_error_and_names_the_command()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(["unknown"], output, error);

        Assert.Equal(ExitCode.UsageError, exitCode);
        Assert.Contains("Unknown command: unknown", error.ToString());
    }
}
