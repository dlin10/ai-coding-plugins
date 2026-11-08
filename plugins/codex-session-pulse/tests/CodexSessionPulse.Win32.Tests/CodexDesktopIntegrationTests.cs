using System.Diagnostics;
using CodexSessionPulse.Core;
using Xunit;
using Xunit.Abstractions;

namespace CodexSessionPulse.Win32.Tests;

/// <summary>Runs only when <c>CODEX_SESSION_PULSE_INTEGRATION=1</c>: needs Windows and a running Codex Desktop window.</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    /// <summary>The variable that opts in.</summary>
    public const string Variable = "CODEX_SESSION_PULSE_INTEGRATION";

    /// <summary>Skips the test unless the variable is <c>1</c>.</summary>
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"Set {Variable}=1 with Codex Desktop open to run.";
        }
    }
}

/// <summary>The detector against the real Codex Desktop window on this machine.</summary>
/// <param name="output">Receives the measurements.</param>
public sealed class CodexDesktopIntegrationTests(ITestOutputHelper output)
{
    /// <summary>The default rule finds the Codex Desktop window(s) and only windows of the OpenAI.Codex package.</summary>
    [IntegrationFact]
    public void DefaultRule_FindsCodexDesktopWindows()
    {
        IReadOnlyList<WindowCandidate> all = WindowEnumerator.EnumerateTopLevel();
        IReadOnlyList<WindowCandidate> codex = WindowEnumerator.EnumerateCodexWindows(WindowDetectorOptions.Default);
        IReadOnlyList<WindowCandidate> byNameOnly = WindowEnumerator.EnumerateCodexWindows(WindowDetectorOptions.Default with { ExecutablePathFilters = [] });

        output.WriteLine($"visible unowned top-level windows: {all.Count}; Codex by default rule: {codex.Count}; by process name only: {byNameOnly.Count}");

        Assert.NotEmpty(codex);
        Assert.All(codex, window => Assert.Contains(@"\WindowsApps\OpenAI.Codex_", window.ExecutablePath!, StringComparison.OrdinalIgnoreCase));
        Assert.Single(codex.Select(window => window.ProcessId).Distinct());
    }

    /// <summary>The anchor attaches to a window with geometry, and an enumeration is cheap enough for the 300 ms poll.</summary>
    [IntegrationFact]
    public async Task Anchor_ReportsGeometryOfTheCodexWindow()
    {
        CodexWindowAnchor anchor = new(WindowDetectorOptions.Default, new SynchronizationContext());
        anchor.Start();
        AnchorSnapshot snapshot = anchor.Current;
        await anchor.DisposeAsync();

        Stopwatch stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < 100; i++)
        {
            WindowEnumerator.EnumerateCodexWindows(WindowDetectorOptions.Default);
        }

        output.WriteLine($"state {snapshot.State}, windows {snapshot.CodexWindows}, bounds {snapshot.Bounds}, work area {snapshot.WorkArea}, " +
                         $"dpi {snapshot.DpiScale}; one enumeration {stopwatch.Elapsed.TotalMilliseconds / 100:F2} ms");

        Assert.NotEqual(AnchorState.NotFound, snapshot.State);
        Assert.True(snapshot.IsAttached);
        Assert.NotNull(snapshot.Bounds);
        Assert.NotNull(snapshot.WorkArea);
        Assert.NotEqual(0, snapshot.Hwnd);
    }
}
