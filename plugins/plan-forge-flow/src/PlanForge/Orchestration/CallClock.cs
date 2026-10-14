namespace PlanForge.Orchestration;

/// <summary>
/// The time this server process spends inside Plan Forge's own tool calls, per Run, in memory: the
/// part of an Orchestrator phase in which the Orchestrator was waiting on the server rather than
/// working. Nothing here touches a file; the busy time is read when this process closes a phase.
/// </summary>
/// <remarks>
/// A Run is named by its <c>workspaceRoot</c> and <c>runId</c> together, the identity a call carries
/// before its Run is opened — and the one that keeps two Runs of one <c>runId</c> in different
/// workspaces apart. Overlapping calls count once, and a call still running counts up to whatever
/// instant the reader asks about.
/// </remarks>
/// <param name="time">The clock every call's arrival and end is read from.</param>
internal sealed class CallClock(TimeProvider time)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RunCalls> _runs = new(StringComparer.OrdinalIgnoreCase);

    public TimeProvider Time => time;

    /// <summary>Enters a call that arrived at <paramref name="arrival"/>; it counts once it names a Run.</summary>
    /// <param name="arrival">When the server received the call.</param>
    /// <param name="workspaceRoot">The call's <c>workspaceRoot</c> argument, if it has one.</param>
    /// <param name="runId">The call's <c>runId</c> argument, if it has one.</param>
    public Ticket Enter(DateTimeOffset arrival, string? workspaceRoot, string? runId)
    {
        var ticket = new Ticket(this, arrival);
        ticket.Attach(workspaceRoot, runId);
        return ticket;
    }

    /// <summary>
    /// The union of the Run's calls inside <c>[from, to)</c>, calls still running included up to
    /// <paramref name="to"/>.
    /// </summary>
    /// <param name="workspaceRoot">The Run's workspace root.</param>
    /// <param name="runId">The Run's id.</param>
    /// <param name="from">The window's start, included.</param>
    /// <param name="to">The window's end, excluded.</param>
    public TimeSpan Busy(string workspaceRoot, string runId, DateTimeOffset from, DateTimeOffset to)
    {
        if (Key(workspaceRoot, runId) is not { } key || to <= from) return TimeSpan.Zero;

        List<(DateTimeOffset Start, DateTimeOffset End)> spans;
        lock (_gate)
        {
            if (!_runs.TryGetValue(key, out var calls)) return TimeSpan.Zero;
            spans = [.. calls.Ended, .. calls.Running.Select(ticket => (ticket.Arrival, to))];
        }

        var busy = TimeSpan.Zero;
        var reached = from;
        foreach (var (start, end) in spans.Select(span => (Start: Max(span.Start, from), End: Min(span.End, to)))
                                          .Where(span => span.End > span.Start)
                                          .OrderBy(span => span.Start))
        {
            var counted = Max(start, reached);
            if (end > counted) busy += end - counted;
            reached = Max(reached, end);
        }

        return busy;
    }

    /// <summary>Drops the Run's ended calls that lie wholly before <paramref name="before"/>: no phase can reach them again.</summary>
    /// <param name="workspaceRoot">The Run's workspace root.</param>
    /// <param name="runId">The Run's id.</param>
    /// <param name="before">The boundary of the phase just closed.</param>
    public void Forget(string workspaceRoot, string runId, DateTimeOffset before)
    {
        if (Key(workspaceRoot, runId) is not { } key) return;
        lock (_gate)
            if (_runs.TryGetValue(key, out var calls))
                calls.Ended.RemoveAll(span => span.End <= before);
    }

    /// <summary>A Run's identity, or null for a call that does not name one.</summary>
    /// <param name="workspaceRoot">The <c>workspaceRoot</c> argument.</param>
    /// <param name="runId">The <c>runId</c> argument.</param>
    internal static string? Key(string? workspaceRoot, string? runId)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(runId)) return null;

        var root = workspaceRoot;
        try
        {
            if (Path.IsPathRooted(root)) root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        return root + "\n" + runId;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private sealed class RunCalls
    {
        public List<(DateTimeOffset Start, DateTimeOffset End)> Ended { get; } = [];

        public HashSet<Ticket> Running { get; } = [];
    }

    /// <summary>One call's stay in the clock, from its arrival to <see cref="Leave"/>.</summary>
    /// <param name="clock">The clock the call is counted in.</param>
    /// <param name="arrival">When the server received the call.</param>
    internal sealed class Ticket(CallClock clock, DateTimeOffset arrival)
    {
        private string? _key;
        private bool _left;

        public DateTimeOffset Arrival => arrival;

        /// <summary>Names the call's Run, if it has not named one yet; <c>forge.begin</c> names the Run it creates.</summary>
        /// <param name="workspaceRoot">The Run's workspace root.</param>
        /// <param name="runId">The Run's id.</param>
        public void Attach(string? workspaceRoot, string? runId)
        {
            if (Key(workspaceRoot, runId) is not { } key) return;
            lock (clock._gate)
            {
                if (_key is not null || _left) return;
                _key = key;
                if (!clock._runs.TryGetValue(key, out var calls)) clock._runs[key] = calls = new RunCalls();
                calls.Running.Add(this);
            }
        }

        /// <summary>Ends the call, however it ended; a second call does nothing.</summary>
        public void Leave()
        {
            var end = clock.Time.GetUtcNow();
            lock (clock._gate)
            {
                if (_left) return;
                _left = true;
                if (_key is null || !clock._runs.TryGetValue(_key, out var calls)) return;
                calls.Running.Remove(this);
                if (end > arrival) calls.Ended.Add((arrival, end));
            }
        }
    }
}
