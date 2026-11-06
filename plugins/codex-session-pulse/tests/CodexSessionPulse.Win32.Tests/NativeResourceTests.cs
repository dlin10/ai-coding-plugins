using System.Diagnostics;
using CodexSessionPulse.Core;
using Xunit;

namespace CodexSessionPulse.Win32.Tests;

/// <summary>Native handles and hooks are released, and the extended-style arithmetic keeps unrelated bits.</summary>
public sealed class NativeResourceTests
{
    /// <summary>Enumerating the windows opens and closes one process handle per process; repeated runs do not leak.</summary>
    [Fact]
    public void EnumerateTopLevel_DoesNotLeakProcessHandles()
    {
        WindowEnumerator.EnumerateTopLevel();
        int before = HandleCount();

        for (int i = 0; i < 200; i++)
        {
            WindowEnumerator.EnumerateTopLevel();
        }

        Assert.InRange(HandleCount() - before, int.MinValue, 20);
    }

    /// <summary>A hook installs, unhooks on Dispose, tolerates a second Dispose, and repeated cycles do not leak.</summary>
    [Fact]
    public void WinEventHook_UnhooksOnDisposeWithoutLeaks()
    {
        using (WinEventHook first = new(0x0003, 0x0003, 0, (_, _) => { }))
        {
            Assert.True(first.IsInstalled);
            first.Dispose();
            Assert.False(first.IsInstalled);
        }

        int before = HandleCount();

        for (int i = 0; i < 200; i++)
        {
            using WinEventHook hook = new(0x8001, 0x8003, 0, (_, _) => { });
            GC.Collect();
        }

        Assert.InRange(HandleCount() - before, int.MinValue, 20);
    }

    /// <summary>With no matching window the anchor reports NotFound with its diagnostic code, never an error, and disposes cleanly.</summary>
    [Fact]
    public async Task Anchor_WithNoMatchingWindow_IsNotFound()
    {
        ManualContext ui = new();
        CodexWindowAnchor anchor = new(new WindowDetectorOptions(["NoSuchProcess-7f1c"], null, []), ui);
        List<AnchorSnapshot> seen = [];

        using (anchor.Snapshots.Subscribe(new Observer(seen.Add)))
        {
            anchor.Start();
            ui.RunPending();

            Assert.Equal(AnchorState.NotFound, anchor.Current.State);
            Assert.False(anchor.Current.IsAttached);
            Assert.Equal(0, anchor.Current.CodexWindows);
            Assert.Equal(CodexWindowAnchor.WindowNotFound, anchor.Current.Diagnostics);
            Assert.All(seen, snapshot => Assert.Equal(AnchorState.NotFound, snapshot.State));

            AnchorDiagnostics diagnostics = anchor.GetDiagnostics();
            Assert.True(diagnostics.GlobalHooksInstalled);
            Assert.False(diagnostics.PollingActive);
        }

        await anchor.DisposeAsync();
        await anchor.DisposeAsync();

        Assert.False(anchor.GetDiagnostics().GlobalHooksInstalled);
    }

    /// <summary>The overlay styles add no-activate and tool-window and keep every unrelated bit.</summary>
    [Fact]
    public void WithOverlayStyles_AddsNoActivateAndToolWindowOnly()
    {
        const long layered = 0x00080000;
        const long noActivate = 0x08000000;
        const long toolWindow = 0x00000080;
        const long transparent = 0x00000020;
        const long topmost = 0x00000008;

        long style = OverlayWindowInterop.WithOverlayStyles(layered | transparent | topmost);

        Assert.Equal(layered | noActivate | toolWindow, style);
    }

    private static int HandleCount()
    {
        using Process self = Process.GetCurrentProcess();

        return self.HandleCount;
    }

    /// <summary>A synchronization context the test pumps by hand.</summary>
    private sealed class ManualContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

        /// <inheritdoc />
        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        /// <summary>Runs the queued callbacks, including the ones they queue.</summary>
        public void RunPending()
        {
            while (_queue.TryDequeue(out (SendOrPostCallback Callback, object? State) item))
            {
                item.Callback(item.State);
            }
        }
    }

    /// <summary>Forwards values to an action.</summary>
    /// <param name="onNext">Receives each value.</param>
    private sealed class Observer(Action<AnchorSnapshot> onNext) : IObserver<AnchorSnapshot>
    {
        /// <inheritdoc />
        public void OnCompleted()
        {
        }

        /// <inheritdoc />
        public void OnError(Exception error)
        {
        }

        /// <inheritdoc />
        public void OnNext(AnchorSnapshot value) => onNext(value);
    }
}
