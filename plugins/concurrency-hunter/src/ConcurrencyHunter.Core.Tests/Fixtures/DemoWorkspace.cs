using System.Diagnostics;

namespace ConcurrencyHunter.Core.Tests.Fixtures;

internal static class DemoWorkspace
{
    private static readonly Lazy<Task> Restore = new(RestoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static Task EnsureRestoredAsync() => Restore.Value;

    private static async Task RestoreAsync()
    {
        var solution = RepositoryFiles.FindRepositoryFile(
            "plugins", "concurrency-hunter", "demo", "Demo.slnx");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(solution)!
        };
        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add(solution);
        startInfo.ArgumentList.Add("--nologo");
        // Without these, a restore that finds no MSBuild node running starts reusable nodes which inherit
        // the redirected stdout and stderr and keep them open for the nodes' 15-minute idle timeout, so the
        // reads below, and the test awaiting them, stall that long after the restore itself has exited.
        startInfo.ArgumentList.Add("--disable-build-servers");
        startInfo.ArgumentList.Add("-nodeReuse:false");

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start dotnet restore for the demo.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet restore exited {process.ExitCode}.{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{await standardOutput}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{await standardError}");
        }

        _ = await standardOutput;
        _ = await standardError;
    }
}
