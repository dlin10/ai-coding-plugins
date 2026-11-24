namespace ConcurrencyHunter;

/// <summary>The cancellation and check observer of the work running in this execution context.</summary>
internal static class WorkCancellation
{
    private static readonly AsyncLocal<Frame?> Current = new();

    /// <summary>The effective token of the current work, or no token outside work.</summary>
    internal static CancellationToken Token => Current.Value?.Token ?? CancellationToken.None;

    /// <summary>Enters work linked to the enclosing work and restores it on disposal.</summary>
    /// <param name="token">The work's own token.</param>
    /// <param name="checkObserved">Observes each check before cancellation is judged; inherited when omitted.</param>
    internal static IDisposable Enter(CancellationToken token, Action? checkObserved = null) =>
        new Frame(token, checkObserved, outside: false);

    /// <summary>Suspends enclosing work, including its token and observer, until disposal.</summary>
    internal static IDisposable Outside() => new Frame(CancellationToken.None, null, outside: true);

    /// <summary>Takes back, until disposal, the work the nearest <see cref="Outside"/> suspended, its token and observer, for work done
    /// inside the suspension on that work's behalf alone, such as the diagnostic census within pairing (issue 492); with no suspension
    /// around it, the current work goes on as it is.</summary>
    internal static IDisposable Resumed()
    {
        var frame = Current.Value;
        // bounded: one step for each frame entered since the suspension, which work nests a handful deep.
        while (frame is not null && !frame.IsOutside)
            frame = frame.Outer;
        if (frame is null)
            return new Frame(CancellationToken.None, null, outside: false);
        return new Frame(frame.Outer?.Token ?? CancellationToken.None, frame.Outer?.Observed, outside: false);
    }

    /// <summary>Reports a check, then throws cancellation with the current work's effective token.</summary>
    internal static void Check()
    {
        var frame = Current.Value;
        frame?.Observed?.Invoke();
        frame?.Token.ThrowIfCancellationRequested();
    }

    private sealed class Frame : IDisposable
    {
        private readonly Frame? _outer;
        private readonly CancellationTokenSource? _linked;
        private bool _disposed;

        internal CancellationToken Token { get; }
        internal Action? Observed { get; }

        /// <summary>Whether this frame suspends the work around it (<see cref="Outside"/>).</summary>
        internal bool IsOutside { get; }

        /// <summary>The frame this one was entered in, or null for the first.</summary>
        internal Frame? Outer => _outer;

        internal Frame(CancellationToken token, Action? observed, bool outside)
        {
            _outer = Current.Value;
            IsOutside = outside;
            var enclosing = outside ? CancellationToken.None : _outer?.Token ?? CancellationToken.None;
            if (token.CanBeCanceled && enclosing.CanBeCanceled)
                _linked = CancellationTokenSource.CreateLinkedTokenSource(token, enclosing);
            Token = _linked?.Token ?? (token.CanBeCanceled ? token : enclosing);
            Observed = outside ? null : observed ?? _outer?.Observed;
            Current.Value = this;
        }

        // bounded: disposes the single linked token source after restoring the enclosing frame.
        public void Dispose()
        {
            if (_disposed)
                return;
            Current.Value = _outer;
            _linked?.Dispose();
            _disposed = true;
        }
    }
}
