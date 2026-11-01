using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PlanForge.Advisors;
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

internal sealed record OrchestratorUsageRecord(string At,
                                               string Event,
                                               string Phase,
                                               int? Round,
                                               int? TaskNumber,
                                               int? TaskCount,
                                               string? Host,
                                               string? HostSessionId,
                                               string? UsageSource,
                                               string Duration,
                                               string WallDuration,
                                               string ToolDuration,
                                               long? InputTokens,
                                               long? CacheReadTokens,
                                               long? CacheCreationTokens,
                                               long? OutputTokens,
                                               long? ReasoningTokens,
                                               double? CostUsd,
                                               [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool UsageIncomplete,
                                               string? UsageMissing);

/// <summary>
/// The one owner of <c>telemetry.json</c>: every record kind is written here, under one
/// gate per file, at the place its <c>at</c> — its opening — gives it, which is after the elements
/// that opened no later. Elements already in the file are kept as they are, in the order they are in,
/// whatever their <c>event</c>.
/// </summary>
internal static class WorkerTelemetryFile
{
    private const string VENDOR_EVENT = "vendor.usage";

    private static readonly ConcurrentDictionary<string, object> Writers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Writes a Worker record, built from the <c>vendor.usage</c> records already in the file, after the
    /// elements that opened no later than it.
    /// </summary>
    /// <param name="context">The run's telemetry destination and act identity.</param>
    /// <param name="turnId">The session call the record belongs to, named if the write fails.</param>
    /// <param name="attempt">The process attempt within that call, named if the write fails.</param>
    /// <param name="record">Builds the record from the ones already in the file.</param>
    public static void Append(WorkerTelemetryContext context,
                              string turnId,
                              int attempt,
                              Func<IReadOnlyList<WorkerUsageRecord>, WorkerUsageRecord> record) =>
        Append(context.Path, context.Log,
               elements => record(VendorRecords(elements)), added => added.At,
               TelemetryJson.Default.WorkerUsageRecord,
               ("turnId", turnId), ("attempt", attempt.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Writes an Orchestrator record, which a phase closes long after it opened, after the elements
    /// that opened no later than it.
    /// </summary>
    /// <param name="path">The run's <c>telemetry.json</c>.</param>
    /// <param name="log">The run's log, which names a failed write.</param>
    /// <param name="record">The record to write.</param>
    public static void Append(string path, RunLog log, OrchestratorUsageRecord record) =>
        Append(path, log, _ => record, added => added.At,
               TelemetryJson.Default.OrchestratorUsageRecord, ("event", record.Event));

    /// <summary>Writes one Advisor call record beside the existing telemetry kinds.</summary>
    /// <param name="path">The Run's telemetry file.</param>
    /// <param name="log">The diagnostic log for a failed write.</param>
    /// <param name="record">The content-free call accounting.</param>
    public static void Append(string path, RunLog log, AdvisorUsageRecord record) =>
        Append(path, log, _ => record, added => added.At,
               TelemetryJson.Default.AdvisorUsageRecord, ("event", record.Event));

    /// <summary>
    /// What the Run's Evidence map calls have cost: each <c>advisor.usage</c> record's own <c>costUsd</c>, or, for a
    /// record written before it carried one, its input, cached and cache-written tokens at the current Advisor's prices. A file that does not read
    /// is logged and counts nothing, as a failed write does.
    /// </summary>
    /// <param name="path">The Run's telemetry file.</param>
    /// <param name="log">The diagnostic log for a failed read.</param>
    /// <param name="advisor">The Advisor whose price stands in for a record without a cost.</param>
    public static decimal AdvisorSpentUsd(string path, RunLog log, IAdvisor advisor)
    {
        if (!File.Exists(path)) return 0m;
        try
        {
            using var document = Parse(AtomicFile.Read(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("telemetry root must be a JSON array");
            var spent = 0m;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("event", out var name)
                    || !name.ValueEquals(AdvisorUsageRecord.EVENT)) continue;
                if (element.TryGetProperty("costUsd", out var cost) && cost.ValueKind == JsonValueKind.Number)
                    spent += cost.GetDecimal();
                else if (AdvisorPrice.Usd(advisor, Tokens(element, "inputTokens"), Tokens(element, "cachedInputTokens"),
                                          Tokens(element, "cacheWriteTokens")) is { } usd)
                    spent += Math.Round(usd, 4);
            }
            return spent;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                                      or FormatException or InvalidOperationException)
        {
            log.Write("error", "telemetry", "telemetry.read.failed", ("error", error.GetType().Name));
            return 0m;
        }
    }

    /// <summary>Reads a record's token count, null when the field is absent or not a number.</summary>
    /// <param name="record">The advisor.usage record.</param>
    /// <param name="name">The token field's name.</param>
    private static long? Tokens(JsonElement record, string name) =>
        record.TryGetProperty(name, out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt64() : null;

    /// <summary>
    /// Where a record opened at <paramref name="at"/> goes: after the last element that opened no
    /// later, found by stepping back from the end. An element whose <c>at</c> is not text is a
    /// boundary, and the walk does not pass it, so a file this server did not write is never reordered.
    /// </summary>
    /// <param name="elements">The file's elements, in the order they are in.</param>
    /// <param name="at">The record's opening, in the format of <see cref="VendorAttempt.FormatLocalTimestamp"/>.</param>
    private static int InsertionIndex(JsonElement[] elements, string at)
    {
        var index = elements.Length;
        while (index > 0
               && elements[index - 1].TryGetProperty("at", out var opened)
               && opened.ValueKind == JsonValueKind.String
               && string.CompareOrdinal(opened.GetString(), at) > 0)
            index--;
        return index;
    }

    private static void Append<T>(string path,
                                  RunLog log,
                                  Func<IReadOnlyList<JsonElement>, T> record,
                                  Func<T, string> openedAt,
                                  JsonTypeInfo<T> type,
                                  params (string Name, string? Value)[] identity)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            lock (Writers.GetOrAdd(fullPath, _ => new object()))
            {
                using var document = File.Exists(fullPath) ? Parse(AtomicFile.Read(fullPath)) : null;
                JsonElement[] elements = [];
                if (document is not null)
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Array)
                        throw new JsonException("telemetry root must be a JSON array");
                    elements = [.. document.RootElement.EnumerateArray()];
                    if (elements.Any(item => item.ValueKind != JsonValueKind.Object))
                        throw new JsonException("telemetry array entries must be objects");
                }

                var added = record(elements);
                var position = InsertionIndex(elements, openedAt(added));

                // The serializer's own options decide the layout and encoder, so a record is written
                // as it always was, and an element already in the file is rewritten with its values.
                var options = TelemetryJson.Default.Options;
                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
                       {
                           Indented = options.WriteIndented,
                           IndentCharacter = options.IndentCharacter,
                           IndentSize = options.IndentSize,
                           NewLine = options.NewLine,
                           Encoder = options.Encoder,
                       }))
                {
                    writer.WriteStartArray();
                    for (var index = 0; index <= elements.Length; index++)
                    {
                        if (index == position) JsonSerializer.Serialize(writer, added, type);
                        if (index < elements.Length) elements[index].WriteTo(writer);
                    }

                    writer.WriteEndArray();
                }

                AtomicFile.Write(fullPath, Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                      or NotSupportedException or JsonException)
        {
            log.Write("error", "telemetry", "telemetry.write.failed",
                      [.. identity, ("error", error.GetType().Name)]);
        }
    }

    /// <summary>
    /// The reader throws an internal subclass; the log names the failure as the serializer always did.
    /// </summary>
    /// <param name="json">The file's text.</param>
    private static JsonDocument Parse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException error) { throw new JsonException(error.Message, error); }
    }

    private static List<WorkerUsageRecord> VendorRecords(IEnumerable<JsonElement> elements) =>
        [.. elements.Where(element => element.TryGetProperty("event", out var name)
                                      && name.ValueKind == JsonValueKind.String
                                      && name.ValueEquals(VENDOR_EVENT))
                    .Select(element => element.Deserialize(TelemetryJson.Default.WorkerUsageRecord)
                                       ?? throw new JsonException("telemetry array entries must be objects"))];

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
[JsonSerializable(typeof(WorkerUsageRecord))]
[JsonSerializable(typeof(OrchestratorUsageRecord))]
[JsonSerializable(typeof(AdvisorUsageRecord))]
internal sealed partial class TelemetryJson : JsonSerializerContext;
