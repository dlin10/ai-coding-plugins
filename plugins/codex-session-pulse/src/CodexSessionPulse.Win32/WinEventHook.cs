namespace CodexSessionPulse.Win32;

/// <summary>
/// One out-of-context <c>SetWinEventHook</c> range. The callback runs on the thread that created the hook, from its
/// message loop, so create it on the UI thread. Only whole-window events (<c>OBJID_WINDOW</c>, <c>CHILDID_SELF</c>) are passed on.
/// </summary>
internal sealed class WinEventHook : IDisposable
{
    // The native side holds only a function pointer to this delegate; the field keeps it alive until Dispose.
    private readonly NativeMethods.WinEventProc _callback;
    private readonly Action<uint, nint> _onEvent;
    private nint _hook;

    /// <summary>Installs the hook.</summary>
    /// <param name="eventMin">The first event of the range.</param>
    /// <param name="eventMax">The last event of the range.</param>
    /// <param name="processId">The process to listen to, or 0 for all.</param>
    /// <param name="onEvent">Receives the event type and the window.</param>
    public WinEventHook(uint eventMin, uint eventMax, uint processId, Action<uint, nint> onEvent)
    {
        _onEvent = onEvent;
        _callback = OnWinEvent;
        _hook = NativeMethods.SetWinEventHook(eventMin, eventMax, 0, _callback, processId, 0,
                                              NativeMethods.WinEventOutOfContext | NativeMethods.WinEventSkipOwnProcess);
    }

    /// <summary>Gets a value indicating whether the hook is installed.</summary>
    public bool IsInstalled => _hook != 0;

    /// <summary>Removes the hook; safe to call more than once.</summary>
    public void Dispose()
    {
        if (_hook != 0)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = 0;
        }
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTimeMs)
    {
        if (_hook != 0 && hwnd != 0 && idObject == NativeMethods.ObjIdWindow && idChild == NativeMethods.ChildIdSelf)
        {
            _onEvent(eventType, hwnd);
        }
    }
}
