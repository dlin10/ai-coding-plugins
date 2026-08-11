using System.Diagnostics;

namespace FindFiles.Everything;

internal sealed record EsInvocation(int ExitCode, IReadOnlyList<string> StandardOutputLines, string StandardError);

internal interface IEsRunner
{
    EsInvocation Run(IReadOnlyList<string> arguments);
}

/// <summary>
/// Exit codes <c>es.exe</c> uses that carry meaning for us. Verified against ES 1.1.0.37:
/// 8 is reported when the Everything IPC window is missing (the engine is not running) and 9 when a
/// search matched nothing and <c>-no-result-error</c> was passed.
/// </summary>
internal static class EsExitCode
{
    internal const int Success = 0;
    internal const int EverythingNotRunning = 8;
    internal const int NoResults = 9;
}

internal sealed class EsRunner(string executablePath) : IEsRunner
{
    public EsInvocation Run(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executablePath}");

        // Read both streams before waiting so a chatty child cannot fill a pipe and deadlock.
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        var lines = standardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                  .Select(line => line.TrimEnd('\r'))
                                  .Where(line => line.Length > 0)
                                  .ToList();

        return new EsInvocation(process.ExitCode, lines, standardError.Trim());
    }
}
