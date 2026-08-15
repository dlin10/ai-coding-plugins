using System.Text.RegularExpressions;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Serialization;

namespace PlanForgeFlow.Claude;

internal static class ClaudeCapabilities
{
    internal const string MinimumVersion = "2.1.232";
    private static readonly Version Minimum = Version.Parse(MinimumVersion);
    private static readonly Regex VersionPattern = new("(?:^|\\s)([0-9]+\\.[0-9]+\\.[0-9]+)(?:[-+][^\\s]+)?(?:\\s|$)",
                                                       RegexOptions.CultureInvariant);

    public static ToolCheckData Probe()
    {
        try
        {
            var result = OperatingSystem.IsWindows()
                ? ProcessExecution.Run("cmd.exe", ["/d", "/c", "claude --version"])
                : ProcessExecution.Run("claude", ["--version"]);
            if (result.ExitCode != 0) return new ToolCheckData(false, null, result.Stderr.Trim());
            return EvaluateVersion(result.Stdout.Trim());
        }
        catch (CliFailure error)
        {
            return new ToolCheckData(false, null, error.Message);
        }
    }

    internal static ToolCheckData EvaluateVersion(string output)
    {
        var match = VersionPattern.Match(output);
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version))
            return new ToolCheckData(false, output, "could not parse the Claude Code version");
        return version >= Minimum
            ? new ToolCheckData(true, output, null)
            : new ToolCheckData(false, output, $"Claude Code {MinimumVersion} or newer is required for persistent builder resume");
    }

    internal static void RequirePersistentBuilder(Func<ToolCheckData>? probe = null)
    {
        var capability = (probe ?? Probe)();
        if (!capability.Ok)
            throw new CliFailure("environment", capability.Error ?? $"Claude Code {MinimumVersion} or newer is required for persistent builder resume");
    }
}
