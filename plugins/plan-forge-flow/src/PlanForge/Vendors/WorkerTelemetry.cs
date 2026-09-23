using System.Collections.Concurrent;
using System.Diagnostics;
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

internal sealed record WorkerUsage(long? InputTokens = null,
                                   long? CacheReadTokens = null,
                                   long? CacheCreationTokens = null,
                                   long? OutputTokens = null,
                                   long? ReasoningTokens = null,
                                   IReadOnlyList<string>? MalformedFields = null,
                                   decimal? CostUsd = null);

/// <summary>One call to a Vendor session, which may launch more than one process.</summary>
internal sealed class VendorTurn(RoleSpec role, Selection selection, string vendor)
{
    private readonly string _turnId = Guid.NewGuid().ToString("D");
    private int _attempts;

    internal string TurnId => _turnId;

    public VendorAttempt Start(long promptBytes, string? resumeToken) =>
        new(role.Telemetry, vendor, role.Role, selection, _turnId, ++_attempts, promptBytes, resumeToken);

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
internal sealed class VendorAttempt(WorkerTelemetryContext? context,
                                    string vendor,
                                    VendorRole role,
                                    Selection selection,
                                    string turnId,
                                    int attempt,
                                    long promptBytes,
                                    string? resumeToken)
{
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private string _outcome = "failed";
    private TimeSpan? _duration;
    private string? _at;
    private bool _finished;

    public void Succeeded() => Terminal("succeeded");

    public void InvalidOutput() => Terminal("invalid_output");

    public void Failed() => Terminal("failed");

    public void Cancelled()
    {
        // A cancellation observed after accepting the structured result does not rewrite success.
        if (_outcome is not "succeeded") Terminal("cancelled");
    }

    public void Finish(WorkerUsage usage, string? reportedSessionId)
    {
        if (_finished) return;
        _finished = true;

        if (_duration is null) CaptureTerminal();
        if (context is null) return;

        var resumed = resumeToken is { Length: > 0 };
        var sessionId = reportedSessionId is { Length: > 0 }
            ? reportedSessionId
            : resumed ? resumeToken : null;

        var record = new WorkerUsageRecord(
            _at!,
            "vendor.usage",
            context.Act,
            context.Round,
            context.TaskNumber,
            context.TaskCount,
            vendor,
            role.ToString().ToLowerInvariant(),
            selection.Model,
            selection.Effort,
            resumed ? "resumed" : "fresh",
            sessionId,
            turnId,
            attempt,
            _outcome,
            FormatDuration(_duration!.Value),
            promptBytes,
            usage.InputTokens,
            usage.CacheReadTokens,
            usage.CacheCreationTokens,
            usage.OutputTokens,
            usage.ReasoningTokens,
            usage.CostUsd,
            usage.MalformedFields is { Count: > 0 } ? usage.MalformedFields : null);

        WorkerTelemetryFile.Append(context, record);
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
        at.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    private void Terminal(string outcome)
    {
        _outcome = outcome;
        CaptureTerminal();
    }

    private void CaptureTerminal()
    {
        _duration = Stopwatch.GetElapsedTime(_startedAt);
        _at = FormatLocalTimestamp(DateTimeOffset.Now);
    }
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
                                         string SessionMode,
                                         string? SessionId,
                                         string TurnId,
                                         int Attempt,
                                         string Outcome,
                                         string Duration,
                                         long PromptBytes,
                                         long? InputTokens,
                                         long? CacheReadTokens,
                                         long? CacheCreationTokens,
                                         long? OutputTokens,
                                         long? ReasoningTokens,
                                         decimal? CostUsd,
                                         IReadOnlyList<string>? MalformedUsageFields);

internal static class WorkerTelemetryFile
{
    private static readonly ConcurrentDictionary<string, object> Writers = new(StringComparer.OrdinalIgnoreCase);

    public static void Append(WorkerTelemetryContext context, WorkerUsageRecord record)
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

                records.Add(record);
                AtomicFile.Write(path, JsonSerializer.Serialize(records, TelemetryJson.Default.ListWorkerUsageRecord));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                      or NotSupportedException or JsonException)
        {
            context.Log.Write("error", "telemetry", "telemetry.write.failed",
                ("turnId", record.TurnId),
                ("attempt", record.Attempt.ToString(CultureInfo.InvariantCulture)),
                ("error", error.GetType().Name));
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true,
                             PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<WorkerUsageRecord>))]
internal sealed partial class TelemetryJson : JsonSerializerContext;
