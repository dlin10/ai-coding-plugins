using System.Text.Json;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.OpenAI;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.State;
using Xunit;
using ForgeWorkflow = PlanForgeFlow.Workflow.ForgeWorkflow;

namespace PlanForgeFlow.Tests;

[Collection("Process environment")]
public sealed class CodexDoctorContractTests
{
    [Fact]
    public void ResolverReportsAbsentAndUnsupportedWindowsInstallations()
    {
        var empty = TestPaths.Unique("planforge-codex-empty");
        var unsupported = TestPaths.Unique("planforge-codex-unsupported");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(unsupported);
        File.WriteAllText(Path.Combine(unsupported, "codex.ps1"), "exit 0");
        try
        {
            Assert.Equal("absent", CodexExecutableResolver.Resolve(empty, windows: true).Status);
            var result = CodexExecutableResolver.Resolve(unsupported, windows: true);
            Assert.Equal("unusable", result.Status);
            Assert.EndsWith("codex.ps1", result.FoundExecutable, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("unsupported", result.FoundLaunchKind);
            Assert.Contains("codex.ps1", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
            Directory.Delete(unsupported, recursive: true);
        }
    }

    [Fact]
    public void ResolverSupportsNpmShimAndHonorsPathOrder()
    {
        var first = TestPaths.Unique("planforge codex first");
        var second = TestPaths.Unique("planforge codex second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var shim = Path.Combine(first, "codex.cmd");
        File.WriteAllText(shim, "@echo off\r\n");
        File.WriteAllText(Path.Combine(second, "codex.exe"), string.Empty);
        try
        {
            var result = CodexExecutableResolver.Resolve($"{first};{second}", windows: true);
            Assert.Equal("ready", result.Status);
            Assert.Equal(Path.GetFullPath(shim), result.Launch!.Executable);
            Assert.Equal("cmd-shim", result.Launch.LaunchKind);
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public void ResolverPrefersNativeExecutableWithinOnePathEntry()
    {
        var directory = TestPaths.Unique("planforge-codex-native");
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "codex.exe");
        File.WriteAllText(executable, string.Empty);
        File.WriteAllText(Path.Combine(directory, "codex.cmd"), "@echo off\r\n");
        try
        {
            var result = CodexExecutableResolver.Resolve(directory, windows: true);
            Assert.Equal(Path.GetFullPath(executable), result.Launch!.Executable);
            Assert.Equal("native", result.Launch.LaunchKind);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DoctorProbeInitializesAuthenticatesAndReadsEveryCatalogPage()
    {
        using var client = Client(
            Response(1, "{}"),
            Response(2, """{"account":{"type":"chatgpt"}}"""),
            Response(3, """{"data":[{"id":"gpt-a","supportedReasoningEfforts":[{"reasoningEffort":"high"}]}],"nextCursor":"next"}"""),
            Response(4, """{"data":[{"id":"gpt-b","supportedReasoningEfforts":[{"reasoningEffort":"medium"}]}],"nextCursor":null}"""));
        var launch = new CodexLaunch("C:/tools/codex.cmd", "cmd-shim");

        var result = CodexCapabilities.Probe(
            () => new CodexExecutableResolution("ready", launch, null),
            _ => client);

        Assert.Equal("ready", result.Status);
        Assert.Equal(launch.Executable, result.Executable);
        Assert.Equal(["gpt-a", "gpt-b"], result.Models.Select(model => model.Id));
        Assert.Null(result.ErrorCode);
    }

    [Theory]
    [InlineData("absent", null, null)]
    [InlineData("unusable", "process", "unsupported launcher")]
    public void DoctorProbeKeepsMissingOrBrokenCodexNonThrowing(string status, string? errorCode, string? error)
    {
        var resolution = status == "absent"
            ? new CodexExecutableResolution("absent", null, null)
            : new CodexExecutableResolution("unusable", null, error);

        var result = CodexCapabilities.Probe(() => resolution, _ => throw new InvalidOperationException("must not launch"));

        Assert.Equal(status, result.Status);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.Empty(result.Models);
    }

    [Fact]
    public void DoctorProbeReturnsStableProcessAuthAndProtocolFailures()
    {
        var launch = new CodexLaunch("C:/tools/codex.exe", "native");
        CodexExecutableResolution Resolve() => new("ready", launch, null);
        var process = CodexCapabilities.Probe(Resolve, _ => throw new CliFailure("process", "start failed", 3));
        using var authClient = Client(Response(1, "{}"), Response(2, """{"account":{"type":"amazonBedrock"}}"""));
        var auth = CodexCapabilities.Probe(Resolve, _ => authClient);
        using var protocolClient = Client(Response(1, "{}"), Response(2, """{"account":{"type":"chatgpt"}}"""), Response(3, "{}"));
        var protocol = CodexCapabilities.Probe(Resolve, _ => protocolClient);

        Assert.Equal("process", process.ErrorCode);
        Assert.Equal("auth", auth.ErrorCode);
        Assert.Equal("protocol", protocol.ErrorCode);
        Assert.All(new[] { process, auth, protocol }, result =>
        {
            Assert.Equal("unusable", result.Status);
            Assert.Empty(result.Models);
        });
    }

    [Fact]
    public void WindowsCmdShimCarriesJsonLinesOverRedirectedStandardIo()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = TestPaths.Unique("planforge codex shim io");
        Directory.CreateDirectory(directory);
        var shim = Path.Combine(directory, "codex.cmd");
        var server = Path.Combine(directory, "server.ps1");
        File.WriteAllText(shim, "@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0server.ps1\"\r\n");
        File.WriteAllText(server, """
$null = [Console]::In.ReadLine()
[Console]::Out.WriteLine('{"id":1,"result":{}}')
$null = [Console]::In.ReadLine()
$null = [Console]::In.ReadLine()
[Console]::Out.WriteLine('{"id":2,"result":{"account":{"type":"chatgpt"}}}')
$null = [Console]::In.ReadLine()
[Console]::Out.WriteLine('{"id":3,"result":{"data":[{"id":"gpt-shim","supportedReasoningEfforts":[{"reasoningEffort":"high"}]}],"nextCursor":null}}')
""");
        try
        {
            using var client = AppServerClient.Start(new CodexLaunch(shim, "cmd-shim"));
            client.Initialize();
            client.ValidateOpenAiAccount();
            Assert.Equal("gpt-shim", client.ListModels().Single().Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CodexReadinessSerializesAsPartOfDoctorData()
    {
        var codex = new CodexReadinessData("unusable", "C:/tools/codex.cmd", "cmd-shim", "process", "failed", []);
        var doctor = new DoctorData("C:/repo", new ToolCheckData(true, "git", null), false,
                                    new RoslynReadinessData("not-applicable", false, null, null), Claude: null, Codex: codex);

        var json = JsonSerializer.Serialize(doctor, ForgeJsonContext.Default.DoctorData);

        Assert.Contains("\"status\":\"unusable\"", json, StringComparison.Ordinal);
        Assert.Contains("\"launchKind\":\"cmd-shim\"", json, StringComparison.Ordinal);
        Assert.Contains("\"errorCode\":\"process\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ClaudeDoctorReportsCodexWithoutCreatingForgeArtifacts()
    {
        var workspace = TestPaths.Unique("planforge-claude-doctor-codex");
        Directory.CreateDirectory(workspace);
        Assert.Equal(0, ProcessExecution.Run("git", ["-C", workspace, "init"]).ExitCode);
        try
        {
            var result = ForgeWorkflow.Doctor(
                workspace,
                HostKind.Claude,
                _ => null,
                () => new ToolCheckData(true, "2.1.232", null),
                () => new CodexReadinessData("absent", null, null, null, null, []));

            Assert.Equal("absent", result.Codex!.Status);
            Assert.False(Directory.Exists(Path.Combine(workspace, ".forge")));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void ClaudeInstructionsRequireProviderFirstDoctorGate()
    {
        var pluginRoot = FindPluginRoot();
        var skill = File.ReadAllText(Path.Combine(pluginRoot, "skills", "forge", "SKILL.md"));
        var selection = File.ReadAllText(Path.Combine(pluginRoot, "skills", "forge", "references", "claude-model-selection.md"));
        var workflow = File.ReadAllText(Path.Combine(pluginRoot, "skills", "forge", "references", "claude-workflow.md"));

        Assert.Contains("sole allowed Codex discovery boundary", skill, StringComparison.Ordinal);
        Assert.Contains("continue without Codex or stop and repair it", selection, StringComparison.Ordinal);
        Assert.Contains("ask reviewer and builder independently whether to use Anthropic or", selection, StringComparison.Ordinal);
        Assert.Contains("no model-selection attempt consumed", workflow, StringComparison.Ordinal);
    }

    private static AppServerClient Client(params string[] responses)
        => new(new StringReader(string.Join(Environment.NewLine, responses)), new StringWriter());

    private static string Response(int id, string result) => $$"""{"id":{{id}},"result":{{result}}}""";

    private static string FindPluginRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, ".codex-plugin", "plugin.json")) &&
                Directory.Exists(Path.Combine(current.FullName, "skills", "forge"))) return current.FullName;
        }
        throw new InvalidOperationException("could not locate the Plan Forge Flow plugin root");
    }
}
