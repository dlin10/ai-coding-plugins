using System.Diagnostics;
using Xunit;

namespace ConcurrencyHunter.Cli.Tests;

public sealed class LauncherTests
{
    [Fact]
    public async Task Launcher_without_a_local_executable_tries_the_release_and_reports_the_failure_on_stderr()
    {
        var launcher = FindLauncher();
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"concurrency-hunter-launcher-{Guid.NewGuid():N}");
        var pluginRoot = Path.Combine(temporaryRoot, "plugin");
        var pluginBin = Path.Combine(pluginRoot, "bin");
        var manifestDirectory = Path.Combine(pluginRoot, ".claude-plugin");
        var localAppData = Path.Combine(temporaryRoot, "local");
        var emptySystemRoot = Path.Combine(temporaryRoot, "sysroot");
        Directory.CreateDirectory(pluginBin);
        Directory.CreateDirectory(manifestDirectory);
        Directory.CreateDirectory(emptySystemRoot);

        try
        {
            var copiedLauncher = Path.Combine(pluginBin, "concurrency-hunter-launcher.cmd");
            File.Copy(launcher, copiedLauncher);
            File.WriteAllText(Path.Combine(manifestDirectory, "plugin.json"), """
                {
                  "version": "0.0.0-test",
                  "name": "concurrency-hunter"
                }
                """);

            var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(copiedLauncher);
            startInfo.Environment["LOCALAPPDATA"] = localAppData;
            startInfo.Environment["SystemRoot"] = emptySystemRoot;

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the launcher.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw;
            }

            Assert.Equal(1, process.ExitCode);
            Assert.Empty(await standardOutput);
            var error = await standardError;
            Assert.Contains("concurrency-hunter: downloading concurrency-hunter.exe 0.0.0-test from https://github.com/dlin10/ai-coding-plugins/releases/download/concurrency-hunter-v0.0.0-test/concurrency-hunter.exe", error);
            Assert.Contains("concurrency-hunter: could not fetch", error);
            Assert.False(Directory.Exists(localAppData) &&
                         Directory.EnumerateFiles(localAppData, "concurrency-hunter.exe", SearchOption.AllDirectories).Any());
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static string FindLauncher()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "plugins", "concurrency-hunter", "bin", "concurrency-hunter-launcher.cmd");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("Could not locate plugins/concurrency-hunter/bin/concurrency-hunter-launcher.cmd above the test binary.");
    }
}
