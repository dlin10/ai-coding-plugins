using System.Globalization;
using System.Text.Json;
using CodexSessionPulse.Core;

namespace CodexSessionPulse.Infrastructure.Rollouts;

/// <summary>The result of one rollout line: at most one of the values is set.</summary>
/// <param name="Usage">A usage event to emit.</param>
/// <param name="Observation">The file's own thread, from its first <c>session_meta</c>.</param>
/// <param name="Diagnostic">A <see cref="DiagnosticCodes"/> value for a skipped line.</param>
/// <param name="Timing">A model request's output and duration, from a <c>token_usage_record</c>.</param>
internal readonly record struct ParsedLine(TokenUsageSnapshot? Usage, ThreadObservation? Observation, string? Diagnostic,
                                           RequestTiming? Timing = null);

/// <summary>
/// Turns the lines of one rollout file into usage events. It reads metadata only, never keeps or reports text.
/// </summary>
/// <remarks>
/// <para><b>Ownership</b> (compatibility.md §7.2): the first <c>session_meta</c> names the file's own thread. Its
/// boundary is <c>subagent_history_start_ordinal</c>, else <c>forked_from_ordinal_exclusive</c>, else
/// <c>history_base.end_ordinal_exclusive</c>. A <c>token_count</c> whose <c>ordinal</c> is below the boundary lies
/// in a copied parent prefix and is skipped (the parent's own rollout carries it); every other one belongs to the
/// own thread. A file without ordinals falls back to time: a record older than the own <c>session_meta</c> is
/// inherited. A later <c>session_meta</c> (the parent's copy) never takes ownership.</para>
/// <para><b>Usage</b>: only <c>info.last_token_usage</c>, never re-summed totals. <c>info: null</c>, a missing
/// counter, an invalid one, the all-zero record after a compaction, and an exact repeat of the previous
/// <c>token_count</c> (same last and total usage) are skipped with a diagnostic code. <c>token_usage_record</c>
/// gives no usage: its <c>token_count</c> follows 1–5 records later with the same numbers (§7.3).</para>
/// <para><b>Request time</b> (spec §6.4): a <c>token_usage_record</c> is written when an answer ends; Codex writes the
/// <c>token_count</c> after the answer's tool calls ran, just before it sends the next request, and
/// <c>task_started</c> before a turn's first request. So a request runs from the latest of those two to its
/// <c>token_usage_record</c>; tool time stays outside, the wait for the first token inside. A record with no start
/// since the previous record (the first one in the history window, a request after a compaction) is not timed.</para>
/// </remarks>
internal sealed class RolloutFileParser
{
    private static readonly byte[][] Markers =
    [
        "\"session_meta\""u8.ToArray(),
        "\"turn_context\""u8.ToArray(),
        "\"task_started\""u8.ToArray(),
        "\"token_count\""u8.ToArray(),
        "\"token_usage_record\""u8.ToArray(),
    ];

    private readonly string _path;
    private readonly string? _fileThreadId;
    private RolloutSchema _schema = RolloutSchema.For(null);
    private bool _hasOwnMeta;
    private string? _threadId;
    private string? _rootSessionId;
    private string? _parentThreadId;
    private string? _cliVersion;
    private ThreadKind _kind = ThreadKind.Unknown;
    private long? _boundaryOrdinal;
    private DateTimeOffset? _ownMetaAtUtc;
    private string? _model;
    private string? _turnId;
    private UsageKey? _previousUsage;
    private DateTimeOffset? _requestStartUtc;

    /// <summary>Creates a parser for one file.</summary>
    /// <param name="path">The rollout path; its name gives the thread id until the meta is read.</param>
    public RolloutFileParser(string path)
    {
        _path = path;
        _fileThreadId = RolloutFileName.TryGetThreadId(path, out string threadId) ? threadId : null;
    }

    /// <summary>Parses one complete line without its line terminator.</summary>
    /// <param name="line">The UTF-8 bytes of the line.</param>
    /// <param name="byteStart">The offset of the line in the file; the event sequence when the record has no ordinal.</param>
    /// <returns>What the line yields.</returns>
    public ParsedLine Parse(ReadOnlySpan<byte> line, long byteStart)
    {
        if (!HasMarker(line))
        {
            return default;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(line.ToArray());
        }
        catch (JsonException)
        {
            return new ParsedLine(null, null, DiagnosticCodes.ParseFailed);
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
             || !root.TryGetProperty("payload", out JsonElement payload)
             || payload.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            return (Text(root, "type"), Text(payload, "type")) switch
            {
                ("session_meta", _) => ReadMeta(root, payload),
                ("turn_context", _) => Remember(ref _model, Text(payload, "model")),
                ("event_msg", "task_started") => StartTurn(root, payload),
                ("event_msg", "token_count") => ReadUsage(root, payload, byteStart),
                ("token_usage_record", _) => ReadTiming(root, payload),
                _ => default,
            };
        }
    }

    private static bool HasMarker(ReadOnlySpan<byte> line)
    {
        foreach (byte[] marker in Markers)
        {
            if (line.IndexOf(marker) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static ParsedLine Remember(ref string? field, string? value)
    {
        field = value ?? field;
        return default;
    }

    private static ThreadKind KindOf(JsonElement payload)
    {
        bool subagentSource = payload.TryGetProperty("source", out JsonElement source)
                           && source.ValueKind == JsonValueKind.Object
                           && source.TryGetProperty("subagent", out _);

        if (subagentSource || Text(payload, "thread_source") == "subagent" || Text(payload, "parent_thread_id") is not null)
        {
            return ThreadKind.Subagent;
        }

        if (Text(payload, "forked_from_id") is not null)
        {
            return ThreadKind.Fork;
        }

        return Text(payload, "thread_source") == "user" || source.ValueKind == JsonValueKind.String
            ? ThreadKind.Main
            : ThreadKind.Unknown;
    }

    private static long? Boundary(JsonElement payload)
    {
        if (Number(payload, "subagent_history_start_ordinal") is { } subagentStart)
        {
            return subagentStart;
        }

        if (Number(payload, "forked_from_ordinal_exclusive") is { } forkedEnd)
        {
            return forkedEnd;
        }

        return payload.TryGetProperty("history_base", out JsonElement historyBase) && historyBase.ValueKind == JsonValueKind.Object
            ? Number(historyBase, "end_ordinal_exclusive")
            : null;
    }

    private static string? Text(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static long? Number(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long number)
            ? number
            : null;
    }

    private static DateTimeOffset? Timestamp(JsonElement root)
    {
        return Text(root, "timestamp") is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset at)
            ? at
            : null;
    }

    private ParsedLine ReadMeta(JsonElement root, JsonElement payload)
    {
        if (_hasOwnMeta)
        {
            return default;
        }

        _hasOwnMeta = true;
        _threadId = Text(payload, "id") ?? _fileThreadId;
        _rootSessionId = Text(payload, "session_id");
        _parentThreadId = Text(payload, "parent_thread_id") ?? Text(payload, "forked_from_id");
        _cliVersion = Text(payload, "cli_version");
        _kind = KindOf(payload);
        _boundaryOrdinal = Boundary(payload);
        _ownMetaAtUtc = Timestamp(root);
        _schema = RolloutSchema.For(_cliVersion);

        return _threadId is null || _ownMetaAtUtc is null
            ? default
            : new ParsedLine(null, new ThreadObservation(_threadId, _kind, _parentThreadId, _rootSessionId, _path,
                                                         _cliVersion, _ownMetaAtUtc.Value), null);
    }

    private ParsedLine StartTurn(JsonElement root, JsonElement payload)
    {
        if (!IsInherited(Number(root, "ordinal"), Timestamp(root)))
        {
            _requestStartUtc = Timestamp(root) ?? _requestStartUtc;
        }

        return Remember(ref _turnId, Text(payload, "turn_id"));
    }

    private ParsedLine ReadTiming(JsonElement root, JsonElement payload)
    {
        DateTimeOffset? at = Timestamp(root);
        DateTimeOffset? start = _requestStartUtc;
        _requestStartUtc = null;

        if (IsInherited(Number(root, "ordinal"), at)
            || start is null || at is null || at <= start
            || !payload.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object
            || Number(usage, _schema.OutputTokens) is not { } output || output <= 0
            || (_threadId ?? _fileThreadId) is not { } threadId)
        {
            return default;
        }

        return new ParsedLine(null, null, null, new RequestTiming(threadId, at.Value, output, at.Value - start.Value));
    }

    private ParsedLine ReadUsage(JsonElement root, JsonElement payload, long byteStart)
    {
        long? ordinal = Number(root, "ordinal");
        DateTimeOffset? at = Timestamp(root);

        if (IsInherited(ordinal, at))
        {
            return new ParsedLine(null, null, DiagnosticCodes.InheritedPrefix);
        }

        // Written after the answer's tool calls ran: the next request is sent from here.
        _requestStartUtc = at ?? _requestStartUtc;

        if (!payload.TryGetProperty("info", out JsonElement info))
        {
            return new ParsedLine(null, null, DiagnosticCodes.UsageIncomplete);
        }

        if (info.ValueKind == JsonValueKind.Null)
        {
            return new ParsedLine(null, null, DiagnosticCodes.InfoNull);
        }

        if (info.ValueKind != JsonValueKind.Object
         || !info.TryGetProperty(_schema.LastTokenUsage, out JsonElement last)
         || last.ValueKind != JsonValueKind.Object
         || Number(last, _schema.InputTokens) is not { } input
         || Number(last, _schema.CachedInputTokens) is not { } cached
         || Number(last, _schema.OutputTokens) is not { } output
         || at is null
         || (_threadId ?? _fileThreadId) is not { } threadId)
        {
            return new ParsedLine(null, null, DiagnosticCodes.UsageIncomplete);
        }

        long? write = Number(last, _schema.CacheWriteInputTokens);
        long? reasoning = Number(last, _schema.ReasoningOutputTokens);
        UsageKey key = new(input, cached, write, output, TotalOf(info));
        bool duplicate = _previousUsage == key;
        _previousUsage = key;

        if (input < 0 || cached < 0 || output < 0 || write < 0 || cached > input || write > input)
        {
            return new ParsedLine(null, null, DiagnosticCodes.UsageInvalid);
        }

        if (input == 0 && cached == 0 && output == 0)
        {
            return new ParsedLine(null, null, DiagnosticCodes.AllZeroUsage);
        }

        if (duplicate)
        {
            return new ParsedLine(null, null, DiagnosticCodes.DuplicateUsage);
        }

        TokenUsageSnapshot usage = new(at.Value, threadId, _rootSessionId, _turnId, _model, input, cached, write, output,
                                       reasoning, _path, ordinal ?? byteStart, _kind, _parentThreadId, _cliVersion);

        return new ParsedLine(usage, null, null);
    }

    private bool IsInherited(long? ordinal, DateTimeOffset? at)
    {
        if (ordinal is { } position && _boundaryOrdinal is { } boundary)
        {
            return position < boundary;
        }

        return ordinal is null && at < _ownMetaAtUtc;
    }

    private TotalKey? TotalOf(JsonElement info)
    {
        return info.TryGetProperty(_schema.TotalTokenUsage, out JsonElement total) && total.ValueKind == JsonValueKind.Object
            ? new TotalKey(Number(total, _schema.InputTokens), Number(total, _schema.CachedInputTokens),
                           Number(total, _schema.OutputTokens), Number(total, _schema.TotalTokens))
            : null;
    }

    private readonly record struct TotalKey(long? Input, long? Cached, long? Output, long? Total);

    private readonly record struct UsageKey(long Input, long Cached, long? Write, long Output, TotalKey? Total);
}
