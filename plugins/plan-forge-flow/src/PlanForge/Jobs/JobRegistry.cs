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
    string? Error)
{
    public string JobId => Id;
}

internal sealed record JobStartResult(string JobId, bool Started, JobRecord Record)
{
    public string Id => JobId;
}

internal sealed class JobRegistry
{
    private static readonly TimeSpan CloseBound = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, Entry>> _finishing =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, JobRecord>> _terminal =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _closed;
    private Task? _closeTask;

    public JobStartResult Start(string runPath, string act, Func<CancellationToken, Task<string>> work)
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
            work);

        lock (_gate)
        {
            if (_closed)
            {
                throw new InvalidOperationException("job registry is closed");
            }

            if (_active.TryGetValue(canonicalRunPath, out var current))
            {
                return new JobStartResult(current.Record.Id, false, current.Record);
            }

            _active.Add(canonicalRunPath, entry);
        }

        _ = Task.Run(() => ExecuteAsync(canonicalRunPath, entry));
        return new JobStartResult(entry.Record.Id, true, entry.Record);
    }

    public JobRecord? Get(string runPath)
    {
        var canonicalRunPath = Canonicalize(runPath);

        lock (_gate)
        {
            if (_active.TryGetValue(canonicalRunPath, out var active))
            {
                return active.Record;
            }

            if (TryGetTerminal(canonicalRunPath, out var terminal))
            {
                return terminal;
            }
        }

        return Read(canonicalRunPath);
    }

    public JobRecord? Get(string runPath, string jobId)
    {
        var canonicalRunPath = Canonicalize(runPath);

        lock (_gate)
        {
            if (_active.TryGetValue(canonicalRunPath, out var active))
            {
                if (active.Record.Id == jobId)
                {
                    return active.Record;
                }
            }

            if (_terminal.TryGetValue(canonicalRunPath, out var terminal) &&
                terminal.TryGetValue(jobId, out var completed))
            {
                return completed;
            }
        }

        return Read(canonicalRunPath, jobId);
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
            var entries = new List<Entry>(_active.Values);
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
            if (_active.TryGetValue(canonicalRunPath, out entry) && entry.Record.Id == jobId)
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
        JobRecord terminal;
        try
        {
            var result = await entry.Work(entry.Cancellation.Token).ConfigureAwait(false);
            terminal = entry.Record with
            {
                State = JobState.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                ResultPayload = result,
                Error = null
            };
        }
        catch (Exception exception)
        {
            terminal = entry.Record with
            {
                State = JobState.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ResultPayload = null,
                Error = exception.Message
            };
        }

        lock (_gate)
        {
            if (!_active.TryGetValue(runPath, out var current) || !ReferenceEquals(current, entry))
            {
                return;
            }

            entry.Record = terminal;
            _active.Remove(runPath);
            RememberTerminal(terminal);
            RememberFinishing(runPath, entry);
        }

        PersistAndSignal(runPath, entry, terminal);
    }

    private async Task CloseCoreAsync(List<Entry> entries)
    {
        foreach (var entry in entries)
        {
            entry.Cancellation.Cancel();
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
                if (!_active.TryGetValue(entry.Record.RunPath, out var current) || !ReferenceEquals(current, entry))
                {
                    continue;
                }

                var terminal = entry.Record with
                {
                    State = JobState.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ResultPayload = null,
                    Error = "abandoned at shutdown"
                };
                entry.Record = terminal;
                _active.Remove(entry.Record.RunPath);
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
            ["error"] = record.Error
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

    private static JobRecord? Read(string runPath)
    {
        return RunDirectory.FromPath(runPath).EnumerateJobFiles()
            .Select(ReadFile)
            .OfType<JobRecord>()
            .FirstOrDefault(record => record.State != JobState.Running);
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

    private bool TryGetTerminal(string runPath, out JobRecord? terminal)
    {
        if (_terminal.TryGetValue(runPath, out var records) && records.Count > 0)
        {
            terminal = records.Values.OrderByDescending(record => record.CompletedAt).First();
            return true;
        }

        terminal = null;
        return false;
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
            json["error"]?.GetValue<string>());
    }

    private sealed class Entry
    {
        public Entry(JobRecord record, Func<CancellationToken, Task<string>> work)
        {
            Record = record;
            Work = work;
        }

        public JobRecord Record { get; set; }
        public Func<CancellationToken, Task<string>> Work { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<JobRecord> Signal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
