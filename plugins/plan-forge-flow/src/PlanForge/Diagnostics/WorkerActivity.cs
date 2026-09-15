namespace PlanForge.Diagnostics;

internal sealed record WorkerActivitySnapshot(DateTimeOffset? LastActivityAt, string? LastEvent);

/// <summary>The in-memory liveness of the worker running on the current async flow.</summary>
internal sealed class WorkerActivity
{
    private const int MAX_EVENT_LENGTH = 200;

    private static readonly AsyncLocal<WorkerActivity?> Ambient = new();

    private readonly object _gate = new();
    private DateTimeOffset? _lastActivityAt;
    private string? _lastEvent;

    public static IDisposable Use(WorkerActivity activity)
    {
        var previous = Ambient.Value;
        Ambient.Value = activity;
        return new Scope(previous);
    }

    public static void RecordOutput()
    {
        var activity = Ambient.Value;
        if (activity is null) return;

        lock (activity._gate)
        {
            activity._lastActivityAt = DateTimeOffset.UtcNow;
        }
    }

    public static void RecordEvent(string description)
    {
        var activity = Ambient.Value;
        if (activity is null) return;

        var normalized = string.Join(' ', description.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (normalized.Length > MAX_EVENT_LENGTH) normalized = normalized[..MAX_EVENT_LENGTH];

        lock (activity._gate)
        {
            activity._lastEvent = normalized;
        }
    }

    public WorkerActivitySnapshot Snapshot()
    {
        lock (_gate)
        {
            return new WorkerActivitySnapshot(_lastActivityAt, _lastEvent);
        }
    }

    private sealed class Scope(WorkerActivity? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
