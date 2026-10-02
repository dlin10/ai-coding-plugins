using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlanForge.Diagnostics;
using PlanForge.Infrastructure;

namespace PlanForge.Vendors;

internal sealed record WorkerTelemetryContext(string Path,
                                              RunLog Log,
                                              string Act,
                                              int? Round = null,
                                              int? TaskNumber = null,
                                              int? TaskCount = null);

/// <param name="SessionTotal">
/// What the Vendor reported as its session's running total on a resumed attempt, for the counters it
/// reports that way — codex's tokens, claude's cost. The attempt's own counters are the growth since
/// the session's previous report; this keeps the report itself for the attempt after.
/// </param>
internal sealed record WorkerUsage(long? InputTokens = null,
                                   long? CacheReadTokens = null,
                                   long? CacheCreationTokens = null,
                                   long? OutputTokens = null,
                                   long? ReasoningTokens = null,
                                   IReadOnlyList<string>? MalformedFields = null,
                                   decimal? CostUsd = null,
                                   WorkerUsage? SessionTotal = null);

/// <summary>One call to a Vendor session, which may launch more than one process.</summary>
internal sealed class VendorTurn(RoleSpec role, Selection selection, string vendor)
{
    private readonly string _turnId = Guid.NewGuid().ToString("D");
    private int _attempts;

    internal string TurnId => _turnId;

    public VendorAttempt Start(long promptBytes, string? resumeToken, TimeProvider? clock = null) =>
        new(role.Telemetry, vendor, role.Role, selection, _turnId, ++_attempts, promptBytes, resumeToken, clock);

    public static long PromptBytes(params string[] fragments)
    {
        long total = 0;
        foreach (var fragment in fragments) total += Encoding.UTF8.GetByteCount(fragment);
        return total;
    }
}

/// <summary>
/// Captures one process launch. The default outcome is failure, so even a launch exception reaches
/// telemetry; callers only name the other terminal states they actually observe.
/// </summary>
/// <param name="context">The run's telemetry destination and act identity, when enabled.</param>
/// <param name="vendor">The vendor identifier.</param>
/// <param name="role">The worker's role.</param>
/// <param name="selection">The requested model, effort and speed.</param>
/// <param name="turnId">The session call shared by any schema retries.</param>
/// <param name="attempt">The process attempt number within that call.</param>
/// <param name="promptBytes">The UTF-8 size of the prompt sent to this attempt.</param>
/// <param name="resumeToken">The session being resumed, or null for a fresh session.</param>
/// <param name="clock">The timing source; defaults to the host's clock.</param>
internal sealed class VendorAttempt(WorkerTelemetryContext? context,
                                    string vendor,
                                    VendorRole role,
                                    Selection selection,
                                    string turnId,
                                    int attempt,
                                    long promptBytes,
                                    string? resumeToken,
                                    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _startedAt = (clock ?? TimeProvider.System).GetTimestamp();
    private long _exitedAt = -1;
    private string _at = FormatLocalTimestamp((clock ?? TimeProvider.System).GetLocalNow());
    private readonly HashSet<string> _openCalls = new(StringComparer.Ordinal);
    private readonly List<(long Start, long End)> _toolIntervals = [];
    private long _toolsStartedAt;
    private bool _sawTools;
    private bool _incompleteTools;
    private string _outcome = "failed";
    private bool _finished;

    public void ProcessStarted()
    {
        _startedAt = _clock.GetTimestamp();
        _at = FormatLocalTimestamp(_clock.GetLocalNow());
    }

    public void ProcessExited() => Interlocked.CompareExchange(ref _exitedAt, _clock.GetTimestamp(), -1);

    public void ToolStarted(string? callId)
    {
        _sawTools = true;
        if (string.IsNullOrEmpty(callId) || !_openCalls.Add(callId))
        {
            _incompleteTools = true;
            return;
        }

        if (_openCalls.Count == 1) _toolsStartedAt = _clock.GetTimestamp();
    }

    public void ToolCompleted(string? callId)
    {
        _sawTools = true;
        if (string.IsNullOrEmpty(callId) || !_openCalls.Remove(callId))
        {
            _incompleteTools = true;
            return;
        }

        if (_openCalls.Count == 0) _toolIntervals.Add((_toolsStartedAt, _clock.GetTimestamp()));
    }

    public void Succeeded() => _outcome = "succeeded";

    public void InvalidOutput() => _outcome = "invalid_output";

    public void Failed() => _outcome = "failed";

    public void Cancelled()
    {
        // A cancellation observed after accepting the structured result does not rewrite success.
        if (_outcome is not "succeeded") _outcome = "cancelled";
    }

    /// <summary>Appends the attempt's timing and provider counters once.</summary>
    /// <param name="usage">The provider counters the attempt's terminal event reported, already its own.</param>
    /// <param name="reportedSessionId">The session id the process reported, when it did.</param>
    /// <param name="servedFastState">The speed the vendor says it served, where it says: claude's <c>fast_mode_state</c>.</param>
    public void Finish(WorkerUsage usage, string? reportedSessionId, string? servedFastState = null) =>
        Finish(_ => usage, reportedSessionId, servedFastState);

    /// <summary>Appends the attempt's timing and counters derived from the session's previous report.</summary>
    /// <param name="usage">
    /// The attempt's own usage, given what its session's latest recorded attempt reported — null for a
    /// fresh session. A Vendor that reports a running total for its session subtracts that report.
    /// </param>
    /// <param name="reportedSessionId">The session id the process reported, when it did.</param>
    /// <param name="servedFastState">The speed the vendor says it served, where it says: claude's <c>fast_mode_state</c>.</param>
    public void Finish(Func<WorkerUsage?, WorkerUsage> usage, string? reportedSessionId, string? servedFastState = null)
    {
        if (_finished) return;
        _finished = true;

        if (context is null) return;

        var exitedAt = Volatile.Read(ref _exitedAt);
        if (exitedAt < 0) exitedAt = _clock.GetTimestamp();
        var wallDuration = _clock.GetElapsedTime(_startedAt, exitedAt);
        TimeSpan? toolDuration = null;
        if (_sawTools && !_incompleteTools && _openCalls.Count == 0)
        {
            toolDuration = TimeSpan.Zero;
            foreach (var interval in _toolIntervals)
            {
                var start = Math.Clamp(interval.Start, _startedAt, exitedAt);
                var end = Math.Clamp(interval.End, start, exitedAt);
                toolDuration += _clock.GetElapsedTime(start, end);
            }
        }

        // Quantize before subtracting so the three displayed durations still add up to the second.
        wallDuration = TimeSpan.FromSeconds((long)wallDuration.TotalSeconds);
        if (toolDuration is { } tools) toolDuration = TimeSpan.FromSeconds((long)tools.TotalSeconds);

        var resumed = resumeToken is { Length: > 0 };
        var sessionId = reportedSessionId is { Length: > 0 }
            ? reportedSessionId
            : resumed ? resumeToken : null;

        // The session object lives for one call, so what the session reported before can only come
        // from the file, read under the same gate as the append.
        WorkerTelemetryFile.Append(context, turnId, attempt, earlier =>
        {
            var own = usage(resumed ? WorkerTelemetryFile.LastReport(earlier, vendor, resumeToken!) : null);

            return new WorkerUsageRecord(
                _at,
                "vendor.usage",
                context.Act,
                context.Round,
                context.TaskNumber,
                context.TaskCount,
                vendor,
                role.ToString().ToLowerInvariant(),
                selection.Model,
                selection.Effort,
                selection.Fast,
                servedFastState,
                resumed ? "resumed" : "fresh",
                sessionId,
                turnId,
                attempt,
                _outcome,
                FormatDuration(wallDuration - (toolDuration ?? TimeSpan.Zero)),
                FormatDuration(wallDuration),
                toolDuration is { } measuredTools ? FormatDuration(measuredTools) : null,
                promptBytes,
                own.InputTokens,
                own.CacheReadTokens,
                own.CacheCreationTokens,
                own.OutputTokens,
                own.ReasoningTokens,
                own.CostUsd,
                own.SessionTotal,
                own.MalformedFields is { Count: > 0 } ? own.MalformedFields : null);
        });
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        var totalSeconds = Math.Max(0, (long)duration.TotalSeconds);
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        return string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{minutes:00}:{seconds:00}");
    }

    internal static string FormatLocalTimestamp(DateTimeOffset at) =>
        at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

internal sealed record WorkerUsageRecord(string At,
                                         string Event,
                                         string Act,
                                         int? Round,
                                         int? TaskNumber,
                                         int? TaskCount,
                                         string Vendor,
                                         string Role,
                                         string Model,
                                         string? Effort,
                                         bool Fast,
                                         string? FastModeState,
                                         string SessionMode,
                                         string? SessionId,
                                         string TurnId,
                                         int Attempt,
                                         string Outcome,
                                         string Duration,
                                         string WallDuration,
                                         [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ToolDuration,
                                         long PromptBytes,
                                         long? InputTokens,
                                         long? CacheReadTokens,
                                         long? CacheCreationTokens,
                                         long? OutputTokens,
                                         long? ReasoningTokens,
                                         decimal? CostUsd,
                                         WorkerUsage? SessionTotal,
                                         IReadOnlyList<string>? MalformedUsageFields);

internal static class WorkerTelemetryFile
{
    private static readonly ConcurrentDictionary<string, object> Writers = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="record">Builds the record from the ones already in the file.</param>
    public static void Append(WorkerTelemetryContext context,
                              string turnId,
                              int attempt,
                              Func<IReadOnlyList<WorkerUsageRecord>, WorkerUsageRecord> record)
    {
        try
        {
            var path = Path.GetFullPath(context.Path);
            lock (Writers.GetOrAdd(path, _ => new object()))
            {
                var records = File.Exists(path)
                    ? JsonSerializer.Deserialize(AtomicFile.Read(path), TelemetryJson.Default.ListWorkerUsageRecord)
                      ?? throw new JsonException("telemetry root must be a JSON array")
                    : [];
                if (records.Any(item => item is null))
                    throw new JsonException("telemetry array entries must be objects");

                records.Add(record(records));
                AtomicFile.Write(path, JsonSerializer.Serialize(records, TelemetryJson.Default.ListWorkerUsageRecord));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                      or NotSupportedException or JsonException)
        {
            context.Log.Write("error", "telemetry", "telemetry.write.failed",
                ("turnId", turnId),
                ("attempt", attempt.ToString(CultureInfo.InvariantCulture)),
                ("error", error.GetType().Name));
        }
    }

    /// <summary>
    /// What a session's latest recorded attempts reported, counter by counter: the running total a
    /// record kept, else its own counter — which is the report itself for a fresh attempt and for a
    /// Vendor that reports per attempt.
    /// </summary>
    internal static WorkerUsage LastReport(IEnumerable<WorkerUsageRecord> records, string vendor, string sessionId)
    {
        var report = new WorkerUsage();
        foreach (var record in records.Where(record => record.Vendor == vendor && record.SessionId == sessionId))
        {
            var total = record.SessionTotal;
            report = new WorkerUsage(total?.InputTokens ?? record.InputTokens ?? report.InputTokens,
                                     total?.CacheReadTokens ?? record.CacheReadTokens ?? report.CacheReadTokens,
                                     total?.CacheCreationTokens ?? record.CacheCreationTokens ?? report.CacheCreationTokens,
                                     total?.OutputTokens ?? record.OutputTokens ?? report.OutputTokens,
                                     total?.ReasoningTokens ?? record.ReasoningTokens ?? report.ReasoningTokens,
                                     CostUsd: total?.CostUsd ?? record.CostUsd ?? report.CostUsd);
        }

        return report;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true,
                             PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<WorkerUsageRecord>))]
internal sealed partial class TelemetryJson : JsonSerializerContext;
