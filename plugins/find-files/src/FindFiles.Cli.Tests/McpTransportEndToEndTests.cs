using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace FindFiles.Tests;

/// <summary>
/// Drives the published executable over a real stdio pipe. This is the only level that can catch the
/// failure mode that matters most here: anything written to stdout besides JSON-RPC corrupts the
/// transport, and the sole symptom a user sees is a server that will not start.
/// </summary>
public sealed class McpTransportEndToEndTests(ITestOutputHelper output)
{
    /// <summary>
    /// Set to 1 in CI. A test that quietly passes because the binary was not published is worse than
    /// no test at all, so CI refuses the skip.
    /// </summary>
    private const string RequireVariable = "FINDFILES_REQUIRE_E2E";

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);

    private static string? FindPublishedExecutable()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "bin", "win-x64", "esfind.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private bool SkipUnlessPublished(out string executable)
    {
        var found = FindPublishedExecutable();
        if (found is not null)
        {
            executable = found;
            return false;
        }

        executable = string.Empty;
        var message = "The published bin/win-x64/esfind.exe was not found. Run build/package.ps1 first.";
        if (Environment.GetEnvironmentVariable(RequireVariable) == "1")
        {
            Assert.Fail(message);
        }

        output.WriteLine($"Skipped: {message}");
        return true;
    }

    [Fact]
    public void TheServerCompletesAHandshakeAndWritesNothingButJsonRpcToStandardOutput()
    {
        if (SkipUnlessPublished(out var executable))
        {
            return;
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // No byte order mark detection: a stray mark must show up as a broken line, not be
            // silently swallowed by the reader.
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        startInfo.ArgumentList.Add("mcp");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        process.StandardInput.NewLine = "\n";
        process.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
        process.StandardInput.WriteLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        process.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        process.StandardInput.Flush();

        var initialize = ReadLine(process, "initialize");
        var toolList = ReadLine(process, "tools/list");

        process.StandardInput.Close();
        Assert.True(process.WaitForExit((int)ReadTimeout.TotalMilliseconds), "The server did not exit after stdin closed.");

        var trailing = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        output.WriteLine($"stderr: {standardError}");

        // The notification must not have produced a response, and nothing may follow the two replies.
        Assert.True(string.IsNullOrWhiteSpace(trailing), $"Unexpected trailing stdout: {trailing}");

        Assert.StartsWith("{", initialize, StringComparison.Ordinal);
        var initializeResult = JsonDocument.Parse(initialize).RootElement.GetProperty("result");
        Assert.Equal("2025-06-18", initializeResult.GetProperty("protocolVersion").GetString());

        var tools = JsonDocument.Parse(toolList).RootElement.GetProperty("result").GetProperty("tools");
        Assert.Equal(1, tools.GetArrayLength());
        Assert.Equal("find_file_anywhere", tools[0].GetProperty("name").GetString());
    }

    [Fact]
    public void TheReportedVersionMatchesTheHostManifests()
    {
        if (SkipUnlessPublished(out var executable))
        {
            return;
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--version");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var reported = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        var pluginRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(executable)!)!)!;
        foreach (var manifest in new[] { ".claude-plugin", ".codex-plugin", ".cursor-plugin" })
        {
            var path = Path.Combine(pluginRoot, manifest, "plugin.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(document.RootElement.GetProperty("version").GetString(), reported);
        }
    }

    private static string ReadLine(Process process, string what)
    {
        var read = Task.Run(process.StandardOutput.ReadLine);
        Assert.True(read.Wait(ReadTimeout), $"Timed out waiting for the {what} response.");
        var line = read.Result;
        Assert.False(string.IsNullOrWhiteSpace(line), $"The server closed stdout before answering {what}.");
        return line!;
    }
}
