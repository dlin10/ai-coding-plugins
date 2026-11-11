using System.Globalization;
using System.Text;
using CodexSessionPulse.Core;
using CodexSessionPulse.Infrastructure.Desktop;
using CodexSessionPulse.Infrastructure.Tests.Rollouts;
using Xunit;

namespace CodexSessionPulse.Infrastructure.Tests.Desktop;

/// <summary>
/// Codex Desktop's route line and the reader that follows one process's log (docs/compatibility.md §6.3).
/// The lines copy the measured shape; ids are made up.
/// </summary>
public sealed class DesktopLogTailTests : IDisposable
{
    private const string ThreadA = "019a0000-0000-7000-8000-0000000000a1";
    private const string ThreadB = "019a0000-0000-7000-8000-0000000000b1";
    private const string Session = "6a2c251c-0000-4000-8000-000000000001";
    private const int Pid = 11244;
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 9, 57, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "csp-desktop-log-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new(T0);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>The three kinds of page, the time in UTC, and the id lowercased; the line's conversationId is not taken.</summary>
    /// <param name="path">The ownerRoutePath value.</param>
    /// <param name="kind">The expected page.</param>
    /// <param name="threadId">The expected thread id.</param>
    [Theory]
    [InlineData("/", CodexRouteKind.NewChat, null)]
    [InlineData("/local/" + ThreadA, CodexRouteKind.LocalThread, ThreadA)]
    [InlineData("/local/019A0000-0000-7000-8000-0000000000A1", CodexRouteKind.LocalThread, ThreadA)]
    [InlineData("/local/" + ThreadA + "?hostId=durable", CodexRouteKind.Other, null)]
    [InlineData("/g/g-p-123/c/68f0aa00-0000-8000-8000-000000000001", CodexRouteKind.Other, null)]
    [InlineData("/remote/task_e_123", CodexRouteKind.Other, null)]
    public void TryParse_RouteLine_GivesThePage(string path, CodexRouteKind kind, string? threadId)
    {
        byte[] line = Encoding.UTF8.GetBytes(RouteLine(T0, path, conversationId: "client-new-thread:" + ThreadB));

        bool parsed = DesktopLogLine.TryParse(line, out CodexRoute route);

        Assert.True(DesktopLogLine.IsMarker(line));
        Assert.True(parsed);
        Assert.Equal(new CodexRoute(kind, threadId, T0), route);
    }

    /// <summary>Other lines are not the marker, and a marker line without a time or a route does not parse.</summary>
    [Fact]
    public void TryParse_OtherOrBrokenLines_AreRejected()
    {
        byte[] other = Encoding.UTF8.GetBytes("2026-11-10T09:57:00.000Z info [AppServerConnection] response_routed conversationId=" + ThreadA);
        byte[] noTime = Encoding.UTF8.GetBytes("info [electron-message-handler] IAB_LIFECYCLE received browser sidebar owner sync ownerRoutePath=/ windowId=2");
        byte[] noRoute = Encoding.UTF8.GetBytes("2026-11-10T09:57:00.000Z info [electron-message-handler] IAB_LIFECYCLE received browser sidebar owner sync windowId=2");

        Assert.False(DesktopLogLine.IsMarker(other));
        Assert.False(DesktopLogLine.TryParse(noTime, out _));
        Assert.False(DesktopLogLine.TryParse(noRoute, out _));
    }

    /// <summary>An MSIX install keeps its log in the package's LocalCache; one without a package in %LOCALAPPDATA%\Codex.</summary>
    [Fact]
    public void LogsRoot_WithAndWithoutPackage()
    {
        Assert.Equal(@"C:\L\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\Local\Codex\Logs",
                     DesktopLogTail.LogsRoot(@"C:\L", "OpenAI.Codex_2p2nqsd0c76g0"));
        Assert.Equal(@"C:\L\Codex\Logs", DesktopLogTail.LogsRoot(@"C:\L", null));
    }

    /// <summary>The first poll reads the whole file and keeps the last page, so an overlay started late knows it at once.</summary>
    [Fact]
    public void Poll_ExistingLog_TakesTheLastRoute()
    {
        string log = LogFile(Pid, T0.AddHours(-2));
        File.WriteAllText(log, RouteLine(T0.AddMinutes(-3), "/") + "\n" + Noise() + "\n" + RouteLine(T0.AddMinutes(-2), "/local/" + ThreadA) + "\n" + Noise() + "\n");
        using DesktopLogTail tail = new(LogsRoot, Pid, _clock);

        bool changed = tail.Poll();
        bool again = tail.Poll();

        Assert.True(changed);
        Assert.False(again);
        Assert.Equal(new CodexRoute(CodexRouteKind.LocalThread, ThreadA, T0.AddMinutes(-2)), tail.Route);
        Assert.Equal(2, tail.GetDiagnostics().Markers);
    }

    /// <summary>
    /// Codex keeps the log open and appends: a switch is a change, a repeat of the same page is not (Codex writes the line
    /// up to 8 times), and the first line of a page dates it.
    /// </summary>
    [Fact]
    public void Poll_AppendsToAnOpenLog_ReportsOnlyPageChanges()
    {
        string log = LogFile(Pid, T0.AddHours(-2));
        using FileStream writer = new(log, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
        Write(writer, RouteLine(T0, "/local/" + ThreadA) + "\n");
        using DesktopLogTail tail = new(LogsRoot, Pid, _clock);
        tail.Poll();

        Write(writer, RouteLine(T0.AddSeconds(10), "/local/" + ThreadB) + "\n" + RouteLine(T0.AddSeconds(10.1), "/local/" + ThreadB) + "\n");
        bool switched = tail.Poll();
        Write(writer, RouteLine(T0.AddSeconds(20), "/local/" + ThreadB) + "\n");
        bool repeated = tail.Poll();

        Assert.True(switched);
        Assert.False(repeated);
        Assert.Equal(new CodexRoute(CodexRouteKind.LocalThread, ThreadB, T0.AddSeconds(10)), tail.Route);
    }

    /// <summary>A line still being written waits for its end; a CRLF ending reads like LF.</summary>
    [Fact]
    public void Poll_PartialLine_IsReadWhenComplete()
    {
        string log = LogFile(Pid, T0);
        File.WriteAllText(log, string.Empty);
        using DesktopLogTail tail = new(LogsRoot, Pid, _clock);
        tail.Poll();
        string line = RouteLine(T0, "/");

        File.AppendAllText(log, line[..40]);
        bool partial = tail.Poll();
        File.AppendAllText(log, line[40..] + "\r\n");
        bool complete = tail.Poll();

        Assert.False(partial);
        Assert.True(complete);
        Assert.Equal(CodexRouteKind.NewChat, tail.Route?.Kind);
    }

    /// <summary>A marker line Codex changed shape for is counted, not guessed at, and the page stays as it was.</summary>
    [Fact]
    public void Poll_MalformedMarker_IsCountedAndIgnored()
    {
        string log = LogFile(Pid, T0);
        File.WriteAllText(log, RouteLine(T0, "/") + "\n" + "IAB_LIFECYCLE received browser sidebar owner sync route=/local/" + ThreadA + "\n");
        using DesktopLogTail tail = new(LogsRoot, Pid, _clock);

        tail.Poll();

        Assert.Equal(CodexRouteKind.NewChat, tail.Route?.Kind);
        Assert.Equal(1, tail.GetDiagnostics().Malformed);
    }

    /// <summary>
    /// Only the main (t0) file of the followed PID is read, not another process's nor the PID's later t1 file, and at UTC
    /// midnight the newer t0 file in the next day's folder takes over once the file search is due again.
    /// </summary>
    [Fact]
    public void Poll_OtherProcessAndMidnightRollover()
    {
        File.WriteAllText(LogFile(4242, T0.AddHours(-1)), RouteLine(T0, "/local/" + ThreadB) + "\n");
        File.WriteAllText(LogFile(Pid, T0.AddHours(-1)), RouteLine(T0, "/local/" + ThreadA) + "\n");
        File.WriteAllText(LogFile(Pid, T0.AddMinutes(-1)).Replace("-t0-", "-t1-", StringComparison.Ordinal),
                          "2026-11-10T09:56:00.000Z info [windows-native-app-icon] loaded\n");
        using DesktopLogTail tail = new(LogsRoot, Pid, _clock);
        tail.Poll();
        CodexRoute? before = tail.Route;

        DateTimeOffset nextDay = new(2026, 11, 11, 0, 0, 1, TimeSpan.Zero);
        File.WriteAllText(LogFile(Pid, nextDay), RouteLine(nextDay, "/") + "\n");
        _clock.UtcNow = nextDay.AddSeconds(DesktopLogTail.LocateInterval.TotalSeconds);
        bool changed = tail.Poll();

        Assert.Equal(ThreadA, before?.ThreadId);
        Assert.True(changed);
        Assert.Equal(CodexRouteKind.NewChat, tail.Route?.Kind);
    }

    /// <summary>No log yet (Codex just started) is no route and no error; the file is picked up once it appears.</summary>
    [Fact]
    public void Poll_NoLogYet_FindsItLater()
    {
        using DesktopLogTail tail = new(LogsRoot, Pid, _clock);
        bool empty = tail.Poll();
        DesktopLogDiagnostics before = tail.GetDiagnostics();

        File.WriteAllText(LogFile(Pid, T0), RouteLine(T0, "/") + "\n");
        bool found = tail.Poll();

        Assert.False(empty);
        Assert.False(before.FileFound);
        Assert.Null(before.LastError);
        Assert.True(found);
    }

    /// <summary>The watcher raises the page from its timer and forgets it, raising null, when it follows another process.</summary>
    [Fact]
    public async Task Watcher_RaisesThePage_AndForgetsItForAnotherProcess()
    {
        File.WriteAllText(LogFile(Pid, DateTimeOffset.UtcNow), RouteLine(DateTimeOffset.UtcNow, "/local/" + ThreadA) + "\n");
        using DesktopRouteWatcher watcher = new(SystemClock.Instance);
        List<CodexRoute?> raised = [];
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.RouteChanged += route =>
        {
            lock (raised)
            {
                raised.Add(route);
            }

            first.TrySetResult();
        };

        watcher.Follow(LogsRoot, Pid);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        watcher.Follow(LogsRoot, 4242);

        lock (raised)
        {
            Assert.Equal(ThreadA, raised[0]?.ThreadId);
            Assert.Null(raised[^1]);
        }

        Assert.Null(watcher.Current);
    }

    private string LogsRoot => Path.Combine(_root, "Logs");

    private static string RouteLine(DateTimeOffset atUtc, string path, string conversationId = "local:1") =>
        $"{atUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)} info [electron-message-handler] " +
        $"IAB_LIFECYCLE received browser sidebar owner sync browserTabId=tab-1 conversationId={conversationId} " +
        $"originWebContentsId=2 ownerRoutePath={path} windowId=2";

    private static string Noise() =>
        "2026-11-10T09:56:00.000Z info [host-app-server-manager] thread_stream_view_activity_changed active=true streamRole=owner";

    private static void Write(FileStream writer, string text)
    {
        writer.Write(Encoding.UTF8.GetBytes(text));
        writer.Flush();
    }

    private string LogFile(int processId, DateTimeOffset startedUtc)
    {
        DateTime date = startedUtc.UtcDateTime;
        string folder = Path.Combine(LogsRoot, date.ToString("yyyy", CultureInfo.InvariantCulture), date.ToString("MM", CultureInfo.InvariantCulture),
                                     date.ToString("dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);

        return Path.Combine(folder, $"codex-desktop-{Session}-{processId}-t0-i1-{date.ToString("HHmmss", CultureInfo.InvariantCulture)}-0.log");
    }
}
