using System.Diagnostics;
using PlanForgeFlow.Cli;

namespace PlanForgeFlow.OpenAI;

internal sealed record CodexLaunch(string Executable, string LaunchKind)
{
    public ProcessStartInfo CreateAppServerStartInfo()
    {
        ProcessStartInfo startInfo;
        if (LaunchKind == "cmd-shim")
        {
            if (Executable.IndexOfAny(['"', '\r', '\n', '%']) >= 0)
                throw new CliFailure("process", "Codex .cmd/.bat path contains characters that cannot be launched safely", 3);
            var commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(commandProcessor)) commandProcessor = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            startInfo = new ProcessStartInfo(commandProcessor)
            {
                Arguments = $"/d /s /c \"\"{Executable}\" app-server\"",
            };
        }
        else
        {
            startInfo = new ProcessStartInfo(Executable);
            startInfo.ArgumentList.Add("app-server");
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        return startInfo;
    }
}

internal sealed record CodexExecutableResolution(string Status,
                                                 CodexLaunch? Launch,
                                                 string? Error,
                                                 string? FoundExecutable = null,
                                                 string? FoundLaunchKind = null);

internal static class CodexExecutableResolver
{
    public static CodexExecutableResolution Resolve()
        => Resolve(Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());

    internal static CodexExecutableResolution Resolve(string? pathValue, bool windows)
    {
        string? unsupported = null;
        foreach (var rawDirectory in (pathValue ?? string.Empty).Split(windows ? ';' : Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0) continue;
            if (!windows)
            {
                var candidate = Path.Combine(directory, "codex");
                if (File.Exists(candidate)) return new CodexExecutableResolution("ready", new CodexLaunch(Path.GetFullPath(candidate), "native"), null);
                continue;
            }

            foreach (var (name, kind) in new[] { ("codex.exe", "native"), ("codex.cmd", "cmd-shim"), ("codex.bat", "cmd-shim") })
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return new CodexExecutableResolution("ready", new CodexLaunch(Path.GetFullPath(candidate), kind), null);
            }

            unsupported ??= new[] { "codex.ps1", "codex" }
                .Select(name => Path.Combine(directory, name))
                .FirstOrDefault(File.Exists);
        }

        return unsupported is null
            ? new CodexExecutableResolution("absent", null, null)
            : new CodexExecutableResolution("unusable", null,
                $"Codex was found at {Path.GetFullPath(unsupported)}, but this Windows launcher type is unsupported; install a native executable or npm .cmd/.bat shim",
                Path.GetFullPath(unsupported), "unsupported");
    }

    public static CodexLaunch RequireLaunch()
    {
        var resolution = Resolve();
        if (resolution.Launch is not null) return resolution.Launch;
        throw new CliFailure("process", resolution.Error ?? "Codex executable was not found on PATH", 3);
    }
}
