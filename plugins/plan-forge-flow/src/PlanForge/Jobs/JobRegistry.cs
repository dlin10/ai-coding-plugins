using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Diagnostics;
using PlanForge.Infrastructure;
using PlanForge.Run;

namespace PlanForge.Jobs;

internal enum JobState
{
    Running,
    Completed,
    Failed
}

internal sealed record JobRecord(
    string Id,
    string RunPath,
    string Act,
    JobState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? ResultPayload,
    string? Error,
    DateTimeOffset? LastActivityAt = null,
    string? LastEvent = null)
{
    public string JobId => Id;
}

/// <summary>What a start did: the job it started, or the oldest of the jobs that kept it from starting.</summary>
/// <param name="JobId">The started job, or the oldest running one.</param>
/// <param name="Started">False when the Run's running jobs kept this one from starting.</param>
/// <param name="Record">The job <paramref name="JobId"/> names, as it stands.</param>
/// <param name="ActiveJobIds">Every job that kept this one from starting, oldest first; null when it started.</param>
internal sealed record JobStartResult(string JobId, bool Started, JobRecord Record, IReadOnlyList<string>? ActiveJobIds = null)
{
    public string Id => JobId;
}

internal sealed class JobRegistry
{
    private static readonly TimeSpan CloseBound = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, List<Entry>> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, Entry>> _finishing =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, JobRecord>> _terminal =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _closed;
    private Task? _closeTask;

    /// <summary>
    /// Starts <paramref name="work"/> as a job of the Run, unless the Run's running jobs keep it from
    /// starting: a shared job runs beside other shared jobs, and any other job runs alone.
    /// </summary>
    /// <param name="runPath">The Run's folder.</param>
    /// <param name="act">The act the job runs.</param>
    /// <param name="work">The act, handed the job's cancellation.</param>
    /// <param name="shared">Whether the job may run beside the Run's other shared jobs — a fresh Scout
    /// question; see docs/adr/0044.</param>
    public JobStartResult Start(string runPath, string act, Func<CancellationToken, Task<string>> work, bool shared = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(act);
        ArgumentNullException.ThrowIfNull(work);

        var canonicalRunPath = Canonicalize(runPath);
        var entry = new Entry(
            new JobRecord(
                Guid.NewGuid().ToString("N")[..16],
                canonicalRunPath,
                act,
                JobState.Running,
                DateTimeOffset.UtcNow,
                null,
                null,
                null),
            work,
            shared);

        lock (_gate)
        {
            if (_closed)
            {
                throw new InvalidOperationException("job registry is closed");
            }

            if (_active.TryGetValue(canonicalRunPath, out var running))
            {
                if (!shared || running.Any(job => !job.Shared))
                {
                    var record = running[0].Snapshot();
                    return new JobStartResult(record.Id, false, record, [.. running.Select(job => job.Record.Id)]);
                }
            }
            else
            {
                running = [];
                _active.Add(canonicalRunPath, running);
            }

            running.Add(entry);
        }

        var started = entry.Snapshot();
        _ = Task.Run(() => ExecuteAsync(canonicalRunPath, entry));
        return new JobStartResult(started.Id, true, started);
    }

    /// <summary>The Run's running jobs, oldest first.</summary>
    /// <param name="runPath">The Run's folder.</param>
    public IReadOnlyList<JobRecord> Running(string runPath)
    {
        var canonicalRunPath = Canonicalize(runPath);

        lock (_gate)
        {
            return _active.TryGetValue(canonicalRunPath, out var active) ? [.. active.Select(job => job.Snapshot())] : [];
        }
    }

    public JobRecord? Get(string runPath, string jobId)
    {
        var canonicalRunPath = Canonicalize(runPath);

        lock (_gate)
        {
            if (Find(canonicalRunPath, jobId) is { } active)
            {
                return active.Snapshot();
            }

            if (_terminal.TryGetValue(canonicalRunPath, out var terminal) &&
                terminal.TryGetValue(jobId, out var completed))
            {
                return completed;
            }
        }

        return Read(canonicalRunPath, jobId);
    }

    public JobRecord? Cancel(string runPath, string jobId)
    {
        var canonicalRunPath = Canonicalize(runPath);
        Entry? active = null;
        JobRecord? snapshot = null;

        lock (_gate)
        {
            if (Find(canonicalRunPath, jobId) is { } candidate)
            {
                candidate.MarkCancellation("job was cancelled");
                active = candidate;
                snapshot = candidate.Snapshot();
            }
            else if (_terminal.TryGetValue(canonicalRunPath, out var terminal) &&
                     terminal.TryGetValue(jobId, out var completed))
            {
                return completed;
            }
        }

        if (active is null) return Read(canonicalRunPath, jobId);

        active.Cancel();
        return snapshot;
    }

    public Task CloseAsync()
    {
        lock (_gate)
        {
            if (_closeTask is not null)
            {
                return _closeTask;
            }

            _closed = true;
            var entries = _active.Values.SelectMany(running => running).ToList();
            _closeTask = CloseCoreAsync(entries);
            return _closeTask;
        }
    }

    public async Task<JobRecord?> WaitAsync(
        string runPath,
        string jobId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var canonicalRunPath = Canonicalize(runPath);
        Entry? entry;

        lock (_gate)
        {
            if ((entry = Find(canonicalRunPath, jobId)) is not null)
            {
                // Keep the active entry below.
            }
            else if (_finishing.TryGetValue(canonicalRunPath, out var finishing) &&
                     finishing.TryGetValue(jobId, out entry))
            {
                // Keep waiting for the terminal persistence and signal.
            }
            else
            {
                return Get(canonicalRunPath, jobId);
            }
        }

        var timeoutTask = Task.Delay(timeout, cancellationToken);
        var completedTask = await Task.WhenAny(entry.Signal.Task, timeoutTask).ConfigureAwait(false);
        if (completedTask == entry.Signal.Task)
        {
            return await entry.Signal.Task.ConfigureAwait(false);
        }

        await timeoutTask.ConfigureAwait(false);
        return Get(canonicalRunPath, jobId);
    }

    private async Task ExecuteAsync(string runPath, Entry entry)
    {
        using var activity = WorkerActivity.Use(entry.Activity);
        JobRecord terminal;
        try
        {
            var result = await entry.Work(entry.Cancellation.Token).ConfigureAwait(false);
            terminal = entry.Snapshot() with
            {
                State = JobState.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                ResultPayload = result,
                Error = null
            };
        }
        catch (OperationCanceledException) when (entry.CancellationRequested)
        {
            terminal = entry.Snapshot() with
            {
                State = JobState.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ResultPayload = null,
                Error = entry.CancellationError
            };
        }
        catch (Exception exception)
        {
            terminal = entry.Snapshot() with
            {
                State = JobState.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ResultPayload = null,
                Error = exception.Message
            };
        }

        lock (_gate)
        {
            if (!IsActive(runPath, entry))
            {
                return;
            }

            if (entry.CancellationRequested)
            {
                terminal = entry.Snapshot() with
                {
                    State = JobState.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ResultPayload = null,
                    Error = entry.CancellationError
                };
            }

            entry.Record = terminal;
            Release(runPath, entry);
            RememberTerminal(terminal);
            RememberFinishing(runPath, entry);
        }

        PersistAndSignal(runPath, entry, terminal);
    }

    private async Task CloseCoreAsync(List<Entry> entries)
    {
        foreach (var entry in entries)
        {
            entry.MarkCancellation("job was cancelled at shutdown");
            entry.Cancel();
        }

        if (entries.Count == 0)
        {
            return;
        }

        var signals = new Task<JobRecord>[entries.Count];
        for (var index = 0; index < entries.Count; index++)
        {
            signals[index] = entries[index].Signal.Task;
        }

        var completion = Task.WhenAll(signals);
        if (await Task.WhenAny(completion, Task.Delay(CloseBound)).ConfigureAwait(false) == completion)
        {
            return;
        }

        var claimed = new List<(Entry Entry, JobRecord Record)>();
        lock (_gate)
        {
            foreach (var entry in entries)
            {
                if (!IsActive(entry.Record.RunPath, entry))
                {
                    continue;
                }

                var terminal = entry.Snapshot() with
                {
                    State = JobState.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ResultPayload = null,
                    Error = "abandoned at shutdown"
                };
                entry.Record = terminal;
                Release(entry.Record.RunPath, entry);
                RememberTerminal(terminal);
                RememberFinishing(entry.Record.RunPath, entry);
                claimed.Add((entry, terminal));
            }
        }

        foreach (var (entry, terminal) in claimed)
        {
            PersistAndSignal(terminal.RunPath, entry, terminal);
        }
    }

    private static string Canonicalize(string runPath) => Path.GetFullPath(runPath);

    private Entry? Find(string runPath, string jobId) =>
        _active.TryGetValue(runPath, out var running) ? running.Find(job => job.Record.Id == jobId) : null;

    private bool IsActive(string runPath, Entry entry) =>
        _active.TryGetValue(runPath, out var running) && running.Contains(entry);

    private void Release(string runPath, Entry entry)
    {
        var running = _active[runPath];
        running.Remove(entry);
        if (running.Count == 0)
            _active.Remove(runPath);
    }

    private static void Persist(JobRecord record)
    {
        if (record.State == JobState.Running)
            throw new InvalidOperationException("running jobs are not persisted");

        var path = RunDirectory.FromPath(record.RunPath).JobFilePath(record.Id);
        var json = new JsonObject
        {
            ["id"] = record.Id,
            ["runPath"] = record.RunPath,
            ["act"] = record.Act,
            ["state"] = record.State.ToString(),
            ["startedAt"] = record.StartedAt,
            ["completedAt"] = record.CompletedAt,
            ["resultPayload"] = record.ResultPayload,
            ["error"] = record.Error,
            ["lastActivityAt"] = record.LastActivityAt,
            ["lastEvent"] = record.LastEvent
        }.ToJsonString();

        AtomicFile.Write(path, json);
    }

    private static void PersistBestEffort(JobRecord record)
    {
        try
        {
            Persist(record);
        }
        catch (Exception error)
        {
            try
            {
                RunDirectory.FromPath(record.RunPath).Log.Write(
                    "warn", "server", "job.persistence",
                    ("jobId", record.Id), ("error", error.Message));
            }
            catch (Exception)
            {
            }
        }
    }

    private static JobRecord? Read(string runPath, string jobId)
    {
        var path = RunDirectory.FromPath(runPath).JobFilePath(jobId);
        if (!File.Exists(path))
        {
            return null;
        }

        var record = ReadFile(path);
        return record is null || record.State == JobState.Running ? null : record;
    }

    private void RememberTerminal(JobRecord record)
    {
        if (!_terminal.TryGetValue(record.RunPath, out var records))
        {
            records = new Dictionary<string, JobRecord>(StringComparer.OrdinalIgnoreCase);
            _terminal.Add(record.RunPath, records);
        }

        records[record.Id] = record;
    }

    private void RememberFinishing(string runPath, Entry entry)
    {
        if (!_finishing.TryGetValue(runPath, out var entries))
        {
            entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            _finishing.Add(runPath, entries);
        }

        entries[entry.Record.Id] = entry;
    }

    private void PersistAndSignal(string runPath, Entry entry, JobRecord terminal)
    {
        PersistBestEffort(terminal);
        entry.Signal.TrySetResult(terminal);

        lock (_gate)
        {
            if (_finishing.TryGetValue(runPath, out var entries) &&
                entries.TryGetValue(entry.Record.Id, out var current) && ReferenceEquals(current, entry))
            {
                entries.Remove(entry.Record.Id);
                if (entries.Count == 0)
                    _finishing.Remove(runPath);
            }
        }
    }

    private static JobRecord? ReadFile(string path)
    {
        var json = JsonNode.Parse(AtomicFile.Read(path))?.AsObject();
        if (json is null)
        {
            return null;
        }

        return new JobRecord(
            json["id"]?.GetValue<string>() ?? throw new JsonException("Job id is missing."),
            json["runPath"]?.GetValue<string>() ?? throw new JsonException("Job run path is missing."),
            json["act"]?.GetValue<string>() ?? throw new JsonException("Job act is missing."),
            Enum.Parse<JobState>(json["state"]?.GetValue<string>() ?? throw new JsonException("Job state is missing.")),
            json["startedAt"]?.GetValue<DateTimeOffset>() ?? throw new JsonException("Job start time is missing."),
            json["completedAt"]?.GetValue<DateTimeOffset?>(),
            json["resultPayload"]?.GetValue<string>(),
            json["error"]?.GetValue<string>(),
            json["lastActivityAt"]?.GetValue<DateTimeOffset?>(),
            json["lastEvent"]?.GetValue<string>());
    }

    private sealed class Entry
    {
        private readonly object _cancellationGate = new();
        private bool _cancellationRequested;
        private string? _cancellationError;

        public Entry(JobRecord record, Func<CancellationToken, Task<string>> work, bool shared)
        {
            Record = record;
            Work = work;
            Shared = shared;
        }

        public JobRecord Record { get; set; }
        public Func<CancellationToken, Task<string>> Work { get; }
        public bool Shared { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public WorkerActivity Activity { get; } = new();
        public TaskCompletionSource<JobRecord> Signal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationRequested
        {
            get
            {
                lock (_cancellationGate) return _cancellationRequested;
            }
        }

        public string CancellationError
        {
            get
            {
                lock (_cancellationGate) return _cancellationError ?? "job was cancelled";
            }
        }

        public void MarkCancellation(string error)
        {
            lock (_cancellationGate)
            {
                if (_cancellationRequested) return;
                _cancellationRequested = true;
                _cancellationError = error;
            }
        }

        public void Cancel() => Cancellation.Cancel();

        public JobRecord Snapshot()
        {
            var activity = Activity.Snapshot();
            return Record with
            {
                LastActivityAt = activity.LastActivityAt,
                LastEvent = activity.LastEvent
            };
        }
    }
}
